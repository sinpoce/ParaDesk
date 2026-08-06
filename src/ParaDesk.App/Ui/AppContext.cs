using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ParaDesk.Core;
using ParaDesk.Elevated;
using ParaDesk.Native;
using ParaDesk.Rdp;

namespace ParaDesk.Ui
{
    /// <summary>
    /// 应用主控。持有托盘、热键、关机守卫与当前桌面窗口，是所有操作的唯一入口。
    /// 用 ApplicationContext 而非主窗体作为生命周期锚点：主窗体可以关掉缩到托盘，
    /// 进程仍需存活。
    /// </summary>
    internal class AppContext : ApplicationContext
    {
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

        private bool _exiting;
        private bool _exitDone;
        private bool _setupRunning;
        private System.Windows.Forms.Timer _exitTimeout;

        public AppSettings Settings { get { return _settings; } }

        /// <summary>首次配置子进程是否在执行中。放在这里而非主窗体，因为主窗体可能被关掉重建。</summary>
        public bool SetupRunning { get { return _setupRunning; } }

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

        public bool IsRecording
        {
            get { return _recorder != null && _recorder.State != Recording.RecorderState.Idle; }
        }

        public string RecordingTargetName { get { return _recordingTargetName; } }

        public DateTime? RecordingStartedAt
        {
            get { return IsRecording ? _recorder.StartedAt : (DateTime?)null; }
        }

        /// <summary>当前可录制的目标：打开着的分身桌面窗口 + 每块显示器。</summary>
        public System.Collections.Generic.List<Recording.CaptureTarget> EnumerateCaptureTargets()
        {
            var windows = new System.Collections.Generic.List<
                System.Collections.Generic.KeyValuePair<IntPtr, string>>();

            if (DesktopAttached && _surface.IsHandleCreated)
            {
                var mon = _surface.Monitor;
                string name = L.T("分身桌面") + (mon != null ? "（" + MonitorNaming.NameOf(mon) + "）" : "");
                windows.Add(new System.Collections.Generic.KeyValuePair<IntPtr, string>(
                    _surface.Handle, name));
            }
            return Recording.CaptureTargetEnumerator.Enumerate(windows);
        }

        /// <summary>开始录制。返回 null 表示成功，否则为错误说明。</summary>
        public string StartRecording(Recording.CaptureTarget target)
        {
            if (IsRecording) return L.T("已经在录制中。");
            if (target == null) return L.T("未选择录制目标。");

            _recorder = new Recording.ScreenRecorder();
            _recorder.Stopped += OnRecordingStopped;

            string err = _recorder.Start(target, _settings.Recording);
            if (err != null)
            {
                _recorder.Dispose();
                _recorder = null;
                return err;
            }
            _recordingTargetName = target.Title;
            RefreshTray();

            // 音频降级不阻断录制，但必须让用户知道这段录像是无声的
            if (_recorder.AudioWarning != null)
                _tray.Notify(AppInfo.ProductName,
                    L.T("音频不可用，本次为无声录制：") + _recorder.AudioWarning, ToolTipIcon.Warning);
            return null;
        }

        public void StopRecording()
        {
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
            RefreshTray();
            _tray.Notify(AppInfo.ProductName,
                pause ? L.T("录制已暂停（暂停时长不计入视频）") : L.T("录制已恢复"), ToolTipIcon.Info);
        }

