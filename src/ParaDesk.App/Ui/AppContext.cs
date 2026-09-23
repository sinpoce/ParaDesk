using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using ParaDesk.Core;
using ParaDesk.Elevated;
using ParaDesk.Native;
using ParaDesk.Rdp;
using WinTimer = System.Windows.Forms.Timer;

namespace ParaDesk.Ui
{
    internal class AppContext : ApplicationContext
    {
        private const int RecordingFinalizeTimeoutMs = 20000;

        private const int SurfaceCloseTimeoutMs = 6000;

        private const int StartWaitTimeoutMs = 100000;

        private const int CloseWaitTimeoutMs = 100000;

        private const int RecordStopWaitTimeoutMs = 60000;

        private const int ScreenshotWaitTimeoutMs = 90000;

        private const int QuitReplyGraceMs = 300;

        private const int AutoStartDelayMs = 1500;

        private const int StateWriteThrottleMs = 250;

        private const int SaveDebounceMs = 500;

        private static readonly int[] ReattachDelaysMs = { 5000, 15000, 60000 };

        private const int ReattachStableMs = 2 * 60 * 1000;

        private const int RecordWatchIntervalMs = 30000;

        private const long DiskWarnBytes = 2L * 1024 * 1024 * 1024;

        private const long DiskStopBytes = 500L * 1024 * 1024;

        private const int EnvCacheTtlSeconds = 5;

        private const int SessionWatchIntervalMs = 10000;

        private const int IdentifySeconds = 3;

        private const int ErrorCancelled = 1223;

        private enum ExitPhase
        {
            None,
            FinalizingRecording,
            ClosingSurface,
            Done,
        }

        /// <summary>
        /// 常驻的 UI 线程封送锚点。不能依赖主窗体：--minimized 启动时它压根不存在，
        /// 用户关掉它之后也会变 null；那时若把回调就地跑在线程池线程上，
        /// 就会在非 UI 线程上改托盘菜单项，触发 ToolStrip 的布局/重绘路径。
        /// 构造时立即取一次 Handle，强制在 UI 线程上创建句柄。
        /// </summary>
        private readonly Control _uiAnchor;

        private readonly TrayService _tray;
        private readonly HotkeyService _hotkeys;
        private readonly ShutdownGuard _shutdown;
        private readonly MonitorService _monitors;

        private AppSettings _settings;
        private Shell.MainWindow _main;
        private DesktopSurface _surface;

        private DesktopProfile _surfaceProfile;

        private DesktopSurface _intentionalClose;

        private ExitPhase _exitPhase = ExitPhase.None;
        private bool _exitConfirming;
        private bool _restartPending;
        private WinTimer _exitTimeout;

        private bool _setupRunning;
        private bool _undoRunning;

        private readonly WinTimer _stateTimer;
        private readonly WinTimer _saveTimer;
        private WinTimer _autoStartTimer;

        private readonly WinTimer _sessionWatch;
        private bool _lastSessionExists;

        private readonly List<PendingReply> _pendingReplies = new List<PendingReply>();
        private readonly List<KeyValuePair<DesktopSurface, PendingReply>> _connectWaiters =
            new List<KeyValuePair<DesktopSurface, PendingReply>>();
        private readonly List<KeyValuePair<Recording.ScreenRecorder, PendingReply>> _recordWaiters =
            new List<KeyValuePair<Recording.ScreenRecorder, PendingReply>>();

        public AppSettings Settings { get { return _settings; } }

        public bool SetupRunning { get { return _setupRunning || _undoRunning; } }

        public bool UndoRunning { get { return _undoRunning; } }

        public bool DesktopAttached
        {
            get { return _surface != null && !_surface.IsDisposed; }
        }

        /// <summary>
        /// 画面**实际所在**的显示器设备名；未运行时为 null。
        ///
        /// 和 profile.MonitorDevice 不是一回事：后者是"想开在哪"，
        /// MonitorDevice 为 null（自动）或目标屏被拔掉走了回退时，两者会不同。
        /// 布局图上那个"正在这块屏"的标记必须用这个，用配置值会画不出来。
        /// </summary>
        public string ActiveMonitorDevice
        {
            get
            {
                if (!DesktopAttached) return null;
                var m = _surface.Monitor;
                return m == null ? null : m.DeviceName;
            }
        }

        // —— 供状态卡片读取的只读快照 ——
        public Rdp.SurfaceState SurfaceState
        {
            get { return DesktopAttached ? _surface.State : Rdp.SurfaceState.Idle; }
        }

        public DateTime? SurfaceConnectedAt
        {
            get { return DesktopAttached ? _surface.ConnectedAt : null; }
        }

        public int SurfaceWidth { get { return DesktopAttached ? _surface.CurrentWidth : 0; } }
        public int SurfaceHeight { get { return DesktopAttached ? _surface.CurrentHeight : 0; } }

        // ---------------- 录制 ----------------

        private Recording.ScreenRecorder _recorder;
        private string _recordingTargetName;
        private Recording.CaptureTarget _currentTarget;
        private Recording.CaptureTarget _rolloverTarget;
        private Recording.ScreenRecorder _abandonedRecorder;
        private WinTimer _recordWatch;
        private bool _lowDiskWarned;
        private DesktopSurface _autoRecordedSurface;

        public bool IsRecording
        {
            get { return _recorder != null && _recorder.State != Recording.RecorderState.Idle; }
        }

        public string RecordingTargetName { get { return _recordingTargetName; } }

        public DateTime? RecordingStartedAt
        {
            get { return IsRecording ? _recorder.StartedAt : (DateTime?)null; }
        }

        private Recording.RecordingOptions RecOptions
        {
            get
            {
                if (_settings.Recording == null) _settings.Recording = Recording.RecordingOptions.CreateDefault();
                return _settings.Recording;
            }
        }

        /// <summary>当前可录制的目标：打开着的分身桌面窗口 + 每块显示器。</summary>
        public List<Recording.CaptureTarget> EnumerateCaptureTargets()
        {
            var windows = new List<KeyValuePair<IntPtr, string>>();

            if (DesktopAttached && _surface.IsHandleCreated)
            {
                var mon = _surface.Monitor;
                string title = L.T("分身桌面");
                string name = mon != null ? string.Format(L.T("{0}（{1}）"), title, MonitorNaming.NameOf(mon)) : title;
                windows.Add(new KeyValuePair<IntPtr, string>(_surface.Handle, name));
            }
            return Recording.CaptureTargetEnumerator.Enumerate(windows);
        }

        /// <summary>开始录制。返回 null 表示成功，否则为错误说明。</summary>
        public string StartRecording(Recording.CaptureTarget target)
        {
            return StartRecordingCore(target, false);
        }

        private string StartRecordingCore(Recording.CaptureTarget target, bool continuation)
        {
            if (IsRecording) return L.T("已经在录制中。");
            if (target == null) return L.T("未选择录制目标。");
            if (_exitPhase != ExitPhase.None) return L.T("程序正在退出。");

            var opts = RecOptions;
            long free = Recording.ScreenRecorder.GetFreeBytes(opts.OutputFolder);
            if (free >= 0 && free < DiskStopBytes)
                return string.Format(L.T("录制目录所在磁盘只剩 {0}，空间不足，无法开始录制。"), FormatBytes(free));

            var rec = new Recording.ScreenRecorder();
            rec.Stopped += OnRecordingStopped;
            rec.AudioFailed += OnRecorderAudioFailed;

            string err;
            try { err = rec.Start(target, opts); }
            catch (Exception ex)
            {
                Log.Error("启动录制异常", ex);
                err = L.T("启动录制失败：") + ex.Message;
            }
            if (err != null)
            {
                rec.Stopped -= OnRecordingStopped;
                rec.AudioFailed -= OnRecorderAudioFailed;
                _abandonedRecorder = rec;
                try { rec.Dispose(); }
                catch (Exception ex) { Log.Debug("释放未启动的录制器失败: " + ex.Message); }
                return err;
            }

            _recorder = rec;
            _rolloverTarget = null;
            _currentTarget = target;
            _recordingTargetName = target.Title;
            if (!continuation) _lowDiskWarned = false;
            StartRecordWatch();
            UpdateShutdownBlock();
            RefreshTray();

            if (rec.AudioWarning != null && !continuation)
                _tray.Notify(AppInfo.ProductName,
                    L.T("音频不可用，本次为无声录制：") + rec.AudioWarning, ToolTipIcon.Warning);
            return null;
        }

        public void StopRecording()
        {
            _rolloverTarget = null;
            if (_recorder != null) _recorder.Stop();
        }

        public bool IsRecordingPaused
        {
            get { return _recorder != null && _recorder.IsPaused; }
        }

        public void TogglePauseRecording()
        {
            if (_recorder == null || !IsRecording) return;
            bool pause = !_recorder.IsPaused;
            _recorder.SetPaused(pause);
            if (_recorder.IsPaused != pause) return;
            RefreshTray();
            _tray.Notify(AppInfo.ProductName,
                pause ? L.T("录制已暂停（暂停时长不计入视频）") : L.T("录制已恢复"), ToolTipIcon.Info);
        }

