using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;
using ParaDesk.Core;

namespace ParaDesk.Recording
{
    internal enum RecorderState { Idle, Starting, Recording, Stopping }

    internal class RecordingStoppedEventArgs : EventArgs
    {
        public string FilePath { get; private set; }
        public string Error { get; private set; }
        public TimeSpan Duration { get; private set; }
        public string Warning { get; set; }
        public RecordingStoppedEventArgs(string path, string error, TimeSpan dur)
        {
            FilePath = path; Error = error; Duration = dur;
        }
    }

    internal class ScreenRecorder : IDisposable
    {
        private const int FramePoolBuffers = 5;

        private static readonly DirectXPixelFormat CaptureFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

        /// <summary>每次交付的音频块时长。20ms 是编码器友好的粒度。</summary>
        private static readonly TimeSpan AudioChunk = TimeSpan.FromMilliseconds(20);

        private static readonly TimeSpan StartingFirstFrameWait = TimeSpan.FromMilliseconds(960);
        private const int StartingPollMs = 8;

        private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(6);
        private const int FirstFramePollMs = 10;

        private const int PacingSleepMaxMs = 15;

        private const int PausePollMs = 30;

        private static readonly TimeSpan AudioLeadLimit = TimeSpan.FromMilliseconds(200);
        private const int AudioLeadSleepMaxMs = 100;

        private const uint AacBitrate = 192000;

        private const int MaxConsecutiveGrabFailures = 50;

        private const long MinFreeBytesToStart = 500L * 1024 * 1024;

        private static readonly TimeSpan DisposeFinishTimeout = TimeSpan.FromSeconds(10);

        private readonly object _sync = new object();

        private IDirect3DDevice _device;
        private IntPtr _nativeDevice, _nativeContext;
        private GraphicsCaptureItem _item;
        private MediaStreamSource _mss;
        private VideoStreamDescriptor _videoDescriptor;
        private AudioStreamDescriptor _audioDescriptor;
        private AudioMixer _audio;
        private IRandomAccessStream _stream;

        private RecordingOptions _options;

        private SizeInt32 _size;        // 交给编码器的输出尺寸，一经确定不再变
        private volatile RecorderState _state = RecorderState.Idle;

        private PoolSlot _current;
        private readonly List<PoolSlot> _retired = new List<PoolSlot>();
        private readonly List<PoolSlot> _closable = new List<PoolSlot>();
        private bool _closeScheduled;
        private readonly Dictionary<Direct3D11CaptureFrame, PoolSlot> _owner =
            new Dictionary<Direct3D11CaptureFrame, PoolSlot>();

        /// <summary>
        /// 墙钟。输出时间轴直接等于真实流逝时间，避免录出来忽快忽慢。
        /// 暂停时停表——这样暂停的那段时间不会计入视频，恢复后画面直接接上。
        /// </summary>
        private System.Diagnostics.Stopwatch _clock;
        private TimeSpan _nextSampleTime;
        private TimeSpan _frameInterval;
        private volatile bool _paused;
        private string _outputPath;
        private DateTime _startedAt;
        private CancellationTokenSource _cts;

        /// <summary>
        /// 最近一帧。刻意「保留」而不是取走即弃：WGC 只在画面变化时送帧，
        /// 屏幕静止时必须靠重复这一帧把时间轴推下去，否则录制会被误判为流结束。
        /// </summary>
        private Direct3D11CaptureFrame _lastFrame;

        /// <summary>
        /// 仍被编码器持有的帧及其引用数。
        ///
        /// 这是本文件最容易出错的地方：MediaStreamSample.CreateFromDirect3D11Surface
        /// **不复制**，只引用那块纹理。而 Dispose 一个 Direct3D11CaptureFrame 会把纹理
        /// 还给帧池，WGC 随即把新画面覆盖上去——编码器于是编到了错误的内容。
        /// 所以只要还有样本引用某一帧，就绝不能释放它；等样本的 Processed 事件到了再放。
        /// 静止画面会重复使用同一帧，因此必须计数而不是布尔标记。
        ///
        /// </summary>
        private readonly Dictionary<Direct3D11CaptureFrame, int> _inFlight =
            new Dictionary<Direct3D11CaptureFrame, int>();

        private bool _used;
        private bool _pipelineStarted;
        private bool _startFailed;
        private bool _framesClosed;
        private bool _finished;
        private readonly ManualResetEventSlim _finishedEvent = new ManualResetEventSlim(false);

        private string _pendingError;
        private readonly List<string> _warnings = new List<string>();
        private int _audioFailedRaised;
        private int _grabFailures;