        /// <summary>
        /// 热键/托盘用的起停切换。未指定目标时自动选：
        /// 有分身桌面就录它，否则录桌面所在的显示器——这是热键场景下最符合直觉的默认。
        /// </summary>
        public void ToggleRecording()
        {
            if (IsRecording)
            {
                StopRecording();
                _tray.Notify(AppInfo.ProductName, L.T("正在结束录制…"), ToolTipIcon.Info);
                return;
            }

            var targets = EnumerateCaptureTargets();
            if (targets.Count == 0)
            {
                _tray.Notify(AppInfo.ProductName, L.T("没有可录制的目标。"), ToolTipIcon.Warning);
                return;
            }

            Recording.CaptureTarget pick = null;
            foreach (var t in targets)
                if (t.Kind == Recording.CaptureTargetKind.Window) { pick = t; break; }

            if (pick == null)
            {
                // 没有分身桌面，就录当前配置指向的那块屏
                var device = _settings.GetActiveProfile().MonitorDevice;
                var mon = MonitorService.Resolve(device);
                foreach (var t in targets)
                {
                    if (t.Kind != Recording.CaptureTargetKind.Monitor) continue;
                    if (mon != null && t.DeviceName == mon.DeviceName) { pick = t; break; }
                    if (pick == null) pick = t;
                }
            }

            string err = StartRecording(pick);
            _tray.Notify(AppInfo.ProductName,
                err ?? (L.T("开始录制：") + pick.Title),
                err == null ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }

        private void OnRecordingStopped(object sender, Recording.RecordingStoppedEventArgs e)
        {
            PostToUi(delegate
            {
                _recordingTargetName = null;
                if (_recorder != null) { _recorder.Dispose(); _recorder = null; }
                RefreshTray();

                // 退出流程正等着录制收尾
                if (_exiting && !_exitDone) { ContinueExitAfterRecording(); return; }

                if (_main != null) _main.OnRecordingFinished(e.FilePath, e.Error);

                if (e.Error == null)
                    _tray.Notify(AppInfo.ProductName,
                        string.Format(L.T("录制完成（{0}）"), e.Duration.ToString(@"hh\:mm\:ss")),
                        ToolTipIcon.Info);
                else
                    _tray.Notify(AppInfo.ProductName, L.T("录制未成功：") + e.Error, ToolTipIcon.Warning);
            });
        }

        public AppContext(bool startMinimized)
        {
            _uiAnchor = new Control();
            IntPtr force = _uiAnchor.Handle;   // 必须在 UI 线程上真正建出句柄
            GC.KeepAlive(force);

            _settings = SettingsStore.Load();
            MonitorNaming.Bind(_settings);   // 显示器自定义名要在任何界面构建之前可用

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
            _tray.IdentifyRequested += delegate { Shell.IdentifyOverlay.Show(3); };
            _tray.ExitRequested += delegate { ExitApp(); };

            _hotkeys = new HotkeyService();
            _hotkeys.Pressed += OnHotkey;
            _hotkeys.Apply(_settings.Hotkeys);

            _shutdown = new ShutdownGuard();
            _shutdown.Cleanup += OnSystemShutdown;

            RefreshTray();

            if (!startMinimized)
            {
                // 首次运行先走向导；用户跳过或走完后不再弹
                if (!_settings.WizardShown) ShowWizard();
                ShowMain();
            }
        }

        // ---------------- 主窗口 ----------------

        /// <summary>退出流程进行中。WPF 窗口据此判断关闭是"收进托盘"还是"真的关"。</summary>
        public bool IsExiting { get { return _exiting; } }

        /// <summary>
        /// 另一个实例被启动时的响应：把主窗口唤到前台。
        /// 由命名管道监听线程调用，必须切回 UI 线程。
        /// </summary>
        public void ActivateFromOtherInstance()
        {
            PostToUi(delegate
            {
                Log.Info("收到激活请求，唤起主窗口");
                ShowMain();
                if (_main != null)
                {
                    // Topmost 抖一下是唤到最前的可靠做法：
                    // 前台窗口切换有系统限制，仅调 Activate() 常常只会闪任务栏
                    bool old = _main.Topmost;
                    _main.Topmost = true;
                    _main.Topmost = old;
                }
            });
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
            string err = StartDesktop();
            if (err != null)
                _tray.Notify(AppInfo.ProductName, err, ToolTipIcon.Warning);
        }

        public string StartDesktop()
        {
            if (DesktopAttached) { _surface.Activate(); return null; }

            var env = SystemStatus.Check();
            if (!env.ReadyToStart)
            {
                string msg = env.NextAction ?? L.T("环境尚未就绪。");
                Log.Warn("启动被拒绝: " + msg);
                return msg;
            }

            var profile = _settings.GetActiveProfile();
            var monitor = MonitorService.Resolve(profile.MonitorDevice);
            if (monitor == null) return L.T("未检测到可用显示器。");

            try
            {
                _surface = new DesktopSurface(profile, monitor);
                _surface.StateChanged += delegate { RefreshTray(); };
                _surface.Closed2 += OnSurfaceClosed;
                _surface.Show();

                _shutdown.SetBlockReason(L.T("正在关闭分身桌面，请稍候…"));
                RefreshTray();
                SystemStatus.Invalidate();
                Log.Info("桌面已启动于 " + monitor.DeviceName);
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("启动桌面失败", ex);
                _surface = null;
                RefreshTray();
                return L.T("启动失败：") + ex.Message;
            }
        }

        /// <summary>收起画面，子会话保留在后台继续运行。</summary>
        public void DetachDesktop()
        {
            if (!DesktopAttached) return;
            SystemStatus.Invalidate();
            Log.Info("收起桌面（子会话保留）");
            _surface.Close();
        }

        /// <summary>彻底关闭：注销子会话，里面的程序全部结束。</summary>
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

            if (DesktopAttached) _surface.Close();

            uint id = SystemStatus.ChildSessionId();
            if (id == NativeMethods.NoChildSession || id == 0) { RefreshTray(); return; }

            // 注销要等桌面内所有程序退出，可能长达数十秒，必须放后台线程
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = NativeMethods.WTSLogoffSession(IntPtr.Zero, id, true);
                int err = ok ? 0 : Marshal.GetLastWin32Error();
                SystemStatus.Invalidate();
                Log.Info("注销子会话 " + id + " => " + ok + (ok ? "" : " Win32Error=" + err));
                PostToUi(delegate
                {
                    _shutdown.SetBlockReason(null);
                    RefreshTray();
                    if (!ok)
                        _tray.Notify(AppInfo.ProductName, L.T("关闭桌面失败，详见日志。"), ToolTipIcon.Warning);
                });
            });
        }

        private void OnSurfaceClosed(object sender, SurfaceClosedEventArgs e)
        {
            _surface = null;
            SystemStatus.Invalidate();
            _shutdown.SetBlockReason(SystemStatus.HasChildSession() ? L.T("分身桌面仍在运行") : null);
            RefreshTray();

            // 退出流程正等着这一刻
            if (_exiting) { CompleteExit(); return; }

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
            if (DesktopAttached) _surface.SetViewOnly(on);
            SettingsStore.Save(_settings);
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
            if (DesktopAttached) _surface.SetAlwaysOnTop(on);
            SettingsStore.Save(_settings);
            RefreshTray();
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

        /// <summary>把显示位置/分辨率的改动实时推给正在运行的桌面。</summary>
        public void ApplyDisplayChanges()
        {
            if (!DesktopAttached) return;
            var p = _settings.GetActiveProfile();
            var monitor = MonitorService.Resolve(p.MonitorDevice);
            if (monitor != null) _surface.MoveToMonitor(monitor);
            _surface.SyncResolutionToWindow();
        }

        // ---------------- 首次配置 ----------------

        public void RunSetup(Action<SetupResult> done)
        {
            if (_setupRunning) return;
            _setupRunning = true;
            RefreshTray();

            string logDir = Log.Dir;
            ThreadPool.QueueUserWorkItem(delegate
            {
                SetupResult result;
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = AppInfo.ExecutablePath,
                        Arguments = "--setup \"" + logDir + "\"",
                        Verb = "runas",
                        UseShellExecute = true,
                    };
                    using (var p = Process.Start(psi))
                    {
                        p.WaitForExit();
                        result = (SetupResult)p.ExitCode;
                    }
                }
                catch (System.ComponentModel.Win32Exception) { result = SetupResult.Cancelled; }
                catch (Exception ex)
                {
                    Log.Error("首次配置失败", ex);
                    result = SetupResult.Failed;
                }

                SystemStatus.Invalidate();   // 配置刚改过，缓存必须作废
                PostToUi(delegate
                {
                    _setupRunning = false;
                    RefreshTray();
                    // 主窗体可能已被关掉，结果仍必须让用户看到
                    bool reported = false;
                    if (done != null) reported = done2(done, result);
                    if (!reported) ReportSetupResult(result);
                });
            });
        }

        /// <summary>启用 Windows 沙盒可选功能（需提权，装完要重启）。</summary>
        public void RunEnableSandbox(Action<SetupResult> done)
        {
            RunElevatedCore("--sandbox", done);
        }

        /// <summary>帧率上限写在 HKLM，同样要走提权子进程。</summary>
        public void RunSetupFps(int fps, Action<bool> done)
        {
            RunElevated("--fps " + fps, done);
        }

        /// <summary>跑一次只做单项设置的提权子进程，回调只关心成没成。</summary>
        private void RunElevated(string extraArgs, Action<bool> done)
        {
            RunElevatedCore(extraArgs, delegate(SetupResult r)
            {
                if (done != null) done(r == SetupResult.Success);
            });
        }

        /// <summary>
        /// 同上，但把真实结果透给调用方。
        /// 装 Windows 沙盒这类操作成功后返回的是 RebootRequired 而不是 Success，
        /// 只看 bool 会把"装好了、等重启"误报成失败。
        /// </summary>
        private void RunElevatedCore(string extraArgs, Action<SetupResult> done)
        {
            string logDir = Log.Dir;
            ThreadPool.QueueUserWorkItem(delegate
            {
                SetupResult res;
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = AppInfo.ExecutablePath,
                        Arguments = "--setup \"" + logDir + "\" " + extraArgs,
                        Verb = "runas",
                        UseShellExecute = true,
                    };
                    using (var p = Process.Start(psi))
                    {
                        p.WaitForExit();
                        res = (SetupResult)p.ExitCode;
                    }
                }
                catch (System.ComponentModel.Win32Exception) { res = SetupResult.Cancelled; }   // 用户拒绝了 UAC
                catch (Exception ex) { Log.Error("提权设置失败: " + extraArgs, ex); res = SetupResult.Failed; }

                PostToUi(delegate { if (done != null) done(res); });
            });
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

        // ---------------- 事件 ----------------

        private void OnHotkey(object sender, HotkeyPressedEventArgs e)
        {
            switch (e.Action)
            {
                case "toggleDesktop":
                    if (DesktopAttached) DetachDesktop();
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
            }
        }

        private void OnMonitorLayoutChanged(object sender, EventArgs e)
        {
            // 显示器组合变了，先看看有没有更合适的方案可以自动套用
            var devices = new System.Collections.Generic.List<string>();
            foreach (var m in MonitorService.Enumerate()) devices.Add(m.DeviceName);

            var picked = _settings.PickForCurrentLayout(devices);
            if (picked != null && !string.Equals(picked.Name, _settings.ActiveProfile, StringComparison.Ordinal))
            {
                _settings.ActiveProfile = picked.Name;
                SettingsStore.Save(_settings);
                Log.Info("显示器变化，已自动切换到方案: " + picked.Name);
                _tray.Notify(AppInfo.ProductName,
                    string.Format(L.T("显示器已变化，自动套用方案「{0}」。"), picked.Name), ToolTipIcon.Info);
            }

            if (_main != null) _main.RefreshAll();
            if (!DesktopAttached) return;

            var profile = _settings.GetActiveProfile();
            var monitor = MonitorService.Resolve(profile.MonitorDevice);
            if (monitor == null) return;

            _surface.MoveToMonitor(monitor);
            _tray.Notify(AppInfo.ProductName,
                string.Format(L.T("分身桌面已移动到 {0}。"), MonitorNaming.NameOf(monitor)), ToolTipIcon.Info);
        }

        private void OnSystemShutdown(object sender, EventArgs e)
        {
            if (!_settings.AutoLogoffOnShutdown) return;
            uint id = SystemStatus.ChildSessionId();
            if (id == NativeMethods.NoChildSession || id == 0) return;

            Log.Info("关机前注销子会话 " + id);
            // 已登记 ShutdownBlockReason，系统给到约 30 秒，同步等待是安全的
            try { NativeMethods.WTSLogoffSession(IntPtr.Zero, id, true); }
            catch (Exception ex) { Log.Error("关机前注销失败", ex); }
        }

        // ---------------- 辅助 ----------------

        public void RefreshTray()
        {
            bool attached = DesktopAttached;
            bool exists = SystemStatus.HasChildSession();
            var p = _settings.GetActiveProfile();

            // 这里刻意不做通道自检：ProbeTransport 会真的创建子会话传输通道，
            // 属于有副作用的调用，不该由每次状态刷新触发。
            bool canStart = !attached && !SystemStatus.IsHomeEdition()
                            && SystemStatus.ChildSessionsEnabled()
                            && SystemStatus.TermServiceRunning();

            _tray.UpdateState(attached, exists, canStart, p.ViewOnly, p.AlwaysOnTop, IsRecording);

            if (_main != null) _main.RefreshAll();
        }

        /// <summary>重新注册热键。返回注册失败的动作列表（多半是被别的程序占用）。</summary>
        public System.Collections.Generic.List<string> ApplyHotkeys()
        {
            return _hotkeys.Apply(_settings.Hotkeys);
        }

        /// <summary>
        /// 把回调切回 UI 线程。绝不就地执行——那会让托盘菜单等 UI 对象
        /// 在线程池线程上被改写。锚点不可用时宁可丢弃并记录。
        /// </summary>
        private void PostToUi(Action a)
        {
            try
            {
                if (_uiAnchor != null && !_uiAnchor.IsDisposed && _uiAnchor.IsHandleCreated)
                {
                    _uiAnchor.BeginInvoke((MethodInvoker)delegate { a(); });
                    return;
                }
                Log.Warn("UI 锚点不可用，回调已丢弃");
            }
            catch (Exception ex) { Log.Error("回到 UI 线程失败", ex); }
        }

        // ---------------- 退出 ----------------

        public void ExitApp()
        {
            if (_exiting) return;

            // 录制中直接退出会让 MP4 缺少 moov 原子而完全无法播放，
            // 必须先把编码收尾，且要让用户知道在等什么。
            if (IsRecording)
            {
                var rr = MessageBox.Show(
                    L.T("正在录制中。退出前需要先结束录制并完成文件收尾，否则视频将无法播放。") + "\r\n\r\n" +
                    L.T("确定现在结束录制并退出吗？"),
                    AppInfo.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (rr != DialogResult.Yes) return;

                _exiting = true;
                _tray.Notify(AppInfo.ProductName, L.T("正在完成录制文件，请稍候…"), ToolTipIcon.Info);
                StopRecording();

                // 收尾完成后由 OnRecordingStopped 继续退出流程；留超时兜底
                _exitTimeout = new System.Windows.Forms.Timer { Interval = 20000 };
                _exitTimeout.Tick += delegate
                {
                    Log.Warn("等待录制收尾超时，强制退出");
                    ContinueExitAfterRecording();
                };
                _exitTimeout.Start();
                return;
            }

            ExitAfterRecordingDone();
        }

        /// <summary>录制收尾完成（或超时）后走完剩下的退出流程。</summary>
        private void ContinueExitAfterRecording()
        {
            if (_exitTimeout != null) { _exitTimeout.Stop(); _exitTimeout.Dispose(); _exitTimeout = null; }
            _exiting = false;          // 让后续分支能正常判定
            ExitAfterRecordingDone();
            if (!_exitDone && !DesktopAttached) { _exiting = true; CompleteExit(); }
        }

        /// <summary>
        /// 换语言后重启自己。
        ///
        /// 不走 ExitApp：那条路会问"分身桌面还在跑，确定退出吗"——
        /// 换个语言而已，不该逼用户回答这种问题。这里直接收起画面（子会话保留）、
        /// 拉起新实例、退掉自己。新实例带 --restart &lt;pid&gt;，会等这个进程死透再启动，
        /// 否则单实例互斥会把它自己挡回去。
        /// </summary>
        public void RestartForLanguage()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = AppInfo.ExecutablePath,
                    Arguments = "--restart " + Process.GetCurrentProcess().Id,
                    UseShellExecute = true,
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log.Error("换语言重启失败", ex);
                return;   // 起不来就别退，否则用户直接失去这个程序
            }

            Log.Info("换语言，重启程序");
            _exiting = true;
            if (DesktopAttached) _surface.Close();   // 只收画面，子会话留着
            CompleteExit();
        }

        /// <summary>录制已收尾（或无录制），继续正常的退出流程。</summary>
        private void ExitAfterRecordingDone()
        {
            if (DesktopAttached)
            {
                var r = MessageBox.Show(
                    string.Format(L.T("分身桌面正在运行。退出 {0} 只会收起画面，"), AppInfo.ProductName) + "\r\n" +
                    L.T("桌面里的程序会继续在后台运行。") + "\r\n\r\n" + L.T("确定退出吗？"),
                    AppInfo.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;

                _exiting = true;
                // Close() 会被 DesktopSurface 主动取消并转为异步断开，
                // 这里不能立刻 ExitThread，否则消息循环先死、mstscax 被强拆。
                // 等 Closed2 回来（CompleteExit），并留超时兜底防止永远等下去。
                _exitTimeout = new System.Windows.Forms.Timer { Interval = 6000 };
                _exitTimeout.Tick += delegate
                {
                    Log.Warn("等待桌面关闭超时，强制退出");
                    CompleteExit();
                };
                _exitTimeout.Start();

                _surface.Close();

                // 若 Close 未被取消（本就已断开），Closed2 可能已同步走完
                if (!DesktopAttached) CompleteExit();
                return;
            }

            _exiting = true;
            CompleteExit();
        }

        private void CompleteExit()
        {
            if (_exitDone) return;
            _exitDone = true;

            if (_exitTimeout != null) { _exitTimeout.Stop(); _exitTimeout.Dispose(); _exitTimeout = null; }
            SettingsStore.Save(_settings);

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
                // 兜底：任何未走正常退出路径的情况下也要让录制收尾，
                // 否则 MP4 会缺 moov 原子而无法播放
                if (_recorder != null)
                {
                    try { _recorder.Dispose(); }
                    catch (Exception ex) { Log.Error("退出时收尾录制失败", ex); }
                    _recorder = null;
                }
                if (_exitTimeout != null) { _exitTimeout.Dispose(); _exitTimeout = null; }
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