        public void ToggleRecording()
        {
            if (IsRecording)
            {
                StopRecording();
                _tray.Notify(AppInfo.ProductName, L.T("正在结束录制…"), ToolTipIcon.Info);
                return;
            }

            var pick = PickDefaultTarget(EnumerateCaptureTargets());
            if (pick == null)
            {
                _tray.Notify(AppInfo.ProductName, L.T("没有可录制的目标。"), ToolTipIcon.Warning);
                return;
            }

            string err = StartRecording(pick);
            _tray.Notify(AppInfo.ProductName,
                err ?? (L.T("开始录制：") + pick.Title),
                err == null ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }

        private Recording.CaptureTarget PickDefaultTarget(List<Recording.CaptureTarget> targets)
        {
            return FindWindowTarget(targets, IntPtr.Zero) ?? PickMonitorTarget(targets);
        }

        private static Recording.CaptureTarget FindWindowTarget(List<Recording.CaptureTarget> targets, IntPtr handle)
        {
            if (targets == null) return null;
            foreach (var t in targets)
            {
                if (t.Kind != Recording.CaptureTargetKind.Window) continue;
                if (handle != IntPtr.Zero && t.Handle != handle) continue;
                return t;
            }
            return null;
        }

        private Recording.CaptureTarget PickMonitorTarget(List<Recording.CaptureTarget> targets)
        {
            if (targets == null) return null;
            string device = ActiveMonitorDevice;
            if (device == null)
            {
                var mon = MonitorService.Resolve(_settings.GetActiveProfile().MonitorDevice);
                if (mon != null) device = mon.DeviceName;
            }

            Recording.CaptureTarget pick = null;
            foreach (var t in targets)
            {
                if (t.Kind != Recording.CaptureTargetKind.Monitor) continue;
                if (device != null && string.Equals(t.DeviceName, device, StringComparison.Ordinal)) return t;
                if (pick == null) pick = t;
            }
            return pick;
        }

        private void OnRecordingStopped(object sender, Recording.RecordingStoppedEventArgs e)
        {
            var rec = sender as Recording.ScreenRecorder;
            PostToUi(delegate { HandleRecordingStopped(rec, e); });
        }

        private void HandleRecordingStopped(Recording.ScreenRecorder rec, Recording.RecordingStoppedEventArgs e)
        {
            if (rec != null)
            {
                rec.Stopped -= OnRecordingStopped;
                rec.AudioFailed -= OnRecorderAudioFailed;
                try { rec.Dispose(); }
                catch (Exception ex) { Log.Debug("释放录制器失败: " + ex.Message); }
            }

            if (rec != null && ReferenceEquals(rec, _abandonedRecorder))
            {
                _abandonedRecorder = null;
                return;
            }

            bool current = rec == null || ReferenceEquals(rec, _recorder);
            Recording.CaptureTarget rollover = null;
            if (current)
            {
                rollover = _rolloverTarget;
                _rolloverTarget = null;
                _recorder = null;
                _recordingTargetName = null;
                _currentTarget = null;
                StopRecordWatch();
                UpdateShutdownBlock();
            }

            FinishRecordWaiters(rec, e);

            // 退出流程正等着录制收尾
            if (_exitPhase == ExitPhase.FinalizingRecording)
            {
                RefreshTray();
                ContinueExitAfterRecording();
                return;
            }
            if (_exitPhase != ExitPhase.None) return;

            if (_main != null) _main.OnRecordingFinished(e.FilePath, e.Error);

            if (rollover != null && e.Error == null)
            {
                bool prevHadAudio = rec != null && rec.HasAudio;
                var next = ResolveContinuationTarget(rollover);
                string err = next == null ? L.T("录制目标已经不在了。") : StartRecordingCore(next, true);
                if (err == null)
                {
                    Log.Info("录制分段：已保存 " + e.FilePath + "，继续录制下一段");
                    var notes = new List<string>();
                    if (!string.IsNullOrEmpty(e.Warning)) notes.Add(L.T("录制提示：") + e.Warning);
                    var cur = _recorder;
                    if (prevHadAudio && cur != null && cur.AudioWarning != null)
                        notes.Add(L.T("音频不可用，本次为无声录制：") + cur.AudioWarning);
                    if (notes.Count > 0)
                        _tray.Notify(AppInfo.ProductName, string.Join("\r\n", notes.ToArray()), ToolTipIcon.Warning);
                    return;
                }
                Log.Warn("录制分段后未能继续: " + err);
                RefreshTray();
                _tray.Notify(AppInfo.ProductName, L.T("录制已分段保存，但下一段未能开始：") + err, ToolTipIcon.Warning);
                return;
            }

            RefreshTray();
            if (e.Error == null)
            {
                string msg = string.Format(L.T("录制完成（{0}）"), e.Duration.ToString(@"hh\:mm\:ss"));
                bool warn = !string.IsNullOrEmpty(e.Warning);
                if (warn) msg += "\r\n" + e.Warning;
                _tray.Notify(AppInfo.ProductName, msg, warn ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }
            else
            {
                _tray.Notify(AppInfo.ProductName, L.T("录制未成功：") + e.Error, ToolTipIcon.Warning);
            }
        }

        private Recording.CaptureTarget ResolveContinuationTarget(Recording.CaptureTarget old)
        {
            var targets = EnumerateCaptureTargets();
            if (old.Kind == Recording.CaptureTargetKind.Window)
            {
                if (!DesktopAttached || !_surface.IsHandleCreated || _surface.Handle != old.Handle) return null;
                return FindWindowTarget(targets, old.Handle);
            }
            foreach (var t in targets)
            {
                if (t.Kind == Recording.CaptureTargetKind.Monitor &&
                    string.Equals(t.DeviceName, old.DeviceName, StringComparison.Ordinal)) return t;
            }
            return null;
        }

        private void OnRecorderAudioFailed(string reason)
        {
            PostToUi(delegate
            {
                if (_exitPhase != ExitPhase.None) return;
                _tray.Notify(AppInfo.ProductName, L.T("录制中失去声音：") + reason, ToolTipIcon.Warning);
            });
        }

        private void StartRecordWatch()
        {
            if (_recordWatch == null)
            {
                _recordWatch = new WinTimer { Interval = RecordWatchIntervalMs };
                _recordWatch.Tick += OnRecordWatchTick;
            }
            _recordWatch.Stop();
            _recordWatch.Start();
        }

        private void StopRecordWatch()
        {
            if (_recordWatch != null) _recordWatch.Stop();
        }

        private void OnRecordWatchTick(object sender, EventArgs e)
        {
            var rec = _recorder;
            if (rec == null || rec.State != Recording.RecorderState.Recording) return;
            if (_exitPhase != ExitPhase.None || _rolloverTarget != null) return;

            var opts = RecOptions;
            string folder = string.IsNullOrEmpty(rec.OutputPath) ? opts.OutputFolder : Path.GetDirectoryName(rec.OutputPath);
            long free = Recording.ScreenRecorder.GetFreeBytes(folder);
            if (free >= 0 && free < DiskStopBytes)
            {
                Log.Warn("录制目录所在磁盘剩余 " + free + " 字节，停止录制");
                StopRecording();
                _tray.Notify(AppInfo.ProductName,
                    string.Format(L.T("磁盘空间不足（剩余 {0}），已停止录制。"), FormatBytes(free)), ToolTipIcon.Warning);
                return;
            }
            if (free >= 0 && free < DiskWarnBytes && !_lowDiskWarned)
            {
                _lowDiskWarned = true;
                Log.Warn("录制目录所在磁盘剩余 " + free + " 字节");
                _tray.Notify(AppInfo.ProductName,
                    string.Format(L.T("录制目录所在磁盘只剩 {0}；低于 500 MB 时会自动停止录制。"), FormatBytes(free)),
                    ToolTipIcon.Warning);
            }

            int seg = opts.SegmentMinutes;
            if (seg > 0 && !rec.IsPaused && (DateTime.Now - rec.StartedAt).TotalMinutes >= seg)
            {
                Log.Info("本段录制已满 " + seg + " 分钟，自动分段");
                _rolloverTarget = _currentTarget;
                rec.Stop();
            }
        }

        private void TryAutoRecord(DesktopSurface s)
        {
            var opts = RecOptions;
            if (!opts.AutoRecordWithDesktop || IsRecording || _exitPhase != ExitPhase.None) return;
            if (!s.IsHandleCreated) return;

            var pick = FindWindowTarget(EnumerateCaptureTargets(), s.Handle);
            if (pick == null)
            {
                Log.Warn("自动录制：没有找到分身桌面窗口目标");
                return;
            }
            string err = StartRecording(pick);
            if (err != null)
            {
                Log.Warn("自动录制未能开始: " + err);
                _tray.Notify(AppInfo.ProductName, L.T("自动录制未能开始：") + err, ToolTipIcon.Warning);
            }
            else
            {
                Log.Info("已按设置自动开始录制分身桌面");
                _tray.Notify(AppInfo.ProductName, L.T("已自动开始录制分身桌面。"), ToolTipIcon.Info);
            }
        }

        public AppContext(bool startMinimized) : this(startMinimized, null) { }

        public AppContext(bool startMinimized, AppSettings preloaded)
        {
            _uiAnchor = new Control();
            IntPtr force = _uiAnchor.Handle;   // 必须在 UI 线程上真正建出句柄
            GC.KeepAlive(force);

            _settings = preloaded ?? SettingsStore.Load();
            MonitorNaming.Bind(_settings);   // 显示器自定义名要在任何界面构建之前可用

            _stateTimer = new WinTimer { Interval = StateWriteThrottleMs };
            _stateTimer.Tick += delegate
            {
                _stateTimer.Stop();
                WriteStateNow();
            };
            _saveTimer = new WinTimer { Interval = SaveDebounceMs };
            _saveTimer.Tick += delegate
            {
                _saveTimer.Stop();
                SettingsStore.Save(_settings);
            };
            _sessionWatch = new WinTimer { Interval = SessionWatchIntervalMs };
            _sessionWatch.Tick += OnSessionWatchTick;

            _monitors = new MonitorService();
            _monitors.LayoutChanged += OnMonitorLayoutChanged;

            _tray = new TrayService();
            _tray.OpenRequested += delegate { ShowMain(); };
            _tray.StartRequested += delegate { StartDesktopWithFeedback(); };
            _tray.DetachRequested += delegate { DetachDesktop(); };
            _tray.CloseDesktopRequested += delegate { CloseDesktop(true); };
            _tray.ViewOnlyToggled += delegate { SetViewOnly(!GetViewOnly()); };
            _tray.TopMostToggled += delegate { SetAlwaysOnTop(!_settings.GetActiveProfile().AlwaysOnTop); };
            _tray.RecordToggled += delegate { ToggleRecording(); };
            _tray.ScreenshotRequested += delegate { ScreenshotWithFeedback(); };
            _tray.PauseToggled += delegate { TogglePauseWithFeedback(); };
            _tray.AttentionAcknowledged += delegate { _tray.SetAttention(false); };
            _tray.MenuOpening += delegate { UpdateShutdownBlock(); RefreshTray(); };
            _tray.IdentifyRequested += delegate { Shell.IdentifyOverlay.Show(IdentifySeconds); };
            _tray.ExitRequested += delegate { ExitApp(); };

            _hotkeys = new HotkeyService();
            _hotkeys.Pressed += OnHotkey;
            _hotkeys.Apply(_settings.Hotkeys);

            _shutdown = new ShutdownGuard();
            _shutdown.Cleanup += OnSystemShutdown;

            try
            {
                var picked = ApplyProfileForCurrentLayout();
                if (picked != null)
                {
                    Log.Info("启动时按当前显示器组合套用方案: " + picked.Name);
                    _tray.Notify(AppInfo.ProductName,
                        string.Format(L.T("显示器已变化，自动套用方案「{0}」。"), picked.Name), ToolTipIcon.Info);
                }
            }
            catch (Exception ex) { Log.Error("启动时按显示器组合挑选方案失败", ex); }

            UpdateShutdownBlock();
            RefreshTray();

            if (!startMinimized)
            {
                // 首次运行先走向导；用户跳过或走完后不再弹
                if (!_settings.WizardShown) ShowWizard();
                ShowMain();
            }

            RunStartupMaintenance();
            if (_settings.AutoStartDesktop) ScheduleAutoStart();
            if (_settings.CheckUpdates) CheckForUpdatesInBackground();
        }

        private void RunStartupMaintenance()
        {
            try { if (StartupRegistration.RepairIfStale()) Log.Info("已修复开机自启动项（程序位置变过）"); }
            catch (Exception ex) { Log.Error("修复开机自启动项失败", ex); }

            try { if (CrossDeviceSettings.RepairIfStale()) Log.Info("已修复子会话守护的注册项（程序位置变过）"); }
            catch (Exception ex) { Log.Error("修复子会话守护注册项失败", ex); }
        }

        private static bool ProfileNeedsAgent(DesktopProfile p)
        {
            return p != null && (p.KeepAwake || !string.IsNullOrEmpty((p.StartupCommand ?? "").Trim()));
        }

        private static void EnsureAgentRegistered()
        {
            try
            {
                if (!CrossDeviceSettings.EnsureAgentRegistered())
                    Log.Warn("子会话守护未能注册，启动命令与保持唤醒不会生效");
            }
            catch (Exception ex) { Log.Error("注册子会话守护失败", ex); }
        }

        private void ScheduleAutoStart()
        {
            _autoStartTimer = new WinTimer { Interval = AutoStartDelayMs };
            _autoStartTimer.Tick += delegate
            {
                StopAutoStart();
                if (_exitPhase != ExitPhase.None || DesktopAttached) return;

                var env = SystemStatus.CheckCached();
                if (!env.ReadyToStart)
                {
                    string why = DescribeNotReady(env);
                    Log.Warn("未自动启动分身桌面: " + why);
                    _tray.Notify(AppInfo.ProductName, L.T("未自动启动分身桌面：") + why, ToolTipIcon.Warning);
                    return;
                }
                Log.Info("按设置自动启动分身桌面");
                string err = StartDesktop();
                if (err != null) _tray.Notify(AppInfo.ProductName, err, ToolTipIcon.Warning);
            };
            _autoStartTimer.Start();
        }

        private void StopAutoStart()
        {
            if (_autoStartTimer == null) return;
            _autoStartTimer.Stop();
            _autoStartTimer.Dispose();
            _autoStartTimer = null;
        }

        private void CheckForUpdatesInBackground()
        {
            try
            {
                UpdateChecker.CheckAsync(delegate(UpdateInfo info, string error)
                {
                    PostToUi(delegate
                    {
                        if (_exitPhase != ExitPhase.None) return;
                        if (error != null) { Log.Warn("检查更新失败: " + error); return; }
                        if (info == null || !info.IsNewer)
                        {
                            Log.Info("检查更新：已是最新版本");
                            return;
                        }
                        Log.Info("发现新版本 " + info.LatestVersion);
                        ShowNotice(L.T("ParaDesk 有新版本"),
                            string.Format(L.T("新版本 {0} 已发布（当前 {1}）。下载：{2}"),
                                info.LatestVersion, AppInfo.Version, UpdateChecker.ReleasesPage),
                            NoticeInfo.LevelInfo, false);
                    });
                });
            }
            catch (Exception ex) { Log.Error("发起更新检查失败", ex); }
        }

        // ---------------- 主窗口 ----------------

        /// <summary>退出流程进行中。WPF 窗口据此判断关闭是"收进托盘"还是"真的关"。</summary>
        public bool IsExiting { get { return _exitPhase != ExitPhase.None; } }

        /// <summary>
        /// 另一个实例被启动时的响应：把主窗口唤到前台。
        /// 由命名管道监听线程调用，必须切回 UI 线程。
        /// </summary>
        public void ActivateFromOtherInstance()
        {
            PostToUi(delegate
            {
                if (_exitPhase != ExitPhase.None) return;
                Log.Info("收到激活请求，唤起主窗口");
                BringMainToFront();
            });
        }

        private void BringMainToFront()
        {
            ShowMain();
            if (_main != null)
            {
                // Topmost 抖一下是唤到最前的可靠做法：
                // 前台窗口切换有系统限制，仅调 Activate() 常常只会闪任务栏
                bool old = _main.Topmost;
                _main.Topmost = true;
                _main.Topmost = old;
            }
        }

        /// <summary>显示首次运行向导（模态）。</summary>
        public void ShowWizard()
        {
            if (!Shell.WpfHost.Initialize()) return;
            try
            {
                var wiz = new Shell.SetupWizard(this);
                wiz.ShowDialog();
                RefreshTray();
            }
            catch (Exception ex)
            {
                Log.Error("显示首次运行向导失败", ex);
                // 向导失败不能挡住主界面
                _settings.WizardShown = true;
                SettingsStore.Save(_settings);
            }
        }

        public void ShowMain()
        {
            if (!Shell.WpfHost.Initialize())
            {
                MessageBox.Show(L.T("界面初始化失败，详见日志：") + Log.Path0,
                    AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (_main == null)
            {
                _main = new Shell.MainWindow(this);
                _main.Closed += delegate { _main = null; };
            }
            _main.Show();
            if (_main.WindowState == System.Windows.WindowState.Minimized)
                _main.WindowState = System.Windows.WindowState.Normal;
            _main.Activate();
        }

        // ---------------- 桌面生命周期 ----------------

        private void StartDesktopWithFeedback()
        {
            if (!DesktopAttached && _exitPhase == ExitPhase.None)
            {
                var env = SystemStatus.CheckCached();
                if (IsSetupPending(env))
                {
                    ShowMain();
                    _tray.Notify(AppInfo.ProductName,
                        L.T("请在主窗口点「启动桌面」完成首次配置（需要一次管理员授权）。"), ToolTipIcon.Info);
                    return;
                }
            }
            string err = StartDesktop();
            if (err != null)
                _tray.Notify(AppInfo.ProductName, err, ToolTipIcon.Warning);
        }

        public string StartDesktop()
        {
            CancelReattach();
            return StartDesktopInternal();
        }

        private string StartDesktopInternal()
        {
            if (_exitPhase != ExitPhase.None) return L.T("程序正在退出。");
            if (DesktopAttached)
            {
                if (_surface.State == Rdp.SurfaceState.Closing)
                    return L.T("分身桌面的画面正在关闭，请过几秒再启动。");
                _surface.Activate();
                return null;
            }

            var env = SystemStatus.CheckCached();
            if (!env.ReadyToStart)
            {
                string msg = DescribeNotReady(env);
                Log.Warn("启动被拒绝: " + msg);
                return msg;
            }

            var profile = _settings.GetActiveProfile();
            var monitor = MonitorService.Resolve(profile.MonitorDevice);
            if (monitor == null) return L.T("未检测到可用显示器。");

            if (ProfileNeedsAgent(profile)) EnsureAgentRegistered();

            DesktopSurface s = null;
            try
            {
                s = new DesktopSurface(profile, monitor);
                s.StateChanged += OnSurfaceStateChanged;
                s.Closed2 += OnSurfaceClosed;
                s.BoundsSaved += OnSurfaceBoundsSaved;
                _surface = s;
                _surfaceProfile = profile;
                _intentionalClose = null;
                s.Show();

                UpdateShutdownBlock();
                RefreshTray();
                SystemStatus.Invalidate();
                Log.Info("桌面已启动于 " + monitor.DeviceName);
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("启动桌面失败", ex);
                if (s != null)
                {
                    s.StateChanged -= OnSurfaceStateChanged;
                    s.Closed2 -= OnSurfaceClosed;
                    s.BoundsSaved -= OnSurfaceBoundsSaved;
                    try { s.Dispose(); }
                    catch (Exception ex2) { Log.Debug("释放启动失败的画面窗口失败: " + ex2.Message); }
                }
                _surface = null;
                _surfaceProfile = null;
                UpdateShutdownBlock();
                RefreshTray();
                return L.T("启动失败：") + ex.Message;
            }
        }

        /// <summary>收起画面，子会话保留在后台继续运行。</summary>
        public void DetachDesktop()
        {
            CancelReattach();
            if (!DesktopAttached) return;
            SystemStatus.Invalidate();
            Log.Info("收起桌面（子会话保留）");
            _intentionalClose = _surface;
            _surface.Close();
        }

        public void CloseDesktop(bool confirm)
        {
            if (confirm && _settings.ConfirmBeforeClose && SystemStatus.HasChildSession())
            {
                var r = MessageBox.Show(
                    L.T("关闭桌面会结束分身桌面里正在运行的所有程序（相当于注销登录）。") + "\r\n\r\n" +
                    L.T("若只想收起画面、让里面的程序继续跑，请选择「收起」。") + "\r\n\r\n" +
                    L.T("确定关闭吗？"),
                    AppInfo.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
            }
            CloseDesktopCore(null);
        }

        private void CloseDesktopCore(Action<bool> done)
        {
            CancelReattach();
            if (DesktopAttached)
            {
                _intentionalClose = _surface;
                _surface.Close();
            }

            uint id = SystemStatus.ChildSessionId();
            if (id == NativeMethods.NoChildSession || id == 0)
            {
                RefreshTray();
                if (done != null) done(true);
                return;
            }

            // 注销要等桌面内所有程序退出，可能长达数十秒，必须放后台线程
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false;
                int err = 0;
                try
                {
                    ok = NativeMethods.WTSLogoffSession(IntPtr.Zero, id, true);
                    err = ok ? 0 : Marshal.GetLastWin32Error();
                }
                catch (Exception ex) { Log.Error("注销子会话异常", ex); }
                SystemStatus.Invalidate();
                Log.Info("注销子会话 " + id + " => " + ok + (ok ? "" : " Win32Error=" + err));
                PostToUi(delegate
                {
                    UpdateShutdownBlock();
                    RefreshTray();
                    if (!ok)
                        _tray.Notify(AppInfo.ProductName, L.T("关闭桌面失败，详见日志。"), ToolTipIcon.Warning);
                    if (done != null) done(ok);
                });
            });
        }

        private void UpdateShutdownBlock()
        {
            if (_shutdown == null) return;
            if (IsRecording) _shutdown.SetBlockReason(L.T("正在完成录制文件，请稍候…"));
            else if (DesktopAttached) _shutdown.SetBlockReason(L.T("正在关闭分身桌面，请稍候…"));
            else _shutdown.SetBlockReason(SystemStatus.HasChildSession() ? L.T("分身桌面仍在运行") : null);
        }

        private void OnSurfaceStateChanged(object sender, EventArgs e)
        {
            if (!OnUiThread())
            {
                PostToUi(delegate { OnSurfaceStateChanged(sender, e); });
                return;
            }

            var s = sender as DesktopSurface;
            if (s != null && ReferenceEquals(s, _surface) && !s.IsDisposed)
            {
                if (s.State == Rdp.SurfaceState.Connected)
                {
                    FinishConnectWaiters(s, null);
                    StartStableTimer();

                    if (ReferenceEquals(s, _reattachSurface))
                    {
                        _reattachSurface = null;
                        Log.Info("自动重新接入成功");
                        _tray.Notify(AppInfo.ProductName, L.T("已重新接入分身桌面。"), ToolTipIcon.Info);
                    }
                    if (!ReferenceEquals(_autoRecordedSurface, s))
                    {
                        _autoRecordedSurface = s;
                        TryAutoRecord(s);
                    }
                }
                else
                {
                    StopStableTimer();
                }
            }
            RefreshTray();
        }

        private void OnSurfaceBoundsSaved(object sender, EventArgs e)
        {
            if (!OnUiThread()) { PostToUi(ScheduleSave); return; }
            ScheduleSave();
        }

        private void OnSurfaceClosed(object sender, SurfaceClosedEventArgs e)
        {
            if (!OnUiThread())
            {
                PostToUi(delegate { OnSurfaceClosed(sender, e); });
                return;
            }

            var closed = sender as DesktopSurface;

            FinishConnectWaiters(closed, e);

            if (closed != null)
            {
                closed.StateChanged -= OnSurfaceStateChanged;
                closed.Closed2 -= OnSurfaceClosed;
                closed.BoundsSaved -= OnSurfaceBoundsSaved;
                if (ReferenceEquals(closed, _autoRecordedSurface)) _autoRecordedSurface = null;
            }

            if (closed != null && _surface != null && !ReferenceEquals(closed, _surface) && !_surface.IsDisposed)
            {
                Log.Debug("忽略旧画面的关闭事件");
                return;
            }

            bool intentional = closed != null && ReferenceEquals(closed, _intentionalClose);
            bool fromReattach = closed != null && ReferenceEquals(closed, _reattachSurface);
            if (intentional) _intentionalClose = null;
            if (fromReattach) _reattachSurface = null;

            _surface = null;
            _surfaceProfile = null;
            StopStableTimer();
            SystemStatus.Invalidate();
            bool sessionAlive = SystemStatus.HasChildSession();
            UpdateShutdownBlock();
            RefreshTray();

            // 退出流程正等着这一刻
            if (_exitPhase == ExitPhase.ClosingSurface) { CompleteExit(); return; }
            if (_exitPhase != ExitPhase.None) return;

            bool userSide = e.UserInitiated || intentional;
            if (!userSide && _settings.AutoReattach && (e.EverConnected || fromReattach)
                && sessionAlive && !IsTerminalDisconnect(e))
            {
                ScheduleReattach(DescribeDrop(e));
                return;
            }
            if (fromReattach && !userSide)
            {
                GiveUpReattach(DescribeDrop(e));
                return;
            }

            if (string.IsNullOrEmpty(e.Reason)) return;

            if (!e.EverConnected)
            {
                MessageBox.Show(e.Reason + "\r\n\r\n" + L.T("日志：") + Log.Path0,
                    AppInfo.ProductName + " — " + L.T("连接失败"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                _tray.Notify(AppInfo.ProductName, e.Reason, ToolTipIcon.Info);
            }
        }

        private int _reattachAttempts;
        private WinTimer _reattachTimer;
        private WinTimer _stableTimer;
        private DesktopSurface _reattachSurface;

        private static bool IsTerminalDisconnect(SurfaceClosedEventArgs e)
        {
            switch (e.ExtendedReason)
            {
                case 2:
                case 5:
                case 7:
                case 8:
                case 9:
                case 10:
                case 11:
                case 12:
                    return true;
            }
            return e.DisconnectReason == 1 || e.DisconnectReason == 2;
        }

        private static string DescribeDrop(SurfaceClosedEventArgs e)
        {
            return string.IsNullOrEmpty(e.Reason) ? L.T("连接意外断开。") : e.Reason;
        }

        private void ScheduleReattach(string reason)
        {
            if (_reattachTimer != null) return;
            if (_reattachAttempts >= ReattachDelaysMs.Length)
            {
                GiveUpReattach(reason);
                return;
            }

            int delay = ReattachDelaysMs[_reattachAttempts];
            _reattachAttempts++;
            int attempt = _reattachAttempts;
            Log.Info("画面意外断开（" + reason + "），" + (delay / 1000) + " 秒后第 " + attempt + " 次自动重新接入");

            _reattachTimer = new WinTimer { Interval = delay };
            _reattachTimer.Tick += delegate
            {
                StopReattachTimer();
                RunReattachAttempt(attempt);
            };
            _reattachTimer.Start();
        }

        private void RunReattachAttempt(int attempt)
        {
            if (_exitPhase != ExitPhase.None || DesktopAttached) return;
            if (!_settings.AutoReattach)
            {
                Log.Info("自动重新接入已在设置里关闭，放弃");
                _reattachAttempts = 0;
                return;
            }
            if (!SystemStatus.HasChildSession())
            {
                GiveUpReattach(L.T("子会话已经结束。"));
                return;
            }

            _tray.Notify(AppInfo.ProductName,
                string.Format(L.T("画面意外断开，正在重新接入（第 {0} 次）…"), attempt), ToolTipIcon.Info);
            SystemStatus.Invalidate();
            string err = StartDesktopInternal();
            if (err != null)
            {
                Log.Warn("第 " + attempt + " 次自动重新接入未能发起: " + err);
                ScheduleReattach(err);
                return;
            }
            _reattachSurface = _surface;
        }

        private void GiveUpReattach(string reason)
        {
            StopReattachTimer();
            _reattachAttempts = 0;
            _reattachSurface = null;
            Log.Warn("自动重新接入放弃: " + reason);

            string msg = L.T("自动重新接入未成功，已放弃：") + reason;
            if (SystemStatus.HasChildSession())
                msg += "\r\n" + L.T("子会话仍在后台运行，可从托盘菜单「重新接入桌面」。");
            _tray.Notify(AppInfo.ProductName, msg, ToolTipIcon.Warning);
        }

        private void CancelReattach()
        {
            if (_reattachTimer != null) Log.Info("取消待定的自动重新接入");
            StopReattachTimer();
            _reattachAttempts = 0;
            _reattachSurface = null;
        }

        private void StopReattachTimer()
        {
            if (_reattachTimer == null) return;
            _reattachTimer.Stop();
            _reattachTimer.Dispose();
            _reattachTimer = null;
        }

        private void StartStableTimer()
        {
            if (_stableTimer == null)
            {
                _stableTimer = new WinTimer { Interval = ReattachStableMs };
                _stableTimer.Tick += delegate
                {
                    _stableTimer.Stop();
                    if (DesktopAttached && _surface.State == Rdp.SurfaceState.Connected && _reattachAttempts > 0)
                    {
                        Log.Info("连接已稳定，自动重新接入计数归零");
                        _reattachAttempts = 0;
                    }
                };
            }
            if (!_stableTimer.Enabled) _stableTimer.Start();
        }

        private void StopStableTimer()
        {
            if (_stableTimer != null) _stableTimer.Stop();
        }

        // ---------------- 开关（绝对值语义，避免两处状态互相打架） ----------------

        /// <summary>配置是唯一事实来源；运行中的窗口只是它的投影。</summary>
        public bool GetViewOnly()
        {
            return _settings.GetActiveProfile().ViewOnly;
        }

        public void SetViewOnly(bool on)
        {
            var p = _settings.GetActiveProfile();
            bool changed = p.ViewOnly != on;
            p.ViewOnly = on;
            SurfaceSetViewOnly(on);
            ScheduleSave();
            RefreshTray();
            if (changed && DesktopAttached)
                _tray.Notify(AppInfo.ProductName,
                    on ? L.T("已开启仅查看：你的键鼠不会作用到分身桌面。") : L.T("已关闭仅查看。"),
                    ToolTipIcon.Info);
        }

        public void SetAlwaysOnTop(bool on)
        {
            var p = _settings.GetActiveProfile();
            p.AlwaysOnTop = on;
            SurfaceSetAlwaysOnTop(on);
            ScheduleSave();
            RefreshTray();
        }

        private void SurfaceSetViewOnly(bool on)
        {
            if (!DesktopAttached) return;
            var own = _surfaceProfile;
            bool foreign = own != null && !ReferenceEquals(own, _settings.GetActiveProfile());
            bool keep = foreign && own.ViewOnly;
            _surface.SetViewOnly(on);
            if (foreign) own.ViewOnly = keep;
        }

        private void SurfaceSetAlwaysOnTop(bool on)
        {
            if (!DesktopAttached) return;
            var own = _surfaceProfile;
            bool foreign = own != null && !ReferenceEquals(own, _settings.GetActiveProfile());
            bool keep = foreign && own.AlwaysOnTop;
            _surface.SetAlwaysOnTop(on);
            if (foreign) own.AlwaysOnTop = keep;
        }

        /// <summary>手动剪贴板模式：把主桌面的剪贴板推给分身桌面。</summary>
        public bool PushClipboard()
        {
            return DesktopAttached && _surface.PushClipboardToDesktop();
        }

        /// <summary>手动剪贴板模式：从分身桌面取回剪贴板。</summary>
        public bool PullClipboard()
        {
            return DesktopAttached && _surface.PullClipboardFromDesktop();
        }

        public void ApplyDisplayChanges()
        {
            if (!DesktopAttached) return;
            var p = _settings.GetActiveProfile();
            _surface.SetResolutionPolicy(p.ResolutionMode, p.CustomWidth, p.CustomHeight);
            var monitor = MonitorService.Resolve(p.MonitorDevice);
            if (monitor != null) _surface.MoveToMonitor(monitor);
            _surface.SyncResolutionToWindow();
            PushTogglesToSurface();
            ScheduleStateWrite();
        }

        private void PushTogglesToSurface()
        {
            if (!DesktopAttached) return;
            var p = _settings.GetActiveProfile();
            SurfaceSetViewOnly(p.ViewOnly);
            SurfaceSetAlwaysOnTop(p.AlwaysOnTop);
        }

        public void SelectProfile(string name)
        {
            if (string.IsNullOrEmpty(name) || !_settings.HasProfile(name)) return;
            bool changed = !string.Equals(_settings.ActiveProfile, name, StringComparison.Ordinal);
            bool preferredChanged = !string.Equals(_settings.PreferredProfile, name, StringComparison.Ordinal);
            if (!changed)
            {
                if (preferredChanged)
                {
                    _settings.PreferredProfile = name;
                    SettingsStore.Save(_settings);
                }
                return;
            }

            _settings.ActiveProfile = name;
            _settings.PreferredProfile = name;
            SettingsStore.Save(_settings);
            Log.Info("用户选择方案: " + name);

            if (DesktopAttached) ApplyDisplayChanges();
            RefreshTray();
        }

        public void RunSetup(Action<SetupResult> done)
        {
            if (_setupRunning || _undoRunning) { RejectBusySetup("首次配置"); return; }
            _setupRunning = true;
            RefreshTray();

            RunElevatedCore("", delegate(SetupResult result)
            {
                _setupRunning = false;
                InvalidateEnvCache();
                try { RefreshTray(); }
                catch (Exception ex) { Log.Error("配置完成后刷新托盘失败", ex); }

                // 主窗体可能已被关掉，结果仍必须让用户看到
                bool reported = done != null && done2(done, result);
                if (!reported) ReportSetupResult(result);
            });
        }

        private void RejectBusySetup(string what)
        {
            Log.Warn("已有配置/撤销的提权进程在运行，忽略本次" + what + "请求");
            _tray.Notify(AppInfo.ProductName, L.T("系统配置或撤销正在进行中，请等它完成后再试。"), ToolTipIcon.Warning);
            RefreshTray();
        }

        public void RunEnableSandbox(Action<SetupResult> done)
        {
            RunElevatedCore("--sandbox", delegate(SetupResult r)
            {
                bool reported = done != null && done2(done, r);
                if (!reported) ReportSandboxResult(r);
            });
        }

        public void RunSetupFps(int fps, Action<bool> done)
        {
            RunElevatedCore("--fps " + fps, delegate(SetupResult r)
            {
                bool ok = r == SetupResult.Success;
                bool reported = false;
                if (done != null)
                {
                    try { done(ok); reported = true; }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                    catch (Exception ex) { Log.Error("汇报帧率设置结果失败", ex); }
                }
                if (!reported) ReportFpsResult(ok, fps);
            });
        }

        public void RunUndoSetup(Action<SetupResult> done)
        {
            if (_setupRunning || _undoRunning) { RejectBusySetup("撤销配置"); return; }
            _undoRunning = true;
            RefreshTray();

            RunElevatedCore("--undo", delegate(SetupResult r)
            {
                _undoRunning = false;
                if (r == SetupResult.Success || r == SetupResult.RebootRequired)
                {
                    try
                    {
                        if (!CrossDeviceSettings.ApplyAll(false)) Log.Warn("撤销配置：清理用户级设置未完全成功");
                    }
                    catch (Exception ex) { Log.Error("撤销配置：清理用户级设置失败", ex); }
                }
                SystemStatus.Invalidate();
                InvalidateEnvCache();
                try { RefreshTray(); }
                catch (Exception ex) { Log.Error("撤销配置后刷新托盘失败", ex); }

                bool reported = done != null && done2(done, r);
                if (!reported) ReportUndoResult(r);
            });
        }

        private void RunElevatedCore(string extraArgs, Action<SetupResult> done)
        {
            string logDir = (Log.Dir ?? "").TrimEnd('\\');
            string args = "--setup \"" + logDir + "\"" + (string.IsNullOrEmpty(extraArgs) ? "" : " " + extraArgs);
            string what = string.IsNullOrEmpty(extraArgs) ? "首次配置" : extraArgs;

            ThreadPool.QueueUserWorkItem(delegate
            {
                SetupResult res = RunElevatedProcess(args, what);
                SystemStatus.Invalidate();   // 配置刚改过，缓存必须作废
                PostToUi(delegate
                {
                    if (done != null) done2(done, res);
                });
            });
        }

        private static SetupResult RunElevatedProcess(string args, string what)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = AppInfo.ExecutablePath,
                    Arguments = args,
                    Verb = "runas",
                    UseShellExecute = true,
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null)
                    {
                        Log.Error("提权子进程未能启动: " + what);
                        return SetupResult.Failed;
                    }
                    p.WaitForExit();
                    int code = p.ExitCode;
                    if (!Enum.IsDefined(typeof(SetupResult), code))
                    {
                        Log.Error("提权子进程返回了未知的退出码 " + code + ": " + what);
                        return SetupResult.Failed;
                    }
                    return (SetupResult)code;
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == ErrorCancelled)
                {
                    Log.Info("用户取消了管理员授权: " + what);
                    return SetupResult.Cancelled;
                }
                Log.Error("提权子进程启动失败（Win32 错误 " + ex.NativeErrorCode + "）: " + what, ex);
                return SetupResult.Failed;
            }
            catch (Exception ex)
            {
                Log.Error("提权设置失败: " + what, ex);
                return SetupResult.Failed;
            }
        }

        /// <summary>回调若因窗体已销毁而无法展示结果，返回 false，由托盘兜底。</summary>
        private static bool done2(Action<SetupResult> done, SetupResult r)
        {
            try { done(r); return true; }
            catch (ObjectDisposedException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (Exception ex) { Log.Error("汇报配置结果失败", ex); return false; }
        }

        /// <summary>主窗体不在时用托盘气泡汇报配置结果，避免用户等不到任何反馈。</summary>
        public void ReportSetupResult(SetupResult r)
        {
            switch (r)
            {
                case SetupResult.Success:
                    _tray.Notify(AppInfo.ProductName, L.T("配置完成，现在可以启动桌面了。"), ToolTipIcon.Info);
                    break;
                case SetupResult.RebootRequired:
                    _tray.Notify(AppInfo.ProductName, L.T("配置已写入，请重启电脑后再启动桌面。"), ToolTipIcon.Info);
                    break;
                case SetupResult.Cancelled:
                    _tray.Notify(AppInfo.ProductName, L.T("已取消（未获得管理员授权）。"), ToolTipIcon.Info);
                    break;
                default:
                    _tray.Notify(AppInfo.ProductName, L.T("配置未完成，详见日志。"), ToolTipIcon.Warning);
                    break;
            }
        }

        private void ReportUndoResult(SetupResult r)
        {
            switch (r)
            {
                case SetupResult.Success:
                    _tray.Notify(AppInfo.ProductName, L.T("已撤销 ParaDesk 对系统所做的配置。"), ToolTipIcon.Info);
                    break;
                case SetupResult.RebootRequired:
                    _tray.Notify(AppInfo.ProductName, L.T("已撤销配置，重启电脑后完全生效。"), ToolTipIcon.Info);
                    break;
                case SetupResult.Cancelled:
                    _tray.Notify(AppInfo.ProductName, L.T("已取消（未获得管理员授权）。"), ToolTipIcon.Info);
                    break;
                default:
                    _tray.Notify(AppInfo.ProductName, L.T("撤销未完成，详见日志。"), ToolTipIcon.Warning);
                    break;
            }
        }

        private void ReportSandboxResult(SetupResult r)
        {
            switch (r)
            {
                case SetupResult.Success:
                case SetupResult.RebootRequired:
                    _tray.Notify(AppInfo.ProductName, L.T("沙盒功能已启用，重启电脑后即可使用。"), ToolTipIcon.Info);
                    break;
                case SetupResult.Cancelled:
                    _tray.Notify(AppInfo.ProductName, L.T("已取消（未获得管理员授权）。"), ToolTipIcon.Info);
                    break;
                default:
                    _tray.Notify(AppInfo.ProductName, L.T("启用沙盒功能失败，详见日志。"), ToolTipIcon.Warning);
                    break;
            }
        }

        private void ReportFpsResult(bool ok, int fps)
        {
            if (ok)
                _tray.Notify(AppInfo.ProductName,
                    string.Format(L.T("帧率上限已设为 {0} FPS。{1}。"), fps, L.T(PerformanceSettings.FrameIntervalNote)),
                    ToolTipIcon.Info);
            else
                _tray.Notify(AppInfo.ProductName, L.T("设置帧率失败（可能未获得管理员授权）。"), ToolTipIcon.Warning);
        }

        // ---------------- 事件 ----------------

        private void OnHotkey(object sender, HotkeyPressedEventArgs e)
        {
            switch (e.Action)
            {
                case "toggleDesktop":
                    if (DesktopAttached && _surface.State != Rdp.SurfaceState.Closing) DetachDesktop();
                    else StartDesktopWithFeedback();
                    break;
                case "toggleViewOnly":
                    SetViewOnly(!GetViewOnly());
                    break;
                case "toggleRecording":
                    ToggleRecording();
                    break;
                case "detach":
                    DetachDesktop();
                    break;
                case "screenshot":
                    ScreenshotWithFeedback();
                    break;
                case "togglePause":
                    TogglePauseWithFeedback();
                    break;
                case "pushClipboard":
                    ClipboardWithFeedback(true);
                    break;
                case "pullClipboard":
                    ClipboardWithFeedback(false);
                    break;
            }
        }

        private void ScreenshotWithFeedback()
        {
            TakeScreenshot(null, delegate(string path, string err)
            {
                if (err == null)
                    _tray.Notify(AppInfo.ProductName, L.T("已截图：") + path, ToolTipIcon.Info);
                else
                    _tray.Notify(AppInfo.ProductName, L.T("截图失败：") + err, ToolTipIcon.Warning);
            });
        }

        private void TogglePauseWithFeedback()
        {
            if (!IsRecording)
            {
                _tray.Notify(AppInfo.ProductName, L.T("当前没有在录制。"), ToolTipIcon.Info);
                return;
            }
            bool before = IsRecordingPaused;
            TogglePauseRecording();
            if (IsRecordingPaused == before)
                _tray.Notify(AppInfo.ProductName, L.T("录制器暂时无法切换暂停状态，请稍后再试。"), ToolTipIcon.Warning);
        }

        private void ClipboardWithFeedback(bool push)
        {
            if (!DesktopAttached)
            {
                _tray.Notify(AppInfo.ProductName, L.T("分身桌面画面没有打开，无法同步剪贴板。"), ToolTipIcon.Info);
                return;
            }
            var mode = _surfaceProfile != null ? _surfaceProfile.Clipboard : _settings.GetActiveProfile().Clipboard;
            if (mode != ClipboardMode.Manual)
            {
                _tray.Notify(AppInfo.ProductName,
                    L.T("推送/取回剪贴板只在「手动同步」剪贴板模式下可用，可在「输入与共享」页切换。"), ToolTipIcon.Info);
                return;
            }
            bool ok = push ? PushClipboard() : PullClipboard();
            string msg = push
                ? (ok ? L.T("已把剪贴板推送到分身桌面。") : L.T("推送剪贴板失败，详见日志。"))
                : (ok ? L.T("已从分身桌面取回剪贴板。") : L.T("取回剪贴板失败，详见日志。"));
            _tray.Notify(AppInfo.ProductName, msg, ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }

        private DesktopProfile ApplyProfileForCurrentLayout()
        {
            var devices = new List<string>();
            foreach (var m in MonitorService.Enumerate()) devices.Add(m.DeviceName);

            var picked = _settings.PickForCurrentLayout(devices);
            if (picked == null || string.Equals(picked.Name, _settings.ActiveProfile, StringComparison.Ordinal)) return null;
            _settings.ActiveProfile = picked.Name;
            SettingsStore.Save(_settings);
            return picked;
        }

        private void OnMonitorLayoutChanged(object sender, EventArgs e)
        {
            // 显示器组合变了，先看看有没有更合适的方案可以自动套用
            var picked = ApplyProfileForCurrentLayout();
            bool switched = picked != null;
            if (switched)
            {
                Log.Info("显示器变化，已自动切换到方案: " + picked.Name);
                _tray.Notify(AppInfo.ProductName,
                    string.Format(L.T("显示器已变化，自动套用方案「{0}」。"), picked.Name), ToolTipIcon.Info);
            }

            if (DesktopAttached)
            {
                var active = _settings.GetActiveProfile();
                _surface.SetResolutionPolicy(active.ResolutionMode, active.CustomWidth, active.CustomHeight);
                FollowMonitorLayout();
                if (switched) _surface.SyncResolutionToWindow();
                PushTogglesToSurface();
            }

            if (switched) RefreshTray();
            else
            {
                if (_main != null) _main.RefreshAll();
                ScheduleStateWrite();
            }
        }

        private void FollowMonitorLayout()
        {
            var profile = _settings.GetActiveProfile();
            var target = MonitorService.Resolve(profile.MonitorDevice);
            if (target == null) return;

            var current = _surface.Monitor;
            if (current != null && string.Equals(current.DeviceName, target.DeviceName, StringComparison.Ordinal))
            {
                if (current.Bounds == target.Bounds) return;
                Log.Info("画面所在显示器的边界变了，跟随调整: " + target.DeviceName);
                _surface.MoveToMonitor(target);
                ScheduleStateWrite();
                return;
            }

            _surface.MoveToMonitor(target);
            ScheduleStateWrite();
            _tray.Notify(AppInfo.ProductName,
                string.Format(L.T("分身桌面已移动到 {0}。"), MonitorNaming.NameOf(target)), ToolTipIcon.Info);
        }

        private void OnSystemShutdown(object sender, EventArgs e)
        {
            try { StateFile.MarkStopped(); }
            catch (Exception ex) { Log.Error("关机前更新状态文件失败", ex); }

            if (_recorder != null && IsRecording)
            {
                Log.Info("关机前收尾录制");
                _rolloverTarget = null;
                try { _recorder.Dispose(); }
                catch (Exception ex) { Log.Error("关机前收尾录制失败", ex); }
            }

            if (!_settings.AutoLogoffOnShutdown) return;
            uint id = SystemStatus.ChildSessionId();
            if (id == NativeMethods.NoChildSession || id == 0) return;

            Log.Info("关机前注销子会话 " + id);
            try { NativeMethods.WTSLogoffSession(IntPtr.Zero, id, false); }
            catch (Exception ex) { Log.Error("关机前注销失败", ex); }
        }

        // ---------------- 辅助 ----------------

        private static int _homeEdition = -1;

        private static bool IsHomeEditionCached()
        {
            if (_homeEdition < 0) _homeEdition = SystemStatus.IsHomeEdition() ? 1 : 0;
            return _homeEdition == 1;
        }

        private bool _envStartable;
        private DateTime _envCheckedAt = DateTime.MinValue;

        private bool EnvironmentLooksStartable()
        {
            if ((DateTime.UtcNow - _envCheckedAt).TotalSeconds >= EnvCacheTtlSeconds)
            {
                _envStartable = SystemStatus.ChildSessionsEnabled() && SystemStatus.TermServiceRunning();
                _envCheckedAt = DateTime.UtcNow;
            }
            return _envStartable;
        }

        private void InvalidateEnvCache()
        {
            _envCheckedAt = DateTime.MinValue;
        }

        public void RefreshTray()
        {
            bool attached = DesktopAttached;
            bool exists = SystemStatus.HasChildSession();
            var p = _settings.GetActiveProfile();

            // 这里刻意不做通道自检：ProbeTransport 会真的创建子会话传输通道，
            // 属于有副作用的调用，不该由每次状态刷新触发。
            bool canStart = !attached && !IsHomeEditionCached() && EnvironmentLooksStartable();

            _tray.UpdateState(attached, exists, canStart, p.ViewOnly, p.AlwaysOnTop, IsRecording, IsRecordingPaused);
            ScheduleStateWrite();

            _lastSessionExists = exists;
            if (_sessionWatch != null)
            {
                bool watch = !attached && exists && _exitPhase == ExitPhase.None;
                if (watch && !_sessionWatch.Enabled) _sessionWatch.Start();
                else if (!watch && _sessionWatch.Enabled) _sessionWatch.Stop();
            }

            if (_main != null) _main.RefreshAll();
        }

        private void OnSessionWatchTick(object sender, EventArgs e)
        {
            if (_exitPhase != ExitPhase.None || DesktopAttached)
            {
                _sessionWatch.Stop();
                return;
            }
            bool exists;
            try { exists = SystemStatus.HasChildSession(); }
            catch (Exception ex)
            {
                Log.Debug("巡检子会话失败: " + ex.Message);
                return;
            }
            if (exists == _lastSessionExists) return;
            Log.Info(exists ? "巡检发现子会话出现了" : "巡检发现后台的子会话已经结束");
            UpdateShutdownBlock();
            RefreshTray();
        }

        /// <summary>重新注册热键。返回注册失败的动作列表（多半是被别的程序占用）。</summary>
        public List<string> ApplyHotkeys()
        {
            return _hotkeys.Apply(_settings.Hotkeys);
        }

        private void ScheduleSave()
        {
            if (_exitPhase == ExitPhase.Done) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private bool PostToUi(Action a)
        {
            try
            {
                if (_uiAnchor != null && !_uiAnchor.IsDisposed && _uiAnchor.IsHandleCreated)
                {
                    _uiAnchor.BeginInvoke((MethodInvoker)delegate { a(); });
                    return true;
                }
                Log.Warn("UI 锚点不可用，回调已丢弃");
            }
            catch (Exception ex) { Log.Error("回到 UI 线程失败", ex); }
            return false;
        }

        private bool OnUiThread()
        {
            try
            {
                return _uiAnchor != null && !_uiAnchor.IsDisposed && _uiAnchor.IsHandleCreated
                       && !_uiAnchor.InvokeRequired;
            }
            catch { return false; }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
                return (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            return (bytes / (1024L * 1024)).ToString(CultureInfo.InvariantCulture) + " MB";
        }

        private static string Shorten(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        private static string DescribeNotReady(EnvironmentReport env)
        {
            if (IsSetupPending(env))
                return L.T("尚未完成首次配置：请在主窗口点「启动桌面」完成配置（需要一次管理员授权）。");
            return env.NextAction ?? L.T("环境尚未就绪。");
        }

        private static bool IsSetupPending(EnvironmentReport env)
        {
            return env.NeedsSetup && (!env.ChildSessionsEnabled || !env.RdpListenerEnabled || !env.TermServiceRunning);
        }

        private static int _pid;

        private static int CurrentPid
        {
            get
            {
                if (_pid == 0)
                {
                    using (var p = Process.GetCurrentProcess()) _pid = p.Id;
                }
                return _pid;
            }
        }

        private void ScheduleStateWrite()
        {
            if (_stateTimer == null || _exitPhase == ExitPhase.Done) return;
            if (!_stateTimer.Enabled) _stateTimer.Start();
        }

        private void WriteStateNow()
        {
            if (_exitPhase == ExitPhase.Done) return;
            try { StateFile.Write(BuildSnapshot()); }
            catch (Exception ex) { Log.Error("写入状态文件失败", ex); }
        }

        public DesktopStateSnapshot BuildSnapshot()
        {
            var p = _settings.GetActiveProfile();
            bool attached = DesktopAttached;
            var shown = attached && _surfaceProfile != null ? _surfaceProfile : p;

            uint id = SystemStatus.ChildSessionId();
            bool exists = id != NativeMethods.NoChildSession && id != 0;

            MonitorInfo mon = attached ? _surface.Monitor : null;
            if (mon == null)
            {
                try { mon = MonitorService.Resolve(p.MonitorDevice); }
                catch (Exception ex) { Log.Debug("快照解析显示器失败: " + ex.Message); }
            }

            var rec = _recorder;
            bool recording = IsRecording;

            return new DesktopStateSnapshot
            {
                Version = StateFile.CurrentVersion,
                Running = _exitPhase != ExitPhase.Done,
                Pid = CurrentPid,
                UpdatedAt = Iso(DateTime.Now),
                AppVersion = AppInfo.Version,

                DesktopState = StateName(attached ? _surface.State : Rdp.SurfaceState.Idle),
                Attached = attached,
                ChildSessionExists = exists,
                ChildSessionId = exists ? (long)id : -1,
                ConnectedAt = attached && _surface.ConnectedAt.HasValue ? Iso(_surface.ConnectedAt.Value) : null,

                Profile = p.Name,
                MonitorDevice = mon != null ? mon.DeviceName : null,
                MonitorName = mon != null ? MonitorNaming.NameOf(mon) : null,
                MonitorX = mon != null ? mon.Bounds.X : 0,
                MonitorY = mon != null ? mon.Bounds.Y : 0,
                MonitorWidth = mon != null ? mon.Bounds.Width : 0,
                MonitorHeight = mon != null ? mon.Bounds.Height : 0,

                Width = attached ? _surface.CurrentWidth : 0,
                Height = attached ? _surface.CurrentHeight : 0,
                ScalePercent = shown.ScalePercent,
                WindowMode = WindowModeName(shown.WindowMode),

                ViewOnly = p.ViewOnly,
                AlwaysOnTop = p.AlwaysOnTop,
                Clipboard = ClipboardName(shown.Clipboard),

                Recording = recording,
                RecordingPaused = recording && rec.IsPaused,
                RecordingPath = recording ? rec.OutputPath : null,
                RecordingTarget = recording ? _recordingTargetName : null,
            };
        }

        private static string Iso(DateTime t)
        {
            var local = t.Kind == DateTimeKind.Utc ? t.ToLocalTime() : t;
            return new DateTimeOffset(local).ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
        }

        private static string StateName(Rdp.SurfaceState s)
        {
            switch (s)
            {
                case Rdp.SurfaceState.Idle: return "idle";
                case Rdp.SurfaceState.Connecting: return "connecting";
                case Rdp.SurfaceState.Connected: return "connected";
                case Rdp.SurfaceState.Reconnecting: return "reconnecting";
                case Rdp.SurfaceState.Closing: return "closing";
                default: return s.ToString().ToLowerInvariant();
            }
        }

        private static string WindowModeName(WindowMode m)
        {
            switch (m)
            {
                case WindowMode.Windowed: return "windowed";
                case WindowMode.Pip: return "pip";
                default: return "fullscreen";
            }
        }

        private static string ClipboardName(ClipboardMode m)
        {
            switch (m)
            {
                case ClipboardMode.Manual: return "manual";
                case ClipboardMode.Off: return "off";
                default: return "shared";
            }
        }

        private static string DescribeSnapshot(DesktopStateSnapshot s)
        {
            string state;
            if (s.Attached)
            {
                switch (s.DesktopState)
                {
                    case "connecting": state = L.T("正在连接"); break;
                    case "reconnecting": state = L.T("正在重新连接"); break;
                    case "closing": state = L.T("正在关闭"); break;
                    default: state = L.T("运行中"); break;
                }
            }
            else
            {
                state = s.ChildSessionExists ? L.T("已收起（后台运行）") : L.T("未启动");
            }

            var sb = new StringBuilder();
            sb.Append(string.Format(L.T("分身桌面：{0}"), state));
            sb.Append(string.Format(L.T("；方案：{0}"), s.Profile));
            if (s.Attached)
            {
                if (s.Width > 0 && s.Height > 0)
                    sb.Append(string.Format(L.T("；显示器：{0}（{1}×{2}）"), s.MonitorName, s.Width, s.Height));
                else
                    sb.Append(string.Format(L.T("；显示器：{0}"), s.MonitorName));
            }
            if (s.ViewOnly) sb.Append(L.T("；仅查看"));
            if (s.Recording) sb.Append(s.RecordingPaused ? L.T("；录制已暂停") : L.T("；录制中"));
            return sb.ToString();
        }

        private string SnapshotJson()
        {
            try { return ControlProtocol.ToJson(BuildSnapshot()); }
            catch (Exception ex)
            {
                Log.Error("生成状态快照失败", ex);
                return null;
            }
        }

        [DataContract]
        internal sealed class PathResult
        {
            [DataMember(Name = "path", Order = 0)] public string FilePath { get; set; }
        }

        private static string PathJson(string path)
        {
            try { return ControlProtocol.ToJson(new PathResult { FilePath = path }); }
            catch (Exception ex)
            {
                Log.Debug("生成路径 JSON 失败: " + ex.Message);
                return null;
            }
        }

        private sealed class ReplyOnce
        {
            private readonly Action<ControlResponse> _reply;
            private readonly string _command;
            private int _sent;

            public ReplyOnce(Action<ControlResponse> reply, string command)
            {
                _reply = reply;
                _command = command;
            }

            public void Send(ControlResponse r)
            {
                if (Interlocked.Exchange(ref _sent, 1) != 0)
                {
                    Log.Debug("命令 " + _command + " 已应答过，忽略重复应答");
                    return;
                }
                if (r == null) r = ControlResponse.Fail(ExitCodes.Failed, "");
                Log.Info("命令 " + _command + " => " + r.ExitCode + " " + Shorten(r.Message, 200));
                if (_reply == null) return;
                try { _reply(r); }
                catch (Exception ex) { Log.Error("写回命令应答失败: " + _command, ex); }
            }
        }

        private sealed class PendingReply
        {
            private readonly Action<ControlResponse> _reply;
            private WinTimer _timer;
            private bool _done;

            public Action Cleanup;

            public PendingReply(Action<ControlResponse> reply)
            {
                _reply = reply;
            }

            public bool IsDone { get { return _done; } }

            public void StartTimeout(int ms, Func<ControlResponse> onTimeout)
            {
                _timer = new WinTimer { Interval = ms };
                _timer.Tick += delegate
                {
                    ControlResponse r;
                    try { r = onTimeout(); }
                    catch (Exception ex) { r = ControlResponse.Fail(ExitCodes.Failed, ex.Message); }
                    Finish(r);
                };
                _timer.Start();
            }

            public void Finish(ControlResponse r)
            {
                if (_done) return;
                _done = true;
                if (_timer != null)
                {
                    _timer.Stop();
                    _timer.Dispose();
                    _timer = null;
                }
                var c = Cleanup;
                Cleanup = null;
                if (c != null)
                {
                    try { c(); }
                    catch (Exception ex) { Log.Debug("清理等待中的命令失败: " + ex.Message); }
                }
                _reply(r);
            }
        }

        private PendingReply NewPending(Action<ControlResponse> reply, int timeoutMs, Func<ControlResponse> onTimeout)
        {
            var p = new PendingReply(reply);
            p.Cleanup = delegate { _pendingReplies.Remove(p); };
            _pendingReplies.Add(p);
            p.StartTimeout(timeoutMs, onTimeout);
            return p;
        }

        private void FinishConnectWaiters(DesktopSurface s, SurfaceClosedEventArgs e)
        {
            if (s == null || _connectWaiters.Count == 0) return;
            foreach (var kv in new List<KeyValuePair<DesktopSurface, PendingReply>>(_connectWaiters))
            {
                if (!ReferenceEquals(kv.Key, s)) continue;
                if (e == null)
                    kv.Value.Finish(ControlResponse.Success(L.T("分身桌面已连接。"), SnapshotJson()));
                else
                    kv.Value.Finish(ControlResponse.Fail(ExitCodes.Failed,
                        string.IsNullOrEmpty(e.Reason) ? L.T("分身桌面在连上之前被关闭了。") : L.T("连接失败：") + e.Reason));
            }
        }

        private void FinishRecordWaiters(Recording.ScreenRecorder rec, Recording.RecordingStoppedEventArgs e)
        {
            if (_recordWaiters.Count == 0) return;
            foreach (var kv in new List<KeyValuePair<Recording.ScreenRecorder, PendingReply>>(_recordWaiters))
            {
                if (rec != null && !ReferenceEquals(kv.Key, rec)) continue;
                if (e.Error == null)
                    kv.Value.Finish(ControlResponse.Success(L.T("录制已保存：") + e.FilePath, PathJson(e.FilePath)));
                else
                    kv.Value.Finish(ControlResponse.Fail(ExitCodes.Failed, L.T("录制未成功：") + e.Error));
            }
        }

        public void HandleControlRequest(ControlRequest req, Action<ControlResponse> reply)
        {
            string cmd = req == null ? "" : NormalizeCommand(req.Command);
            var once = new ReplyOnce(reply, cmd);
            try
            {
                if (req == null)
                {
                    once.Send(ControlResponse.Fail(ExitCodes.Usage, L.T("命令为空。")));
                    return;
                }

                Log.Info("收到命令行命令: " + DescribeRequest(cmd, req));
                bool readOnly = cmd == "status";
                if (_exitPhase == ExitPhase.Done || (_exitPhase != ExitPhase.None && !readOnly))
                {
                    once.Send(QuittingResponse(readOnly));
                    return;
                }

                bool posted = PostToUi(delegate
                {
                    try
                    {
                        if (_exitPhase == ExitPhase.Done || (_exitPhase != ExitPhase.None && !readOnly))
                        {
                            once.Send(QuittingResponse(readOnly));
                            return;
                        }
                        Dispatch(cmd, req, once.Send);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("处理命令失败: " + cmd, ex);
                        once.Send(ControlResponse.Fail(ExitCodes.Failed, ex.Message));
                    }
                });
                if (!posted) once.Send(ControlResponse.Fail(ExitCodes.Failed, L.T("主实例的界面线程不可用。")));
            }
            catch (Exception ex)
            {
                Log.Error("接收命令失败: " + cmd, ex);
                once.Send(ControlResponse.Fail(ExitCodes.Failed, ex.Message));
            }
        }

        private static ControlResponse QuittingResponse(bool status)
        {
            var r = ControlResponse.Fail(ExitCodes.Failed, L.T("ParaDesk 正在退出。"));
            if (!status) return r;
            try
            {
                var s = StateFile.TryRead() ?? new DesktopStateSnapshot { DesktopState = "idle", ChildSessionId = -1 };
                StateFile.ApplyStopped(s);
                s.Version = StateFile.CurrentVersion;
                s.AppVersion = AppInfo.Version;
                uint id = SystemStatus.ChildSessionId();
                bool exists = id != NativeMethods.NoChildSession && id != 0;
                s.ChildSessionExists = exists;
                s.ChildSessionId = exists ? (long)id : -1;
                string json = ControlProtocol.ToJson(s);
                r.ExitCode = ExitCodes.NotRunning;
                r.Message = string.Format(L.T("{0} 没有在运行。"), AppInfo.ProductName);
                r.Json = json;
            }
            catch (Exception ex) { Log.Debug("退出途中生成状态快照失败: " + ex.Message); }
            return r;
        }

        private static string NormalizeCommand(string c)
        {
            return (c ?? "").Trim().TrimStart('-').ToLowerInvariant();
        }

        private static string DescribeRequest(string cmd, ControlRequest req)
        {
            string s = cmd;
            if (req.Args != null && req.Args.Count > 0) s += " " + string.Join(" ", req.Args.ToArray());
            return Shorten(s, 300);
        }

        private void Dispatch(string cmd, ControlRequest req, Action<ControlResponse> reply)
        {
            switch (cmd)
            {
                case "status": CmdStatus(reply); return;
                case "start": CmdStart(req, reply); return;
                case "detach": CmdDetach(reply); return;
                case "close": CmdClose(reply); return;
                case "view-only":
                case "viewonly": CmdSwitch(req, reply, true); return;
                case "topmost": CmdSwitch(req, reply, false); return;
                case "record": CmdRecord(req, reply); return;
                case "screenshot": CmdScreenshot(req, reply); return;
                case "notify": CmdNotify(req, reply); return;
                case "show":
                case "activate": CmdShow(reply); return;
                case "quit": CmdQuit(reply); return;
                default:
                    reply(ControlResponse.Fail(ExitCodes.Usage, string.Format(L.T("不认识的命令：{0}"), cmd)));
                    return;
            }
        }

        private void CmdStatus(Action<ControlResponse> reply)
        {
            var snap = BuildSnapshot();
            reply(ControlResponse.Success(DescribeSnapshot(snap), ControlProtocol.ToJson(snap)));
        }

        private void CmdStart(ControlRequest req, Action<ControlResponse> reply)
        {
            string switchedTo = null;
            if (req.Has("profile"))
            {
                string prof = req.Get("profile");
                if (string.IsNullOrEmpty(prof) || prof.StartsWith("--", StringComparison.Ordinal))
                {
                    reply(ControlResponse.Fail(ExitCodes.Usage, L.T("--profile 后面要跟方案名。")));
                    return;
                }
                if (!_settings.HasProfile(prof))
                {
                    reply(ControlResponse.Fail(ExitCodes.Usage,
                        string.Format(L.T("没有名为「{0}」的方案。现有方案：{1}"), prof, ProfileNames())));
                    return;
                }
                if (!string.Equals(_settings.ActiveProfile, prof, StringComparison.Ordinal)) switchedTo = prof;
                SelectProfile(prof);
            }

            bool wasAttached = DesktopAttached;
            string switchNote = wasAttached && switchedTo != null
                ? string.Format(L.T("已切换到方案「{0}」。窗口模式、声音与剪贴板要在下次启动或重新接入后生效。"), switchedTo)
                : null;
            if (!wasAttached)
            {
                var env = SystemStatus.CheckCached();
                if (!env.ReadyToStart)
                {
                    reply(ControlResponse.Fail(ExitCodes.NotReady, DescribeNotReady(env)));
                    return;
                }
            }

            bool sessionExisted = SystemStatus.HasChildSession();
            string err = StartDesktop();
            if (err != null)
            {
                reply(ControlResponse.Fail(ExitCodes.Failed, err));
                return;
            }
            var s = _surface;
            if (s == null || s.IsDisposed)
            {
                reply(ControlResponse.Fail(ExitCodes.Failed, L.T("分身桌面未能启动，详见日志。")));
                return;
            }

            if (!req.Has("wait"))
            {
                string msg = switchNote != null ? switchNote : wasAttached ? L.T("分身桌面已在运行。")
                    : (sessionExisted ? L.T("正在重新接入分身桌面。") : L.T("正在启动分身桌面。"));
                reply(ControlResponse.Success(msg, null));
                return;
            }

            if (s.State == Rdp.SurfaceState.Connected)
            {
                reply(ControlResponse.Success(switchNote ?? L.T("分身桌面已连接。"), SnapshotJson()));
                return;
            }

            var pending = NewPending(reply, StartWaitTimeoutMs, delegate
            {
                return ControlResponse.Fail(ExitCodes.Failed, L.T("等待 100 秒仍未连上，分身桌面仍在连接中。"));
            });
            var entry = new KeyValuePair<DesktopSurface, PendingReply>(s, pending);
            var removePending = pending.Cleanup;
            pending.Cleanup = delegate
            {
                _connectWaiters.Remove(entry);
                if (removePending != null) removePending();
            };
            _connectWaiters.Add(entry);
        }

        private string ProfileNames()
        {
            var names = new List<string>();
            if (_settings.Profiles != null)
                foreach (var p in _settings.Profiles)
                    if (p != null) names.Add("\"" + p.Name + "\"");
            return string.Join(", ", names.ToArray());
        }

        private void CmdDetach(Action<ControlResponse> reply)
        {
            if (!DesktopAttached)
            {
                CancelReattach();
                reply(ControlResponse.Success(SystemStatus.HasChildSession()
                    ? L.T("画面已经是收起状态，分身桌面在后台运行。")
                    : L.T("分身桌面没有在运行。"), null));
                return;
            }
            DetachDesktop();
            reply(ControlResponse.Success(L.T("已收起画面，分身桌面在后台继续运行。"), null));
        }

        private void CmdClose(Action<ControlResponse> reply)
        {
            if (!DesktopAttached && !SystemStatus.HasChildSession())
            {
                CancelReattach();
                reply(ControlResponse.Success(L.T("分身桌面没有在运行。"), null));
                return;
            }
            var pending = NewPending(reply, CloseWaitTimeoutMs, delegate
            {
                return ControlResponse.Success(L.T("已发出注销请求，分身桌面仍在关闭中。"), null);
            });
            CloseDesktopCore(delegate(bool ok)
            {
                pending.Finish(ok
                    ? ControlResponse.Success(L.T("已关闭分身桌面。"), null)
                    : ControlResponse.Fail(ExitCodes.Failed, L.T("关闭桌面失败，详见日志。")));
            });
        }

        private static bool TryParseSwitch(string arg, bool current, out bool value)
        {
            value = current;
            switch ((arg ?? "").Trim().ToLowerInvariant())
            {
                case "on":
                case "true":
                case "1":
                    value = true;
                    return true;
                case "off":
                case "false":
                case "0":
                    value = false;
                    return true;
                case "toggle":
                    value = !current;
                    return true;
                default:
                    return false;
            }
        }

        private void CmdSwitch(ControlRequest req, Action<ControlResponse> reply, bool viewOnly)
        {
            var p = _settings.GetActiveProfile();
            bool current = viewOnly ? p.ViewOnly : p.AlwaysOnTop;
            bool value;
            if (!TryParseSwitch(req.Positional(0), current, out value))
            {
                reply(ControlResponse.Fail(ExitCodes.Usage,
                    viewOnly ? L.T("用法：--view-only on|off|toggle") : L.T("用法：--topmost on|off|toggle")));
                return;
            }

            string msg;
            if (viewOnly)
            {
                SetViewOnly(value);
                msg = value ? L.T("已开启仅查看。") : L.T("已关闭仅查看。");
            }
            else
            {
                SetAlwaysOnTop(value);
                msg = value ? L.T("已开启窗口置顶。") : L.T("已关闭窗口置顶。");
            }
            if (!DesktopAttached) msg += " " + L.T("（画面没有打开，下次打开时生效。）");
            reply(ControlResponse.Success(msg, null));
        }

        private void CmdRecord(ControlRequest req, Action<ControlResponse> reply)
        {
            string sub = (req.Positional(0) ?? "").Trim().ToLowerInvariant();
            string target = req.Get("target");
            if (target != null) target = target.Trim().ToLowerInvariant();
            if (req.Has("target") && target != "desktop" && target != "monitor")
            {
                reply(ControlResponse.Fail(ExitCodes.Usage, L.T("--target 只能是 desktop 或 monitor。")));
                return;
            }

            switch (sub)
            {
                case "start":
                    reply(RecordStart(target));
                    return;
                case "stop":
                    RecordStop(reply);
                    return;
                case "toggle":
                    if (IsRecording) RecordStop(reply);
                    else reply(RecordStart(target));
                    return;
                case "pause":
                case "resume":
                {
                    if (!IsRecording || _recorder.State != Recording.RecorderState.Recording)
                    {
                        reply(ControlResponse.Fail(ExitCodes.Failed, L.T("当前没有在录制。")));
                        return;
                    }
                    bool want = sub == "pause";
                    if (IsRecordingPaused == want)
                    {
                        reply(ControlResponse.Success(want ? L.T("录制已经是暂停状态。") : L.T("录制没有暂停。"), null));
                        return;
                    }
                    TogglePauseRecording();
                    if (IsRecordingPaused != want)
                    {
                        reply(ControlResponse.Fail(ExitCodes.Failed, L.T("录制器暂时无法切换暂停状态，请稍后再试。")));
                        return;
                    }
                    reply(ControlResponse.Success(want ? L.T("录制已暂停（暂停时长不计入视频）") : L.T("录制已恢复"), null));
                    return;
                }
                default:
                    reply(ControlResponse.Fail(ExitCodes.Usage,
                        L.T("用法：--record start|stop|pause|resume|toggle [--target desktop|monitor]")));
                    return;
            }
        }

        private ControlResponse RecordStart(string target)
        {
            if (IsRecording)
                return ControlResponse.Success(string.Format(L.T("已经在录制中：{0}"), _recordingTargetName),
                    _recorder != null ? PathJson(_recorder.OutputPath) : null);

            var targets = EnumerateCaptureTargets();
            Recording.CaptureTarget pick;
            if (target == "desktop")
            {
                pick = FindWindowTarget(targets, IntPtr.Zero);
                if (pick == null)
                    return ControlResponse.Fail(ExitCodes.Failed, L.T("分身桌面画面没有打开，无法录制它。"));
            }
            else if (target == "monitor")
            {
                pick = PickMonitorTarget(targets);
            }
            else
            {
                pick = FindWindowTarget(targets, IntPtr.Zero);
                if (pick == null)
                    return ControlResponse.Fail(ExitCodes.Failed,
                        L.T("分身桌面画面没有打开。命令行默认只录分身桌面；要录显示器请加 --target monitor。"));
            }
            if (pick == null) return ControlResponse.Fail(ExitCodes.Failed, L.T("没有可录制的目标。"));

            string err = StartRecording(pick);
            if (err != null) return ControlResponse.Fail(ExitCodes.Failed, err);
            return ControlResponse.Success(L.T("开始录制：") + pick.Title,
                _recorder != null ? PathJson(_recorder.OutputPath) : null);
        }

        private void RecordStop(Action<ControlResponse> reply)
        {
            var rec = _recorder;
            if (rec == null || !IsRecording)
            {
                reply(ControlResponse.Success(L.T("当前没有在录制。"), null));
                return;
            }

            string path = rec.OutputPath;
            var pending = NewPending(reply, RecordStopWaitTimeoutMs, delegate
            {
                return ControlResponse.Success(L.T("已请求结束录制，文件仍在收尾：") + path, PathJson(path));
            });
            var entry = new KeyValuePair<Recording.ScreenRecorder, PendingReply>(rec, pending);
            var removePending = pending.Cleanup;
            pending.Cleanup = delegate
            {
                _recordWaiters.Remove(entry);
                if (removePending != null) removePending();
            };
            _recordWaiters.Add(entry);
            StopRecording();
        }

        private void CmdScreenshot(ControlRequest req, Action<ControlResponse> reply)
        {
            string arg = req.Positional(0);
            string folder = null;
            string file = null;
            if (!string.IsNullOrEmpty(arg))
            {
                string full;
                try { full = ResolveClientPath(arg, req.WorkingDirectory); }
                catch (Exception ex)
                {
                    reply(ControlResponse.Fail(ExitCodes.Usage, L.T("路径无效：") + ex.Message));
                    return;
                }
                bool dirLike = arg.EndsWith("\\", StringComparison.Ordinal) || arg.EndsWith("/", StringComparison.Ordinal);
                if (dirLike || Directory.Exists(full))
                {
                    folder = full;
                }
                else
                {
                    file = full;
                    if (string.IsNullOrEmpty(Path.GetExtension(file))) file += ".png";
                }
            }

            var target = FindWindowTarget(EnumerateCaptureTargets(), IntPtr.Zero);
            if (target == null)
            {
                reply(ControlResponse.Fail(ExitCodes.Failed, L.T("分身桌面画面没有打开，无法截图。")));
                return;
            }

            var pending = NewPending(reply, ScreenshotWaitTimeoutMs, delegate
            {
                return ControlResponse.Fail(ExitCodes.Failed, L.T("截图超时，详见日志。"));
            });
            TakeScreenshotCore(target, folder, file, delegate(string path, string err)
            {
                if (pending.IsDone)
                {
                    if (err == null) Log.Warn("截图在命令超时后才完成: " + path);
                    else Log.Warn("截图在命令超时后才失败: " + err);
                    return;
                }
                if (err != null)
                    pending.Finish(ControlResponse.Fail(ExitCodes.Failed, L.T("截图失败：") + err));
                else
                    pending.Finish(ControlResponse.Success(path, PathJson(path)));
            });
        }

        private static string ResolveClientPath(string p, string cwd)
        {
            string s = p.Trim().Trim('"');
            if (!Path.IsPathRooted(s) && !string.IsNullOrEmpty(cwd)) s = Path.Combine(cwd, s);
            return Path.GetFullPath(s);
        }

        private void CmdNotify(ControlRequest req, Action<ControlResponse> reply)
        {
            string title = req.Get("title");
            string body = req.Get("body") ?? req.Positional(0);
            string level = NoticeInfo.NormalizeLevel(req.Get("level"));
            if (level == null)
            {
                reply(ControlResponse.Fail(ExitCodes.Usage, L.T("--level 只能是 info、warn 或 error。")));
                return;
            }
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(body))
            {
                reply(ControlResponse.Fail(ExitCodes.Usage, L.T("提醒内容为空：请用 --body 或第一个位置参数给出正文。")));
                return;
            }
            ShowNotice(title, body, level, req.Has("sound"));
            reply(ControlResponse.Success(L.T("已发出提醒。"), null));
        }

        private void CmdShow(Action<ControlResponse> reply)
        {
            BringMainToFront();
            if (_main != null) reply(ControlResponse.Success(L.T("已唤起主窗口。"), null));
            else reply(ControlResponse.Fail(ExitCodes.Failed, L.T("界面初始化失败，详见日志：") + Log.Path0));
        }

        private void CmdQuit(Action<ControlResponse> reply)
        {
            reply(ControlResponse.Success(IsRecording
                ? L.T("正在结束录制并退出 ParaDesk（分身桌面保留在后台）。")
                : L.T("ParaDesk 正在退出（分身桌面保留在后台）。"), null));

            var t = new WinTimer { Interval = QuitReplyGraceMs };
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                QuitSilently();
            };
            t.Start();
        }

        public void TakeScreenshot(string targetPath, Action<string, string> done)
        {
            TakeScreenshotCore(PickDefaultTarget(EnumerateCaptureTargets()), null, targetPath, done);
        }

        private void TakeScreenshotCore(Recording.CaptureTarget target, string folder, string targetPath, Action<string, string> done)
        {
            Action<string, string> finish = delegate(string path, string err)
            {
                if (err == null && string.IsNullOrEmpty(path)) err = L.T("截图失败，详见日志。");
                if (done == null) return;
                try { done(err == null ? path : null, err); }
                catch (Exception ex) { Log.Error("截图回调异常", ex); }
            };

            if (target == null)
            {
                PostToUi(delegate { finish(null, L.T("没有可截图的目标。")); });
                return;
            }

            var opts = RecOptions;
            string outDir = string.IsNullOrEmpty(folder) ? opts.OutputFolder : folder;
            try
            {
                Recording.Screenshot.CaptureAsync(target, outDir, targetPath, opts.CaptureCursor,
                    delegate(string path, string err) { PostToUi(delegate { finish(path, err); }); });
            }
            catch (Exception ex)
            {
                Log.Error("发起截图失败", ex);
                string msg = ex.Message;
                PostToUi(delegate { finish(null, msg); });
            }
        }

        private NoticeInfo _lastNotice;

        public NoticeInfo LastNotice { get { return _lastNotice; } }

        public void ShowNotice(string title, string body, string level, bool sound)
        {
            if (!OnUiThread())
            {
                PostToUi(delegate { ShowNotice(title, body, level, sound); });
                return;
            }

            var n = new NoticeInfo
            {
                Title = title,
                Body = body,
                Level = NoticeInfo.NormalizeLevel(level) ?? NoticeInfo.LevelInfo,
                Time = DateTime.Now,
            };
            _lastNotice = n;
            Log.Info("提醒[" + n.Level + "]: " + (title ?? "") + " | " + Shorten(body, 200));

            string t = string.IsNullOrEmpty(title) ? AppInfo.ProductName : title;
            string b = string.IsNullOrEmpty(body) ? t : body;
            _tray.Notify(Shorten(t, 60), Shorten(b, 250), n.IsWarning ? ToolTipIcon.Warning : ToolTipIcon.Info);
            _tray.SetAttention(true);
            if (sound)
            {
                try
                {
                    if (n.IsWarning) SystemSounds.Exclamation.Play();
                    else SystemSounds.Asterisk.Play();
                }
                catch (Exception ex) { Log.Debug("播放提醒声音失败: " + ex.Message); }
            }
            RefreshTray();
        }

        public void DismissNotice()
        {
            if (!OnUiThread())
            {
                PostToUi(DismissNotice);
                return;
            }
            if (_lastNotice == null) return;
            _lastNotice = null;
            _tray.SetAttention(false);
            RefreshTray();
        }

        // ---------------- 退出 ----------------

        public void ExitApp()
        {
            if (_exitPhase != ExitPhase.None || _exitConfirming) return;
            _exitConfirming = true;
            try
            {
                // 录制中直接退出会让 MP4 缺少 moov 原子而完全无法播放，
                // 必须先把编码收尾，且要让用户知道在等什么。
                if (IsRecording)
                {
                    var rr = MessageBox.Show(
                        L.T("正在录制中。退出前需要先结束录制并完成文件收尾，否则视频将无法播放。") + "\r\n\r\n" +
                        L.T("确定现在结束录制并退出吗？"),
                        AppInfo.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (rr != DialogResult.Yes) return;
                }

                if (DesktopAttached)
                {
                    var r = MessageBox.Show(
                        string.Format(L.T("分身桌面正在运行。退出 {0} 只会收起画面，"), AppInfo.ProductName) + "\r\n" +
                        L.T("桌面里的程序会继续在后台运行。") + "\r\n\r\n" + L.T("确定退出吗？"),
                        AppInfo.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (r != DialogResult.Yes) return;
                }
            }
            finally { _exitConfirming = false; }

            if (_exitPhase != ExitPhase.None) return;
            Log.Info("用户退出程序");
            BeginExit();
        }

        public void QuitSilently()
        {
            if (_exitPhase != ExitPhase.None) return;
            Log.Info("按命令行请求退出程序");
            BeginExit();
        }

        /// <summary>
        /// 换语言后重启自己。
        ///
        /// 不走 ExitApp：那条路会问"分身桌面还在跑，确定退出吗"——
        /// 换个语言而已，不该逼用户回答这种问题。这里直接收起画面（子会话保留）、
        /// 拉起新实例、退掉自己。新实例带 --restart &lt;pid&gt;，会等这个进程死透再启动，
        /// 否则单实例互斥会把它自己挡回去。
        ///
        /// </summary>
        public void RestartForLanguage()
        {
            if (_exitPhase != ExitPhase.None) return;
            Log.Info("换语言，重启程序");
            _restartPending = true;
            BeginExit();
        }

        private static bool LaunchRestartInstance()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = AppInfo.ExecutablePath,
                    Arguments = "--restart " + CurrentPid,
                    UseShellExecute = true,
                };
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("换语言重启失败", ex);
                return false;
            }
        }

        private void BeginExit()
        {
            CancelReattach();
            StopAutoStart();
            _rolloverTarget = null;

            if (IsRecording)
            {
                _exitPhase = ExitPhase.FinalizingRecording;
                _tray.Notify(AppInfo.ProductName, L.T("正在完成录制文件，请稍候…"), ToolTipIcon.Info);
                StartExitTimeout(RecordingFinalizeTimeoutMs, delegate
                {
                    Log.Warn("等待录制收尾超时，继续退出");
                    ContinueExitAfterRecording();
                });
                StopRecording();
                return;
            }

            CloseSurfaceForExit();
        }

        /// <summary>录制收尾完成（或超时）后走完剩下的退出流程。</summary>
        private void ContinueExitAfterRecording()
        {
            if (_exitPhase != ExitPhase.FinalizingRecording) return;
            StopExitTimeout();
            CloseSurfaceForExit();
        }

        private void CloseSurfaceForExit()
        {
            if (_restartPending)
            {
                _restartPending = false;
                if (!LaunchRestartInstance())
                {
                    // 起不来就别退，否则用户直接失去这个程序
                    _exitPhase = ExitPhase.None;
                    StopExitTimeout();
                    _tray.Notify(AppInfo.ProductName, L.T("重启失败，程序继续运行。新的语言设置将在下次启动时生效。"),
                        ToolTipIcon.Warning);
                    return;
                }
            }

            if (!DesktopAttached)
            {
                CompleteExit();
                return;
            }

            _exitPhase = ExitPhase.ClosingSurface;
            // Close() 会被 DesktopSurface 主动取消并转为异步断开，
            // 这里不能立刻 ExitThread，否则消息循环先死、mstscax 被强拆。
            // 等 Closed2 回来（CompleteExit），并留超时兜底防止永远等下去。
            StartExitTimeout(SurfaceCloseTimeoutMs, delegate
            {
                Log.Warn("等待桌面关闭超时，强制退出");
                CompleteExit();
            });

            _intentionalClose = _surface;
            _surface.Close();

            // 若 Close 未被取消（本就已断开），Closed2 可能已同步走完
            if (!DesktopAttached) CompleteExit();
        }

        private void StartExitTimeout(int ms, Action onTimeout)
        {
            StopExitTimeout();
            _exitTimeout = new WinTimer { Interval = ms };
            _exitTimeout.Tick += delegate
            {
                StopExitTimeout();
                onTimeout();
            };
            _exitTimeout.Start();
        }

        private void StopExitTimeout()
        {
            if (_exitTimeout == null) return;
            _exitTimeout.Stop();
            _exitTimeout.Dispose();
            _exitTimeout = null;
        }

        private void FailPendingReplies(string message)
        {
            foreach (var p in new List<PendingReply>(_pendingReplies))
                p.Finish(ControlResponse.Fail(ExitCodes.Failed, message));
            _pendingReplies.Clear();
        }

        private void CompleteExit()
        {
            if (_exitPhase == ExitPhase.Done) return;
            _exitPhase = ExitPhase.Done;
            Log.Info("退出流程完成");

            StopExitTimeout();
            CancelReattach();
            StopAutoStart();
            StopRecordWatch();
            StopStableTimer();
            _sessionWatch.Stop();
            FailPendingReplies(L.T("ParaDesk 已退出。"));

            _saveTimer.Stop();
            SettingsStore.Save(_settings);
            _stateTimer.Stop();
            try { StateFile.MarkStopped(); }
            catch (Exception ex) { Log.Error("更新状态文件失败", ex); }

            // WPF 窗口不归 WinForms 消息循环管生命周期，退出前必须显式关掉，
            // 否则进程结束时它会以未销毁状态被强拆。
            try
            {
                if (_main != null) { _main.Close(); _main = null; }
                if (System.Windows.Application.Current != null)
                    System.Windows.Application.Current.Shutdown();
            }
            catch (Exception ex) { Log.Error("关闭 WPF 界面失败", ex); }

            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                bool abnormal = _exitPhase != ExitPhase.Done;
                _exitPhase = ExitPhase.Done;

                if (abnormal) FailPendingReplies(L.T("ParaDesk 已退出。"));

                StopExitTimeout();
                StopReattachTimer();
                StopAutoStart();
                if (_stableTimer != null) { _stableTimer.Dispose(); _stableTimer = null; }
                if (_recordWatch != null) { _recordWatch.Dispose(); _recordWatch = null; }
                if (_saveTimer != null) _saveTimer.Dispose();
                if (_stateTimer != null) _stateTimer.Dispose();
                if (_sessionWatch != null) _sessionWatch.Dispose();

                if (abnormal)
                {
                    try { SettingsStore.Save(_settings); }
                    catch (Exception ex) { Log.Error("退出时保存设置失败", ex); }
                    try { StateFile.MarkStopped(); }
                    catch (Exception ex) { Log.Error("退出时更新状态文件失败", ex); }
                }

                // 兜底：任何未走正常退出路径的情况下也要让录制收尾，
                // 否则 MP4 会缺 moov 原子而无法播放
                if (_recorder != null)
                {
                    try { _recorder.Dispose(); }
                    catch (Exception ex) { Log.Error("退出时收尾录制失败", ex); }
                    _recorder = null;
                }

                if (_surface != null && !_surface.IsDisposed)
                {
                    var s = _surface;
                    _surface = null;
                    s.StateChanged -= OnSurfaceStateChanged;
                    s.Closed2 -= OnSurfaceClosed;
                    s.BoundsSaved -= OnSurfaceBoundsSaved;
                    try { s.Close(); }
                    catch (Exception ex) { Log.Error("退出时关闭画面失败", ex); }
                    try { s.Dispose(); }
                    catch (Exception ex) { Log.Error("退出时释放画面失败", ex); }
                }
                if (_main != null)
                {
                    try { _main.Close(); }
                    catch (Exception ex) { Log.Error("退出时关闭主窗口失败", ex); }
                    _main = null;
                }

                if (_hotkeys != null) _hotkeys.Dispose();
                if (_shutdown != null) _shutdown.Dispose();
                if (_monitors != null) _monitors.Dispose();
                if (_tray != null) _tray.Dispose();
                if (_uiAnchor != null) _uiAnchor.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