        public RecorderState State { get { return _state; } }
        public string OutputPath { get { return _outputPath; } }
        public DateTime StartedAt { get { return _startedAt; } }

        /// <summary>音频若不可用，这里说明原因（录制仍会继续，只是无声）。</summary>
        public string AudioWarning { get; private set; }

        /// <summary>
        /// 本次录制是否真的带上了声音。
        /// 用锁存值而非实时查询 _audio：清理阶段会把它置空，
        /// 而调用方往往在录制结束后才来读这个状态。
        /// </summary>
        public bool HasAudio { get; private set; }

        /// <summary>是否处于暂停状态。</summary>
        public bool IsPaused { get { return _paused; } }

        private bool IsCancelled
        {
            get
            {
                var cts = _cts;
                return cts == null || cts.IsCancellationRequested;
            }
        }

        /// <summary>
        /// 暂停 / 恢复。停表意味着暂停期间不产生样本，也不计入视频时长，
        /// 恢复后画面直接接上——比录一段静止画面再后期剪掉有用得多。
        /// </summary>
        public void SetPaused(bool paused)
        {
            AudioMixer audio = null;
            lock (_sync)
            {
                if (_state != RecorderState.Recording || _paused == paused) return;
                _paused = paused;
                var clock = _clock;
                if (clock != null)
                {
                    if (paused) clock.Stop();
                    else clock.Start();
                }
                if (!paused) audio = _audio;
            }
            if (audio != null) audio.DiscardBuffered();
            Log.Info(paused ? "录制已暂停" : "录制已恢复");
        }

        public event EventHandler<RecordingStoppedEventArgs> Stopped;

        public event Action<string> AudioFailed;

        public static long GetFreeBytes(string folder)
        {
            return CaptureHelpers.GetFreeBytes(folder);
        }

        public string Start(CaptureTarget target, RecordingOptions options)
        {
            lock (_sync)
            {
                if (_state != RecorderState.Idle) return L.T("已经在录制中。");
                if (_used) return L.T("该录制器已经用过一次，请新建一个再录制。");
                if (target == null) return L.T("未选择录制目标。");
                if (!CaptureItemFactory.IsSupported)
                    return L.T("当前系统不支持屏幕捕获（需要 Windows 10 1903 或更高版本）。");

                var opts = options != null ? options.Clone() : RecordingOptions.CreateDefault();
                opts.Normalize();

                long free = CaptureHelpers.GetFreeBytes(opts.OutputFolder);
                if (free >= 0 && free < MinFreeBytesToStart)
                {
                    Log.Warn("录制目录所在磁盘剩余 " + free + " 字节，拒绝开始录制");
                    return string.Format(L.T("录制目录所在磁盘只剩 {0}（至少需要 {1}），无法开始录制。"),
                        CaptureHelpers.FormatBytes(free), CaptureHelpers.FormatBytes(MinFreeBytesToStart));
                }

                _options = opts;
                _used = true;
                _cts = new CancellationTokenSource();
                _state = RecorderState.Starting;
            }

            try
            {
                _item = target.Kind == CaptureTargetKind.Window
                    ? CaptureItemFactory.CreateForWindow(target.Handle)
                    : CaptureItemFactory.CreateForMonitor(target.Handle);

                if (_item == null) return FailStart(L.T("无法捕获该目标（窗口可能已关闭）。"));

                _device = Direct3DHelper.CreateDevice(out _nativeDevice, out _nativeContext);
                if (_device == null) return FailStart(L.T("显卡设备初始化失败，无法录制。"));

                // 编码器要求宽高为偶数
                _size = CaptureHelpers.EvenSize(_item.Size.Width, _item.Size.Height);
                if (!CaptureHelpers.IsUsableSize(_size)) return FailStart(L.T("目标尺寸无效。"));

                var slot = CreatePoolSlot(_size);
                lock (_sync) _current = slot;

                _item.Closed += OnItemClosed;

                _outputPath = BuildOutputPath(_options, target);
                Directory.CreateDirectory(Path.GetDirectoryName(_outputPath));

                _startedAt = DateTime.Now;

                int fps = _options.FrameRate;
                _frameInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / fps);
                _nextSampleTime = TimeSpan.Zero;
                _clock = System.Diagnostics.Stopwatch.StartNew();

                // 音频要在编码管线建立之前就开始采，否则视频开头会缺声音
                if (_options.Audio != AudioSource.None)
                {
                    var mixer = new AudioMixer();
                    string audioErr = mixer.Start(_options.Audio);
                    if (audioErr != null)
                    {
                        // 音频失败不阻断录制——静音的视频总好过什么都没有
                        Log.Warn("音频不可用，改为无声录制：" + audioErr);
                        CaptureHelpers.SafeDispose(mixer, "释放音频采集");
                        AudioWarning = audioErr;
                    }
                    else
                    {
                        lock (_sync) _audio = mixer;
                    }
                }
                HasAudio = _audio != null && _audio.Enabled;

                string earlyStop = null;
                lock (_sync)
                {
                    if (_state != RecorderState.Starting)
                        earlyStop = _pendingError ?? L.T("无法捕获该目标（窗口可能已关闭）。");
                }
                if (earlyStop != null)
                {
                    Log.Info("启动途中录制已被叫停（多半是目标已关闭），放弃开始录制");
                    return FailStart(earlyStop);
                }

                BuildPipelineAndRun();

                try { slot.Session.StartCapture(); }
                catch (Exception ex)
                {
                    bool stopped;
                    lock (_sync) stopped = _state != RecorderState.Starting;
                    if (!stopped) throw;
                    Log.Info("启动途中录制已被叫停（多半是目标已关闭），捕获会话已停止: " + ex.Message);
                    return FailStart(L.T("无法捕获该目标（窗口可能已关闭）。"));
                }

                bool stoppedMeanwhile;
                lock (_sync)
                {
                    if (_state == RecorderState.Starting) _state = RecorderState.Recording;
                    stoppedMeanwhile = _state != RecorderState.Recording;
                }
                if (stoppedMeanwhile) StopCore();

                Log.Info("开始录制 -> " + _outputPath + "  " + _size.Width + "×" + _size.Height + "  " + fps + "fps");
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("启动录制失败", ex);
                return FailStart(L.T("启动录制失败：") + ex.Message);
            }
        }

