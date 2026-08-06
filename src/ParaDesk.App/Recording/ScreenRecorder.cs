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
        public RecordingStoppedEventArgs(string path, string error, TimeSpan dur)
        {
            FilePath = path; Error = error; Duration = dur;
        }
    }

    /// <summary>
    /// 屏幕/窗口录制器。
    ///
    /// 管线：GraphicsCaptureItem → Direct3D11CaptureFramePool（帧到达）
    ///       → MediaStreamSource（按需供样）→ MediaTranscoder → MP4(H.264)
    ///
    /// 选择这条 WinRT 全链路而不是 Media Foundation 手写互操作：后者光是
    /// IMFAttributes/IMFMediaType/IMFSample 就要重复声明上百个方法（C# 不继承
    /// COM 虚表布局），而 WinRT 在 .NET Framework 上有 CLR 自带投影，代码量差一个量级。
    /// </summary>
    internal class ScreenRecorder : IDisposable
    {
        private readonly object _sync = new object();

        private IDirect3DDevice _device;
        private IntPtr _nativeDevice, _nativeContext;
        private GraphicsCaptureItem _item;
        private Direct3D11CaptureFramePool _pool;
        private GraphicsCaptureSession _session;
        private MediaStreamSource _mss;
        private VideoStreamDescriptor _videoDescriptor;
        private AudioStreamDescriptor _audioDescriptor;
        private AudioMixer _audio;
        private IRandomAccessStream _stream;

        /// <summary>每次交付的音频块时长。20ms 是编码器友好的粒度。</summary>
        private static readonly TimeSpan AudioChunk = TimeSpan.FromMilliseconds(20);

        private SizeInt32 _size;        // 交给编码器的输出尺寸，一经确定不再变
        private SizeInt32 _poolSize;    // 帧池尺寸，跟随目标窗口变化
        private RecorderState _state = RecorderState.Idle;

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
        /// </summary>
        private readonly Dictionary<Direct3D11CaptureFrame, int> _inFlight =
            new Dictionary<Direct3D11CaptureFrame, int>();

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

        /// <summary>
        /// 暂停 / 恢复。停表意味着暂停期间不产生样本，也不计入视频时长，
        /// 恢复后画面直接接上——比录一段静止画面再后期剪掉有用得多。
        /// </summary>
        public void SetPaused(bool paused)
        {
            if (_state != RecorderState.Recording || _paused == paused) return;
            _paused = paused;
            try
            {
                if (_clock == null) return;
                if (paused) _clock.Stop();
                else _clock.Start();
            }
            catch { }
            Log.Info(paused ? "录制已暂停" : "录制已恢复");
        }

        public event EventHandler<RecordingStoppedEventArgs> Stopped;

        /// <summary>开始录制。返回 null 表示已开始，否则为错误说明。</summary>
        public string Start(CaptureTarget target, RecordingOptions options)
        {
            lock (_sync)
            {
                if (_state != RecorderState.Idle) return L.T("已经在录制中。");
                if (target == null) return L.T("未选择录制目标。");
                if (!CaptureItemFactory.IsSupported)
                    return L.T("当前系统不支持屏幕捕获（需要 Windows 10 1903 或更高版本）。");

                _state = RecorderState.Starting;
            }

            try
            {
                _item = target.Kind == CaptureTargetKind.Window
                    ? CaptureItemFactory.CreateForWindow(target.Handle)
                    : CaptureItemFactory.CreateForMonitor(target.Handle);

                if (_item == null)
                {
                    _state = RecorderState.Idle;
                    return L.T("无法捕获该目标（窗口可能已关闭）。");
                }

                _device = Direct3DHelper.CreateDevice(out _nativeDevice, out _nativeContext);
                if (_device == null)
                {
                    _state = RecorderState.Idle;
                    return L.T("显卡设备初始化失败，无法录制。");
                }

                // 编码器要求宽高为偶数
                _size = new SizeInt32
                {
                    Width = _item.Size.Width - (_item.Size.Width % 2),
                    Height = _item.Size.Height - (_item.Size.Height % 2),
                };
                if (_size.Width < 2 || _size.Height < 2)
                {
                    _state = RecorderState.Idle;
                    return L.T("目标尺寸无效。");
                }

                // 5 个缓冲：我们要保留最近一帧用于静止时重发，还要留出编码器
                // 尚未处理完的在途帧的余量；缓冲不足会导致 WGC 停止送新帧。
                _poolSize = _size;
                _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 5, _poolSize);
                _pool.FrameArrived += OnFrameArrived;

                _session = _pool.CreateCaptureSession(_item);
                TryConfigureSession(options);

                _item.Closed += OnItemClosed;

                _outputPath = BuildOutputPath(options, target);
                Directory.CreateDirectory(Path.GetDirectoryName(_outputPath));

                _startedAt = DateTime.Now;
                _cts = new CancellationTokenSource();

                int fps = options.FrameRate < 5 ? 30 : options.FrameRate;
                _frameInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / fps);
                _nextSampleTime = TimeSpan.Zero;
                _clock = System.Diagnostics.Stopwatch.StartNew();

                // 音频要在编码管线建立之前就开始采，否则视频开头会缺声音
                if (options.Audio != AudioSource.None)
                {
                    _audio = new AudioMixer();
                    string audioErr = _audio.Start(options.Audio);
                    if (audioErr != null)
                    {
                        // 音频失败不阻断录制——静音的视频总好过什么都没有
                        Log.Warn("音频不可用，改为无声录制：" + audioErr);
                        _audio.Dispose();
                        _audio = null;
                        AudioWarning = audioErr;
                    }
                }
                HasAudio = _audio != null && _audio.Enabled;

                BuildPipelineAndRun(options);

                _session.StartCapture();
                _state = RecorderState.Recording;
                Log.Info("开始录制 -> " + _outputPath + "  " + _size.Width + "×" + _size.Height);
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("启动录制失败", ex);
                // 失败发生在编码管线起来之前，此时直接清理是安全的；
                // 若管线已启动，让它自己走 Finish 收尾，这里只标记取消。
                try { if (_cts != null) _cts.Cancel(); } catch { }
                CleanUp();
                _state = RecorderState.Idle;
                return L.T("启动录制失败：") + ex.Message;
            }
        }

        private void TryConfigureSession(RecordingOptions options)
        {
            // 光标是否入镜
            try { _session.IsCursorCaptureEnabled = options.CaptureCursor; }
            catch (Exception ex) { Log.Debug("设置光标捕获失败: " + ex.Message); }

            // 黄色捕获边框只有 Win11 能关；Win10 上属于系统行为，无法消除
            if (CaptureItemFactory.CanHideBorder)
            {
                try { _session.IsBorderRequired = false; }
                catch (Exception ex) { Log.Debug("隐藏捕获边框失败: " + ex.Message); }
            }
        }

        private void BuildPipelineAndRun(RecordingOptions options)
        {
            var videoProps = VideoEncodingProperties.CreateUncompressed(
                MediaEncodingSubtypes.Bgra8, (uint)_size.Width, (uint)_size.Height);
            _videoDescriptor = new VideoStreamDescriptor(videoProps);

            if (_audio != null && _audio.Enabled)
            {
                var audioProps = AudioEncodingProperties.CreatePcm(
                    (uint)AudioMixer.SampleRate, (uint)AudioMixer.Channels,
                    (uint)AudioMixer.BitsPerSample);
                _audioDescriptor = new AudioStreamDescriptor(audioProps);
                _mss = new MediaStreamSource(_videoDescriptor, _audioDescriptor);
            }
            else
            {
                _mss = new MediaStreamSource(_videoDescriptor);
            }

            _mss.BufferTime = TimeSpan.Zero;   // 实时源，不缓冲
            _mss.Starting += OnMssStarting;
            _mss.SampleRequested += OnMssSampleRequested;

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Video.Width = (uint)_size.Width;
            profile.Video.Height = (uint)_size.Height;
            profile.Video.Bitrate = (uint)options.BitrateBps;
            profile.Video.FrameRate.Numerator = (uint)options.FrameRate;
            profile.Video.FrameRate.Denominator = 1;

            profile.Audio = (_audio != null && _audio.Enabled)
                ? AudioEncodingProperties.CreateAac(
                    (uint)AudioMixer.SampleRate, (uint)AudioMixer.Channels, 192000)
                : null;

            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            string path = _outputPath;

            Task.Run(async delegate
            {
                try
                {
                    var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path));
                    var file = await folder.CreateFileAsync(Path.GetFileName(path),
                        CreationCollisionOption.ReplaceExisting);
                    _stream = await file.OpenAsync(FileAccessMode.ReadWrite);

                    var prep = await transcoder.PrepareMediaStreamSourceTranscodeAsync(
                        _mss, _stream, profile);
                    if (!prep.CanTranscode)
                    {
                        Finish(L.T("编码器不接受该配置：") + prep.FailureReason);
                        return;
                    }
                    // 一直阻塞到 MediaStreamSource 结束（我们在停止时通知它）
                    await prep.TranscodeAsync();
                    Finish(null);
                }
                catch (Exception ex)
                {
                    Log.Error("录制管线异常", ex);
                    Finish(L.T("录制失败：") + ex.Message);
                }
            });
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            try
            {
                var frame = sender.TryGetNextFrame();
                if (frame == null) return;

                // 目标窗口改了大小：帧池仍按旧尺寸分配，新内容只占纹理的一角，
                // 四周残留旧像素。重建帧池让它跟上，但**不改**已声明给编码器的
                // 输出尺寸——那是不能中途变的，超出部分裁掉、不足部分留黑边。
                var content = frame.ContentSize;
                if (content.Width > 0 && content.Height > 0 &&
                    (content.Width != _poolSize.Width || content.Height != _poolSize.Height))
                {
                    int w = Math.Max(2, content.Width - (content.Width % 2));
                    int h = Math.Max(2, content.Height);
                    _poolSize = new SizeInt32 { Width = w, Height = h };
                    try
                    {
                        sender.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 5, _poolSize);
                        Log.Info("捕获目标尺寸变化，帧池已重建为 " + w + "×" + h);
                    }
                    catch (Exception ex) { Log.Debug("重建帧池失败: " + ex.Message); }
                }

                lock (_sync)
                {
                    if (_state != RecorderState.Recording && _state != RecorderState.Starting)
                    {
                        frame.Dispose();
                        return;
                    }
                    // 始终持有最新一帧；旧帧只有在没人引用时才归还池子
                    var old = _lastFrame;
                    _lastFrame = frame;
                    ReleaseFrameLocked(old);
                }
            }
            catch (Exception ex) { Log.Debug("取帧失败: " + ex.Message); }
        }

        /// <summary>登记一次引用。必须在持有 _sync 时调用。</summary>
        private void AddRefFrameLocked(Direct3D11CaptureFrame frame)
        {
            if (frame == null) return;
            int n;
            _inFlight[frame] = _inFlight.TryGetValue(frame, out n) ? n + 1 : 1;
        }

        /// <summary>
        /// 释放一次引用；当它既不再是最新帧、也没有任何样本引用时才真正归还池子。
        /// 必须在持有 _sync 时调用。
        /// </summary>
        private void ReleaseFrameLocked(Direct3D11CaptureFrame frame)
        {
            if (frame == null) return;

            int n;
            if (_inFlight.TryGetValue(frame, out n))
            {
                if (n > 1) { _inFlight[frame] = n - 1; return; }
                _inFlight.Remove(frame);
            }

            if (ReferenceEquals(frame, _lastFrame)) return;   // 还要留着重发
            try { frame.Dispose(); } catch { }
        }

        private void OnMssStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            // 等首帧到达再开始计时，避免把启动耗时算进视频开头
            for (int i = 0; i < 120; i++)
            {
                lock (_sync) { if (_lastFrame != null) break; }
                Thread.Sleep(8);
            }

            // 时间轴原点固定为 0，样本时间戳也用相对值——两边必须同一套基准，
            // 之前一边用绝对系统时间、一边用相对时间，导致播放速度错乱。
            _clock.Restart();
            _nextSampleTime = TimeSpan.Zero;

            // 音频与视频必须共用这一个原点。音频采集启动得更早（否则会丢开头），
            // 那段预卷数据要在此刻丢掉，不然整条音轨会恒定超前于画面。
            if (_audio != null) _audio.ResetTimeline();

            args.Request.SetActualStartPosition(TimeSpan.Zero);
        }

        private void OnMssSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            var request = args.Request;

            if (_cts != null && _cts.IsCancellationRequested)
            {
                request.Sample = null;   // null 表示流结束，Transcode 随之收尾
                return;
            }

            // 音视频共用同一个回调，靠流描述符区分
            if (_audioDescriptor != null && request.StreamDescriptor == _audioDescriptor)
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
                    if (_cts == null || _cts.IsCancellationRequested) break;
                    // 暂停时表停了，_clock.Elapsed 不再前进，这里自然一直等下去
                    if (_paused) { Thread.Sleep(30); continue; }
                    var wait = _nextSampleTime - _clock.Elapsed;
                    if (wait <= TimeSpan.Zero) break;
                    Thread.Sleep(wait > TimeSpan.FromMilliseconds(15)
                        ? 15 : Math.Max(1, (int)wait.TotalMilliseconds));
                }

                if (_cts != null && _cts.IsCancellationRequested)
                {
                    request.Sample = null;   // 只有真正停止时才结束流
                    return;
                }

                // 等首帧到来。绝不能用 Sample = null 表示"跳过一拍"——
                // 对 MediaStreamSource 而言 null 就是流结束，录制会当场被截断。
                // 只有真正取消时才允许交 null。
                for (int i = 0; i < 600; i++)
                {
                    lock (_sync) { if (_lastFrame != null) break; }
                    if (_cts == null || _cts.IsCancellationRequested) break;
                    Thread.Sleep(10);
                }

                if (_cts != null && _cts.IsCancellationRequested)
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
                    if (used != null)
                    {
                        sample = MediaStreamSample.CreateFromDirect3D11Surface(used.Surface, ts);
                        sample.Duration = _frameInterval;   // 不设时长编码器会按序排帧，导致画面加速
                        AddRefFrameLocked(used);
                    }
                }

                if (sample == null)
                {
                    // 等了 6 秒仍无帧：目标多半已经不再绘制，正常结束而不是死等
                    Log.Warn("等待首帧超时，结束录制");
                    request.Sample = null;
                    return;
                }

                // 编码器用完这一帧才允许把纹理还给帧池
                var frameRef = used;
                sample.Processed += delegate
                {
                    lock (_sync) { ReleaseFrameLocked(frameRef); }
                };

                request.Sample = sample;
            }
            catch (Exception ex)
            {
                Log.Debug("供样失败: " + ex.Message);
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
                if (_audio == null) { request.Sample = null; return; }

                // 暂停期间不取音频，否则恢复后音轨会比画面长出一截
                while (_paused && _cts != null && !_cts.IsCancellationRequested) Thread.Sleep(30);
                if (_cts != null && _cts.IsCancellationRequested) { request.Sample = null; return; }

                TimeSpan ts;
                byte[] pcm = _audio.Read(AudioChunk, out ts);

                // 音频不能跑到视频前面太多，否则编码器要缓存大量视频帧
                var ahead = ts - _clock.Elapsed;
                if (ahead > TimeSpan.FromMilliseconds(200))
                {
                    int ms = (int)Math.Min(100, ahead.TotalMilliseconds);
                    Thread.Sleep(ms);
                }

                var buffer = System.Runtime.InteropServices.WindowsRuntime
                    .WindowsRuntimeBufferExtensions.AsBuffer(pcm);
                var sample = MediaStreamSample.CreateFromBuffer(buffer, ts);
                sample.Duration = AudioChunk;
                request.Sample = sample;
            }
            catch (Exception ex)
            {
                Log.Debug("音频供样失败: " + ex.Message);
                request.Sample = null;
            }
            finally { deferral.Complete(); }
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

            try { if (_cts != null) _cts.Cancel(); } catch { }
            try { if (_audio != null) _audio.Stop(); } catch { }
            try { if (_session != null) _session.Dispose(); } catch { }
            try { if (_pool != null) _pool.Dispose(); } catch { }
            Log.Info("已请求停止录制");
        }

        private bool _finished;

        private void Finish(string error)
        {
            lock (_sync)
            {
                if (_finished) return;
                _finished = true;
            }

            var dur = DateTime.Now - _startedAt;
            string path = _outputPath;

            CleanUp();
            _state = RecorderState.Idle;

            if (error == null) Log.Info("录制完成 " + path + "  时长 " + dur.ToString(@"hh\:mm\:ss"));
            else Log.Warn("录制结束（有错误）: " + error);

            var h = Stopped;
            if (h != null)
            {
                try { h(this, new RecordingStoppedEventArgs(path, error, dur)); }
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
            try
            {
                lock (_sync)
                {
                    var last = _lastFrame;
                    _lastFrame = null;
                    // 先清在途表再释放，避免 ReleaseFrameLocked 因 _lastFrame 判断而漏放
                    foreach (var kv in _inFlight)
                    {
                        try { kv.Key.Dispose(); } catch { }
                    }
                    _inFlight.Clear();
                    if (last != null) { try { last.Dispose(); } catch { } }
                }
            }
            catch { }
            try { if (_clock != null) { _clock.Stop(); _clock = null; } } catch { }
            try { if (_audio != null) { _audio.Dispose(); _audio = null; } } catch { }
            _videoDescriptor = null;
            _audioDescriptor = null;
            try { if (_session != null) { _session.Dispose(); _session = null; } } catch { }
            try { if (_pool != null) { _pool.Dispose(); _pool = null; } } catch { }
            try { if (_stream != null) { _stream.Dispose(); _stream = null; } } catch { }
            try { if (_item != null) { _item.Closed -= OnItemClosed; _item = null; } } catch { }
            _mss = null;
            _device = null;

            try
            {
                if (_nativeContext != IntPtr.Zero)
                { System.Runtime.InteropServices.Marshal.Release(_nativeContext); _nativeContext = IntPtr.Zero; }
                if (_nativeDevice != IntPtr.Zero)
                { System.Runtime.InteropServices.Marshal.Release(_nativeDevice); _nativeDevice = IntPtr.Zero; }
            }
            catch { }

            try { if (_cts != null) { _cts.Dispose(); _cts = null; } } catch { }
            // 注意：不要在这里复位 _finished。它是一次性录制的终态标记，
            // 复位会让 Finish 有机会重复触发 Stopped 事件。
        }

        private static string BuildOutputPath(RecordingOptions options, CaptureTarget target)
        {
            string dir = string.IsNullOrEmpty(options.OutputFolder)
                ? RecordingOptions.DefaultFolder
                : options.OutputFolder;

            // 键不能用"录制"——那个词同时是导航项，两处英文不同，字典里会互相覆盖
            string safe = target.Title ?? L.T("未命名录制");
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            if (safe.Length > 40) safe = safe.Substring(0, 40);

            string name = string.Format("{0}_{1:yyyyMMdd_HHmmss}.mp4", safe, DateTime.Now);
            return Path.Combine(dir, name);
        }

        public void Dispose()
        {
            Stop();
            CleanUp();
        }
    }
}