        private string FailStart(string error)
        {
            bool pipeline;
            lock (_sync)
            {
                _startFailed = true;
                pipeline = _pipelineStarted;
                if (!_finished) _state = pipeline ? RecorderState.Stopping : RecorderState.Idle;
            }
            if (pipeline) StopCore();
            else CleanUp();
            return error;
        }

        private PoolSlot CreatePoolSlot(SizeInt32 size)
        {
            var slot = new PoolSlot { Size = size };
            try
            {
                slot.Pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _device, CaptureFormat, FramePoolBuffers, size);
                slot.Handler = delegate(Direct3D11CaptureFramePool sender, object args) { OnFrameArrived(slot, sender); };
                slot.Pool.FrameArrived += slot.Handler;
                slot.Session = slot.Pool.CreateCaptureSession(_item);
                CaptureHelpers.ConfigureSession(slot.Session, _options.CaptureCursor);
                return slot;
            }
            catch
            {
                slot.Close();
                throw;
            }
        }

        private void BuildPipelineAndRun()
        {
            var opts = _options;
            CreateMediaStreamSource();

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Video.Width = (uint)_size.Width;
            profile.Video.Height = (uint)_size.Height;
            profile.Video.Bitrate = (uint)opts.BitrateBps;
            profile.Video.FrameRate.Numerator = (uint)opts.FrameRate;
            profile.Video.FrameRate.Denominator = 1;

            profile.Audio = HasAudio
                ? AudioEncodingProperties.CreateAac(
                    (uint)AudioMixer.SampleRate, (uint)AudioMixer.Channels, AacBitrate)
                : null;

            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            string path = _outputPath;

            lock (_sync) _pipelineStarted = true;
            Task.Run(async delegate
            {
                string error = null;
                bool created = false;
                try
                {
                    var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path));
                    var file = await folder.CreateFileAsync(Path.GetFileName(path),
                        CreationCollisionOption.ReplaceExisting);
                    created = true;
                    _stream = await file.OpenAsync(FileAccessMode.ReadWrite);

                    var prep = await transcoder.PrepareMediaStreamSourceTranscodeAsync(
                        _mss, _stream, profile);
                    if (!prep.CanTranscode && transcoder.HardwareAccelerationEnabled)
                    {
                        Log.Warn("硬件编码器不接受该配置（" + prep.FailureReason + "），关闭硬件加速重试一次");
                        transcoder.HardwareAccelerationEnabled = false;
                        PrepareRetry();
                        prep = await transcoder.PrepareMediaStreamSourceTranscodeAsync(
                            _mss, _stream, profile);
                        if (prep.CanTranscode)
                        {
                            Log.Info("已改用软件编码");
                            AddWarning(L.T("硬件编码器不接受该配置，已改用软件编码"));
                        }
                    }

                    if (!prep.CanTranscode)
                        error = L.T("编码器不接受该配置：") + prep.FailureReason;
                    else
                        await prep.TranscodeAsync();   // 一直阻塞到 MediaStreamSource 结束（我们在停止时通知它）
                }
                catch (Exception ex)
                {
                    Log.Error("录制管线异常", ex);
                    error = L.T("录制失败：") + ex.Message;
                }
                Finish(error, created);
            });
        }

        private void CreateMediaStreamSource()
        {
            var videoProps = VideoEncodingProperties.CreateUncompressed(
                MediaEncodingSubtypes.Bgra8, (uint)_size.Width, (uint)_size.Height);
            var video = new VideoStreamDescriptor(videoProps);

            AudioStreamDescriptor audio = null;
            MediaStreamSource mss;
            if (HasAudio)
            {
                var audioProps = AudioEncodingProperties.CreatePcm(
                    (uint)AudioMixer.SampleRate, (uint)AudioMixer.Channels,
                    (uint)AudioMixer.BitsPerSample);
                audio = new AudioStreamDescriptor(audioProps);
                mss = new MediaStreamSource(video, audio);
            }
            else
            {
                mss = new MediaStreamSource(video);
            }

            mss.BufferTime = TimeSpan.Zero;   // 实时源，不缓冲
            mss.Starting += OnMssStarting;
            mss.SampleRequested += OnMssSampleRequested;

            _videoDescriptor = video;
            _audioDescriptor = audio;
            _mss = mss;
        }

        private void DetachMediaStreamSource()
        {
            var mss = _mss;
            if (mss == null) return;
            CaptureHelpers.SafeRun(delegate
            {
                mss.Starting -= OnMssStarting;
                mss.SampleRequested -= OnMssSampleRequested;
            }, "解除 MediaStreamSource 事件");
        }

        private void PrepareRetry()
        {
            DetachMediaStreamSource();
            CreateMediaStreamSource();
            var stream = _stream;
            if (stream != null)
            {
                stream.Size = 0;
                stream.Seek(0);
            }
        }

        private void OnFrameArrived(PoolSlot slot, Direct3D11CaptureFramePool sender)
        {
            Direct3D11CaptureFrame frame;
            try { frame = sender.TryGetNextFrame(); }
            catch (Exception ex)
            {
                OnGrabFailed(slot, ex);
                return;
            }
            if (frame == null) return;
            Interlocked.Exchange(ref _grabFailures, 0);

            SizeInt32 content = slot.Size;
            try { content = frame.ContentSize; }
            catch (Exception ex) { Log.Debug("读取帧内容尺寸失败: " + ex.Message); }

            bool accepted = false, resize = false;
            SizeInt32 want = slot.Size;
            lock (_sync)
            {
                if (!_framesClosed && !slot.Retired &&
                    (_state == RecorderState.Recording || _state == RecorderState.Starting))
                {
                    _owner[frame] = slot;
                    slot.Outstanding++;
                    var old = _lastFrame;
                    _lastFrame = frame;
                    DropLastFrameHoldLocked(old);
                    accepted = true;

                    if (content.Width > 0 && content.Height > 0 &&
                        ReferenceEquals(slot, _current) && !slot.SwitchPending)
                    {
                        want = CaptureHelpers.EvenPoolSize(content);
                        if (!CaptureHelpers.SameSize(want, slot.Size) &&
                            !CaptureHelpers.SameSize(want, slot.FailedResize))
                        {
                            slot.SwitchPending = true;
                            resize = true;
                        }
                    }
                }
            }

            if (!accepted) CaptureHelpers.SafeDispose(frame, "归还多余的捕获帧");

            ScheduleClosePools();

            if (resize) ThreadPool.QueueUserWorkItem(delegate { SwitchPool(slot, want); });
        }

        private void OnGrabFailed(PoolSlot slot, Exception ex)
        {
            if (slot.Retired || _state != RecorderState.Recording)
            {
                Log.Debug("取帧失败（旧帧池或录制已在收尾）: " + ex.Message);
                return;
            }

            int n = Interlocked.Increment(ref _grabFailures);
            if (n < MaxConsecutiveGrabFailures)
            {
                Log.Debug("取帧失败 #" + n + ": " + ex.Message);
                return;
            }
            if (n > MaxConsecutiveGrabFailures) return;

            Log.Warn("连续 " + n + " 次取帧失败，判定显卡设备丢失或捕获目标失效，结束录制: " + ex.Message);
            ThreadPool.QueueUserWorkItem(delegate
            {
                if (_state != RecorderState.Recording) return;
                EndWithError(L.T("录制中断：连续无法获取画面（显卡设备可能已丢失）。"));
            });
        }

        private void SwitchPool(PoolSlot from, SizeInt32 size)
        {
            lock (_sync)
            {
                if (_framesClosed || !ReferenceEquals(_current, from) ||
                    (_state != RecorderState.Recording && _state != RecorderState.Starting))
                {
                    from.SwitchPending = false;
                    return;
                }
            }

            PoolSlot slot;
            try { slot = CreatePoolSlot(size); }
            catch (Exception ex)
            {
                Log.Warn("捕获目标尺寸变化，但新建 " + size.Width + "×" + size.Height + " 帧池失败，继续用旧帧池: " + ex.Message);
                lock (_sync)
                {
                    from.FailedResize = size;
                    from.SwitchPending = false;
                }
                return;
            }

            bool installed = false;
            lock (_sync)
            {
                if (!_framesClosed && ReferenceEquals(_current, from) &&
                    (_state == RecorderState.Recording || _state == RecorderState.Starting))
                {
                    from.Retired = true;
                    _current = slot;
                    if (from.Outstanding <= 0) { from.Queued = true; _closable.Add(from); }
                    else _retired.Add(from);
                    installed = true;
                }
                else from.SwitchPending = false;
            }
            if (!installed)
            {
                slot.Close();
                return;
            }

            from.StopSession();
            try
            {
                slot.Session.StartCapture();
                Log.Info("捕获目标尺寸变化，已换用 " + size.Width + "×" + size.Height +
                         " 的新帧池（旧帧池待其帧全部归还后释放）");
            }
            catch (Exception ex)
            {
                if (_state == RecorderState.Recording)
                {
                    Log.Warn("新帧池启动捕获失败: " + ex.Message);
                    EndWithError(L.T("录制中断：目标尺寸变化后无法继续捕获画面。"));
                }
                else Log.Debug("新帧池启动捕获失败（录制已在收尾）: " + ex.Message);
            }
            ScheduleClosePools();
        }

        private void AddRefFrameLocked(Direct3D11CaptureFrame frame)
        {
            if (frame == null) return;
            int n;
            _inFlight[frame] = _inFlight.TryGetValue(frame, out n) ? n + 1 : 1;
        }

        private void DropLastFrameHoldLocked(Direct3D11CaptureFrame old)
        {
            if (old == null) return;
            if (_inFlight.ContainsKey(old)) return;
            DisposeFrameLocked(old);
        }

        private void ReleaseSampleRefLocked(Direct3D11CaptureFrame frame)
        {
            if (frame == null || _framesClosed) return;
            int n;
            if (!_inFlight.TryGetValue(frame, out n)) return;
            if (n > 1) { _inFlight[frame] = n - 1; return; }
            _inFlight.Remove(frame);

            if (ReferenceEquals(frame, _lastFrame)) return;   // 还要留着重发
            DisposeFrameLocked(frame);
        }

        private void DisposeFrameLocked(Direct3D11CaptureFrame frame)
        {
            PoolSlot slot;
            if (_owner.TryGetValue(frame, out slot))
            {
                _owner.Remove(frame);
                slot.Outstanding--;
                if (slot.Retired && slot.Outstanding <= 0 && !slot.Queued)
                {
                    slot.Queued = true;
                    _retired.Remove(slot);
                    _closable.Add(slot);
                }
            }
            CaptureHelpers.SafeDispose(frame, "归还捕获帧");
        }

        private void ScheduleClosePools()
        {
            lock (_sync)
            {
                if (_closable.Count == 0 || _closeScheduled) return;
                _closeScheduled = true;
            }
            ThreadPool.QueueUserWorkItem(delegate { ClosePendingPools(); });
        }

        private void ClosePendingPools()
        {
            List<PoolSlot> list;
            lock (_sync)
            {
                _closeScheduled = false;
                if (_closable.Count == 0) return;
                list = new List<PoolSlot>(_closable);
                _closable.Clear();
            }
            foreach (var s in list)
            {
                s.Close();
                Log.Debug("旧帧池 " + s.Size.Width + "×" + s.Size.Height + " 的帧已全部归还，已释放");
            }
        }

        private void OnMssStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            // 等首帧到达再开始计时，避免把启动耗时算进视频开头
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (waited.Elapsed < StartingFirstFrameWait)
            {
                lock (_sync) { if (_lastFrame != null) break; }
                if (IsCancelled) break;
                Thread.Sleep(StartingPollMs);
            }

            lock (_sync)
            {
                var clock = _clock;
                if (clock != null)
                {
                    clock.Reset();
                    if (!_paused && !IsCancelled) clock.Start();
                }
                _nextSampleTime = TimeSpan.Zero;
            }

            // 音频与视频必须共用这一个原点。音频采集启动得更早（否则会丢开头），
            // 那段预卷数据要在此刻丢掉，不然整条音轨会恒定超前于画面。
            var audio = _audio;
            if (audio != null) audio.ResetTimeline();

            args.Request.SetActualStartPosition(TimeSpan.Zero);
        }

        private void OnMssSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            var request = args.Request;

            if (IsCancelled)
            {
                request.Sample = null;   // null 表示流结束，Transcode 随之收尾
                return;
            }

            // 音视频共用同一个回调，靠流描述符区分
            var audioDescriptor = _audioDescriptor;
            if (audioDescriptor != null && request.StreamDescriptor == audioDescriptor)
            {
                SupplyAudio(request);
                return;
            }

            var deferral = request.GetDeferral();
            try
            {
                // 按目标帧率定速供样：等到该出这一帧的时刻再交出去。
                // 这样输出时间轴严格等于真实流逝时间，不会忽快忽慢。
                while (true)
                {
                    if (IsCancelled) break;
                    if (_paused) { Thread.Sleep(PausePollMs); continue; }
                    var clock = _clock;
                    if (clock == null) break;
                    var wait = _nextSampleTime - clock.Elapsed;
                    if (wait <= TimeSpan.Zero) break;
                    Thread.Sleep(wait > TimeSpan.FromMilliseconds(PacingSleepMaxMs)
                        ? PacingSleepMaxMs : Math.Max(1, (int)wait.TotalMilliseconds));
                }

                if (IsCancelled)
                {
                    request.Sample = null;   // 只有真正停止时才结束流
                    return;
                }

                // 等首帧到来。绝不能用 Sample = null 表示"跳过一拍"——
                // 对 MediaStreamSource 而言 null 就是流结束，录制会当场被截断。
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (waited.Elapsed < FirstFrameTimeout)
                {
                    lock (_sync) { if (_lastFrame != null) break; }
                    if (IsCancelled) break;
                    Thread.Sleep(FirstFramePollMs);
                }

                if (IsCancelled)
                {
                    request.Sample = null;
                    return;
                }

                var ts = _nextSampleTime;
                _nextSampleTime += _frameInterval;

                MediaStreamSample sample = null;
                Direct3D11CaptureFrame used = null;

                // 建样本必须在锁内：一旦离开锁，OnFrameArrived 就可能把这一帧释放掉，
                // 之后再访问 frame.Surface 会抛异常，而那个异常会被吞成 Sample = null，
                // 也就是又一次静默截断录制。
                lock (_sync)
                {
                    used = _lastFrame;
                    if (used != null && !_framesClosed)
                    {
                        sample = MediaStreamSample.CreateFromDirect3D11Surface(used.Surface, ts);
                        sample.Duration = _frameInterval;   // 不设时长编码器会按序排帧，导致画面加速
                        AddRefFrameLocked(used);
                    }
                }

                if (sample == null)
                {
                    if (IsCancelled) { request.Sample = null; return; }

                    Log.Warn("等待首帧超时（" + FirstFrameTimeout.TotalSeconds + " 秒），以错误结束录制");
                    EndWithError(L.T("录制目标没有产生画面（可能已最小化或被系统停止绘制）"));
                    request.Sample = null;
                    return;
                }

                // 编码器用完这一帧才允许把纹理还给帧池
                var frameRef = used;
                sample.Processed += delegate
                {
                    lock (_sync) { ReleaseSampleRefLocked(frameRef); }
                    ScheduleClosePools();
                };

                request.Sample = sample;
            }
            catch (Exception ex)
            {
                if (!IsCancelled)
                {
                    Log.Warn("视频供样失败，结束录制: " + ex.Message);
                    EndWithError(L.T("录制失败：") + ex.Message);
                }
                else Log.Debug("视频供样失败（已在停止）: " + ex.Message);
                request.Sample = null;
            }
            finally { deferral.Complete(); }
        }

        /// <summary>
        /// 交付音频样本。时间戳由已交付的字节数推算，因此音轨自身严格连续；
        /// 数据不足的部分由 AudioMixer 补静音，不会出现空洞导致的音画不同步。
        /// </summary>
        private void SupplyAudio(MediaStreamSourceSampleRequest request)
        {
            var deferral = request.GetDeferral();
            try
            {
                var audio = _audio;
                if (audio == null) { request.Sample = null; return; }

                // 暂停期间不取音频，否则恢复后音轨会比画面长出一截
                while (_paused && !IsCancelled) Thread.Sleep(PausePollMs);
                if (IsCancelled) { request.Sample = null; return; }

                CheckAudioHealth(audio);

                TimeSpan ts;
                byte[] pcm = audio.Read(AudioChunk, out ts);

                // 音频不能跑到视频前面太多，否则编码器要缓存大量视频帧
                var clock = _clock;
                if (clock != null)
                {
                    var ahead = ts - clock.Elapsed;
                    if (ahead > AudioLeadLimit)
                        Thread.Sleep((int)Math.Min(AudioLeadSleepMaxMs, ahead.TotalMilliseconds));
                }

                var buffer = System.Runtime.InteropServices.WindowsRuntime
                    .WindowsRuntimeBufferExtensions.AsBuffer(pcm);
                var sample = MediaStreamSample.CreateFromBuffer(buffer, ts);
                sample.Duration = AudioChunk;
                request.Sample = sample;
            }
            catch (Exception ex)
            {
                if (!IsCancelled)
                {
                    Log.Warn("音频供样失败，之后的录制没有声音: " + ex.Message);
                    ReportAudioFailure(ex.Message);
                }
                else Log.Debug("音频供样失败（已在停止）: " + ex.Message);
                request.Sample = null;
            }
            finally { deferral.Complete(); }
        }

        private void CheckAudioHealth(AudioMixer audio)
        {
            if (Thread.VolatileRead(ref _audioFailedRaised) != 0) return;
            string reason = audio.FailureReason;
            if (reason != null) ReportAudioFailure(reason);
        }

        private void ReportAudioFailure(string reason)
        {
            if (Interlocked.CompareExchange(ref _audioFailedRaised, 1, 0) != 0) return;
            Log.Warn("录制中音频采集中断，录制继续: " + reason);
            AddWarning(L.T("中途失去声音：") + reason);

            var h = AudioFailed;
            if (h == null) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { h(reason); }
                catch (Exception ex) { Log.Error("音频中断回调异常", ex); }
            });
        }

        private void AddWarning(string warning)
        {
            lock (_sync) _warnings.Add(warning);
        }

        private string BuildWarning()
        {
            lock (_sync)
            {
                if (_warnings.Count == 0) return null;
                return string.Join(L.Current == "en" ? "; " : "；", _warnings.ToArray());
            }
        }

        private void OnItemClosed(GraphicsCaptureItem sender, object args)
        {
            Log.Info("录制目标已关闭，自动停止录制");
            Stop();
        }

        /// <summary>停止录制。文件在编码收尾后才可用，完成时触发 Stopped。</summary>
        public void Stop()
        {
            lock (_sync)
            {
                if (_state != RecorderState.Recording && _state != RecorderState.Starting) return;
                _state = RecorderState.Stopping;
            }
            StopCore();
            Log.Info("已请求停止录制");
        }

        private void StopCore()
        {
            var cts = _cts;
            if (cts != null) CaptureHelpers.SafeRun(delegate { cts.Cancel(); }, "取消录制");

            PoolSlot slot;
            AudioMixer audio;
            lock (_sync)
            {
                var clock = _clock;
                if (clock != null) clock.Stop();
                slot = _current;
                audio = _audio;
            }
            if (audio != null) CaptureHelpers.SafeRun(audio.Stop, "停止音频采集");
            if (slot != null) slot.StopSession();
        }

        private void EndWithError(string error)
        {
            lock (_sync)
            {
                if (_pendingError == null) _pendingError = error;
            }
            Stop();
        }

        private void Finish(string error, bool outputCreated)
        {
            lock (_sync)
            {
                if (_finished) return;
                _finished = true;
                if (_pendingError != null) error = _pendingError;
                if (_state == RecorderState.Recording || _state == RecorderState.Starting)
                    _state = RecorderState.Stopping;
            }

            TimeSpan dur = TimeSpan.Zero;
            string path = _outputPath;
            string warning = null;
            try
            {
                StopCore();

                var clock = _clock;
                if (clock != null) dur = clock.Elapsed;
                warning = BuildWarning();

                CleanUp();
            }
            catch (Exception ex) { Log.Error("录制收尾异常", ex); }
            finally
            {
                lock (_sync) _state = RecorderState.Idle;
                _finishedEvent.Set();
            }

            if (error == null)
                Log.Info("录制完成 " + path + "  时长 " + dur.ToString(@"hh\:mm\:ss") +
                         (warning != null ? "  提示: " + warning : ""));
            else Log.Warn("录制结束（有错误）: " + error);

            bool startFailed;
            lock (_sync) startFailed = _startFailed;
            if (startFailed)
            {
                if (outputCreated && !string.IsNullOrEmpty(path))
                {
                    try { File.Delete(path); }
                    catch (Exception ex) { Log.Debug("删除启动失败留下的空录像失败: " + ex.Message); }
                }
                Log.Debug("该录制器启动时已报错，不再触发 Stopped");
                return;
            }

            var h = Stopped;
            if (h != null)
            {
                var e = new RecordingStoppedEventArgs(path, error, dur);
                if (error == null) e.Warning = warning;
                try { h(this, e); }
                catch (Exception ex) { Log.Error("录制完成回调异常", ex); }
            }
        }

        private readonly object _cleanupSync = new object();
        private bool _cleanedUp;

        private void CleanUp()
        {
            // 停止与结束两条路径可能并发进来；原生 COM 指针只能释放一次，
            // 重复 Release 会破坏引用计数并可能崩溃。
            lock (_cleanupSync)
            {
                if (_cleanedUp) return;
                _cleanedUp = true;
            }
            CleanUpCore();
        }

        private void CleanUpCore()
        {
            var frames = new List<Direct3D11CaptureFrame>();
            var slots = new List<PoolSlot>();
            lock (_sync)
            {
                _framesClosed = true;
                var all = new HashSet<Direct3D11CaptureFrame>(_owner.Keys);
                if (_lastFrame != null) all.Add(_lastFrame);
                foreach (var f in _inFlight.Keys) all.Add(f);
                frames.AddRange(all);
                _owner.Clear();
                _inFlight.Clear();
                _lastFrame = null;

                if (_current != null) slots.Add(_current);
                slots.AddRange(_retired);
                slots.AddRange(_closable);
                _current = null;
                _retired.Clear();
                _closable.Clear();
            }
            foreach (var f in frames) CaptureHelpers.SafeDispose(f, "释放捕获帧");

            foreach (var s in slots) s.Close();

            lock (_sync)
            {
                if (_clock != null) { _clock.Stop(); _clock = null; }
            }

            var audio = _audio;
            _audio = null;
            CaptureHelpers.SafeDispose(audio, "释放音频采集");

            DetachMediaStreamSource();
            _mss = null;
            _videoDescriptor = null;
            _audioDescriptor = null;

            var stream = _stream;
            _stream = null;
            CaptureHelpers.SafeDispose(stream, "关闭输出文件");

            var item = _item;
            _item = null;
            if (item != null) CaptureHelpers.SafeRun(delegate { item.Closed -= OnItemClosed; }, "解除目标关闭事件");

            _device = null;
            CaptureHelpers.ReleaseNative(ref _nativeContext, " D3D11 上下文");
            CaptureHelpers.ReleaseNative(ref _nativeDevice, " D3D11 设备");

            var cts = _cts;
            _cts = null;
            CaptureHelpers.SafeDispose(cts, "释放取消令牌");
            // 注意：不要在这里复位 _finished。它是一次性录制的终态标记，
            // 复位会让 Finish 有机会重复触发 Stopped 事件。
        }

        private static string BuildOutputPath(RecordingOptions options, CaptureTarget target)
        {
            string dir = string.IsNullOrEmpty(options.OutputFolder)
                ? RecordingOptions.DefaultFolder
                : options.OutputFolder;
            dir = Path.GetFullPath(dir);

            // 键不能用"录制"——那个词同时是导航项，两处英文不同，字典里会互相覆盖
            string safe = CaptureHelpers.SafeFileStem(target.Title, L.T("未命名录制"));

            string name = string.Format("{0}_{1:yyyyMMdd_HHmmss}.mp4", safe, DateTime.Now);
            return CaptureHelpers.UniquePath(Path.Combine(dir, name));
        }

        public void Dispose()
        {
            Stop();
            bool wait;
            lock (_sync) wait = _pipelineStarted && !_finished;
            if (wait && !_finishedEvent.Wait(DisposeFinishTimeout))
                Log.Warn("录制在 " + DisposeFinishTimeout.TotalSeconds + " 秒内未能收尾，强制释放资源（文件可能不完整）");
            CleanUp();
        }

        private sealed class PoolSlot
        {
            public Direct3D11CaptureFramePool Pool;
            public GraphicsCaptureSession Session;
            public TypedEventHandler<Direct3D11CaptureFramePool, object> Handler;
            public SizeInt32 Size;

            public int Outstanding;
            public volatile bool Retired;
            public bool Queued;
            public bool SwitchPending;
            public SizeInt32 FailedResize;

            private int _sessionStopped, _closed;

            public void StopSession()
            {
                if (Interlocked.Exchange(ref _sessionStopped, 1) != 0) return;
                CaptureHelpers.SafeDispose(Session, "停止捕获会话");
            }

            public void Close()
            {
                if (Interlocked.Exchange(ref _closed, 1) != 0) return;
                StopSession();
                var pool = Pool;
                var handler = Handler;
                if (pool != null && handler != null)
                    CaptureHelpers.SafeRun(delegate { pool.FrameArrived -= handler; }, "解除帧到达事件");
                CaptureHelpers.SafeDispose(pool, "释放帧池");
            }
        }
    }
}
