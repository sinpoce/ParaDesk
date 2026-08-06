using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ParaDesk.Core;
using ParaDesk.Native;

namespace ParaDesk.Rdp
{
    internal enum SurfaceState
    {
        Idle,
        Connecting,
        Connected,
        Reconnecting,
        Closing,
    }

    internal class SurfaceClosedEventArgs : EventArgs
    {
        /// <summary>连接曾经建立过。false 表示从未连上，属于首次配置类故障。</summary>
        public bool EverConnected { get; private set; }
        /// <summary>面向用户的原因说明；null 表示正常关闭。</summary>
        public string Reason { get; private set; }
        public SurfaceClosedEventArgs(bool ever, string reason) { EverConnected = ever; Reason = reason; }
    }

    /// <summary>
    /// 承载分身桌面画面的窗口。按规划，RDP 画面永远放在独立的顶层 WinForms 窗口里，
    /// 这样彻底避开 WPF/WinForms 混排的 airspace 遮挡与 Per-Monitor DPI 缩放问题。
    /// </summary>
    internal class DesktopSurface : Form
    {
        private RdpHostControl _host;
        private RdpEventSink _sink;
        private dynamic _ocx;
        private ViewOnlyGuard _viewOnly;
        private Label _overlayText;

        private readonly Timer _watchdog;      // 兜底：事件缺失时仍能发现连接失败
        private readonly Timer _resizeDebounce; // 窗口拖动结束后再改远端分辨率
        private readonly Timer _loginSettle;    // 登录完成后延迟应用显示设置

        private SurfaceState _state = SurfaceState.Idle;
        private bool _everConnected;
        private bool _closePending;
        private int _watchdogTicks;
        private int _closeDeadline;
        private bool _loggedIn;

        private DesktopProfile _profile;
        private MonitorInfo _monitor;
        private int _pendingWidth, _pendingHeight;

        public SurfaceState State { get { return _state; } }
        public bool ViewOnly { get { return _viewOnly != null && _viewOnly.IsActive; } }

        /// <summary>远端当前实际生效的分辨率（0 表示尚未确定）。</summary>
        public int CurrentWidth { get { return _pendingWidth; } }
        public int CurrentHeight { get { return _pendingHeight; } }

        /// <summary>连接建立的时刻，用于显示运行时长。</summary>
        public DateTime? ConnectedAt { get; private set; }

        /// <summary>画面所在的显示器。</summary>
        public MonitorInfo Monitor { get { return _monitor; } }

        public event EventHandler StateChanged;
        public event EventHandler<SurfaceClosedEventArgs> Closed2;

        public DesktopSurface(DesktopProfile profile, MonitorInfo monitor)
        {
            _profile = profile;
            _monitor = monitor;

            Text = AppInfo.Title;
            BackColor = Color.Black;
            StartPosition = FormStartPosition.Manual;
            // ShowInTaskbar 只能在句柄创建前设定：它的 setter 会 RecreateHandle()，
            // 那会摧毁窗口句柄，并把寄宿其中的 RDP 控件一起带走、连接直接掉线。
            ShowInTaskbar = true;
            KeyPreview = false;

            ApplyWindowMode();

            _overlayText = new Label
            {
                AutoSize = true,
                ForeColor = Color.White,
                BackColor = Color.Black,
                Font = new Font("Microsoft YaHei UI", 12F),
                Location = new Point(48, 48),
                Text = L.T("正在连接分身桌面……") + "\r\n" +
                       L.T("首次连接会为你的账户建立第二个登录，可能需要一两分钟。"),
            };
            Controls.Add(_overlayText);

            _watchdog = new Timer { Interval = 1000 };
            _watchdog.Tick += OnWatchdogTick;

            _resizeDebounce = new Timer { Interval = 500 };
            _resizeDebounce.Tick += OnResizeSettled;

            _loginSettle = new Timer { Interval = 1500 };
            _loginSettle.Tick += OnLoginSettled;

            Shown += OnShownFirst;
            FormClosing += OnFormClosingInternal;
            // 兜底：无论走哪条关闭路径，窗体真正关掉后一定要通知外界，
            // 否则 AppContext 会一直以为桌面还在，关机阻止原因也摘不掉。
            FormClosed += delegate { FinishClose(_everConnected, null); };
            ResizeEnd += delegate { RestartResizeDebounce(); };
        }

        // ---------------- 窗口模式 ----------------

        private void ApplyWindowMode()
        {
            Rectangle b = _monitor.Bounds;
            switch (_profile.WindowMode)
            {
                case WindowMode.Fullscreen:
                    FormBorderStyle = FormBorderStyle.None;
                    Bounds = b;
                    break;

                case WindowMode.Pip:
                    FormBorderStyle = FormBorderStyle.SizableToolWindow;
                    int w = Math.Max(480, b.Width / 3);
                    int h = Math.Max(320, b.Height / 3);
                    Bounds = new Rectangle(b.Right - w - 40, b.Bottom - h - 60, w, h);
                    break;

                default: // Windowed
                    FormBorderStyle = FormBorderStyle.Sizable;
                    Bounds = new Rectangle(b.X + 60, b.Y + 60,
                        Math.Max(800, b.Width - 240), Math.Max(600, b.Height - 240));
                    break;
            }
            TopMost = _profile.AlwaysOnTop || _profile.WindowMode == WindowMode.Pip;
        }

        protected override void WndProc(ref Message m)
        {
            // 置顶的可靠做法：在 WM_WINDOWPOSCHANGING 里把 hwndInsertAfter 钳回 HWND_TOPMOST。
            // 这是无竞态的，比起定时器反复 SetWindowPos 既省电又不会抢焦点。
            if (m.Msg == NativeMethods.WM_WINDOWPOSCHANGING && TopMost)
            {
                var wp = (NativeMethods.WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(NativeMethods.WINDOWPOS));
                if ((wp.flags & NativeMethods.SWP_NOZORDER) == 0)
                {
                    wp.hwndInsertAfter = NativeMethods.HWND_TOPMOST;
                    Marshal.StructureToPtr(wp, m.LParam, false);
                }
            }
            base.WndProc(ref m);
        }

        // ---------------- 连接 ----------------

        private void OnShownFirst(object sender, EventArgs e)
        {
            Shown -= OnShownFirst;
            try
            {
                _sink = new RdpEventSink();
                _sink.Connecting += delegate { SetState(SurfaceState.Connecting); };
                _sink.Connected += OnRdpConnected;
                _sink.LoginComplete += OnRdpLoginComplete;
                _sink.Disconnected += OnRdpDisconnected;
                _sink.Reconnecting += OnRdpReconnecting;
                _sink.Reconnected += OnRdpReconnected;
                _sink.ConfirmClose = delegate { return true; };

                _host = new RdpHostControl { Dock = DockStyle.Fill, Sink = _sink };
                Controls.Add(_host);
                _host.CreateControl();
                _ocx = _host.Ocx;

                _viewOnly = new ViewOnlyGuard(this, _host);
                // 必须在 Connect 之前就武装：否则连接与登录的整个过程里
                // 用户的物理键鼠都能打进子会话，而界面上「仅查看」是勾着的。
                if (_profile.ViewOnly) _viewOnly.SetActive(true);

                Configure();
                SetState(SurfaceState.Connecting);
                _ocx.Connect();
                _watchdog.Start();
                Log.Info("开始连接分身桌面，目标屏幕 " + _monitor.DeviceName + " " + Bounds);
            }
            catch (Exception ex)
            {
                Log.Error("初始化 RDP 控件失败", ex);
                FinishClose(false, L.T("启动失败：") + ex.Message);
            }
        }

        private void Configure()
        {
            Rectangle b = ClientRectangle;
            int w = EvenWidth(Math.Max(200, b.Width));
            int h = Math.Max(200, b.Height);
            if (_profile.ResolutionMode == ResolutionMode.Custom)
            {
                w = EvenWidth(Clamp(_profile.CustomWidth, 200, 8192));
                h = Clamp(_profile.CustomHeight, 200, 8192);
            }

            _ocx.Server = "localhost";
            Try("DesktopWidth", delegate { _ocx.DesktopWidth = w; });
            Try("DesktopHeight", delegate { _ocx.DesktopHeight = h; });
            Try("ColorDepth", delegate { _ocx.ColorDepth = 32; });

            dynamic adv = GetAdvanced();
            if (adv != null)
            {
                Try("EnableCredSspSupport", delegate { adv.EnableCredSspSupport = true; });
                Try("AuthenticationLevel", delegate { adv.AuthenticationLevel = 0; });
                // 容器自己管全屏，控件就不会再创建一个与我们抢置顶的全屏窗口
                Try("ContainerHandledFullScreen", delegate { adv.ContainerHandledFullScreen = 1; });
                Try("DisplayConnectionBar", delegate { adv.DisplayConnectionBar = false; });
                // 关闭：完全不重定向；共享/手动：都需开启重定向，
                // 区别在于「手动」会额外要求控件停止自动同步（见下方 ManualClipboardSyncEnabled）
                Try("RedirectClipboard", delegate
                {
                    adv.RedirectClipboard = _profile.Clipboard != ClipboardMode.Off;
                });
                Try("SmartSizing", delegate { adv.SmartSizing = true; });
                Try("EnableAutoReconnect", delegate { adv.EnableAutoReconnect = true; });
                Try("MaxReconnectAttempts", delegate { adv.MaxReconnectAttempts = 20; });
                // 环回带宽是免费的，画质相关的开关全开
                Try("PerformanceFlags", delegate { adv.PerformanceFlags = 0x00000190; });
                if (_profile.ViewOnly)
                    Try("GrabFocusOnConnect", delegate { adv.GrabFocusOnConnect = false; });
                Try("allowBackgroundInput", delegate { adv.allowBackgroundInput = 0; });
            }

            // 可选的已保存凭据。正常情况下子会话会用父会话身份自动登录，
            // 只有在「允许委派默认凭据」策略也无法生效时（如仅 PIN 登录的账户）才需要。
            if (CredentialStore.Exists)
            {
                string user, pwd;
                if (CredentialStore.TryLoad(out user, out pwd))
                {
                    Try("UserName", delegate { _ocx.UserName = user; });
                    if (adv != null)
                        Try("ClearTextPassword", delegate { adv.ClearTextPassword = pwd; });
                    Log.Info("已使用保存的凭据登录分身桌面");
                }
            }

            Try("KeyboardHookMode", delegate { _ocx.SecuredSettings2.KeyboardHookMode = 1; });

            // IMsRdpExtendedSettings 派生自 IUnknown 而非 dual，dynamic 会抛
            // RuntimeBinderException，必须走 ComImport 强转。
            var ext = (IMsRdpExtendedSettings)_host.Ocx;
            object vTrue = true;
            ext.set_Property("ConnectToChildSession", ref vTrue);

            // 手动模式：关掉自动同步，改由用户显式推送，避免两边剪贴板互相覆盖
            if (_profile.Clipboard == ClipboardMode.Manual)
            {
                Try("ManualClipboardSyncEnabled", delegate
                {
                    object v = true;
                    ext.set_Property("ManualClipboardSyncEnabled", ref v);
                });
            }

            Try("EnableHardwareMode", delegate { object v = true; ext.set_Property("EnableHardwareMode", ref v); });
            // 环回专用：允许帧缓冲内存共享
            Try("EnableFrameBufferRedirection", delegate { object v = true; ext.set_Property("EnableFrameBufferRedirection", ref v); });

            Try("ScaleFactors", delegate
            {
                uint desktopScale, deviceScale;
                ResolveScale(out desktopScale, out deviceScale);
                object ds = desktopScale, dev = deviceScale;
                ext.set_Property("DesktopScaleFactor", ref ds);
                ext.set_Property("DeviceScaleFactor", ref dev);
            });
        }

        /// <summary>
        /// 缩放取值受协议约束：DesktopScaleFactor 100–500，DeviceScaleFactor 只能是
        /// 100/140/180，且两者必须同时有效，否则整组被忽略。
        /// </summary>
        private void ResolveScale(out uint desktopScale, out uint deviceScale)
        {
            int pct = _profile.ScalePercent;
            if (pct <= 0)
            {
                uint dpi = 96;
                try { if (IsHandleCreated) dpi = NativeMethods.GetDpiForWindow(Handle); }
                catch { }
                if (dpi == 0) dpi = 96;
                pct = (int)(dpi * 100 / 96);
            }
            desktopScale = (uint)Clamp(pct, 100, 500);
            // Devolutions 的实测映射：125% → 140；150–175% → 140；200%+ → 180
            deviceScale = desktopScale >= 200 ? 180u : (desktopScale >= 125 ? 140u : 100u);
        }

        // ---------------- 事件处理 ----------------

        private void OnRdpConnected(object sender, EventArgs e)
        {
            _everConnected = true;
            if (ConnectedAt == null) ConnectedAt = DateTime.Now;
            SetState(SurfaceState.Connected);
            ShowBanner(null);
            _watchdog.Stop();
            Log.Info("分身桌面已连接");
        }

        private void OnRdpLoginComplete(object sender, EventArgs e)
        {
            _loggedIn = true;
            Log.Info("分身桌面登录完成");
            // 动态分辨率只有完全登录后才可靠，且需要再等一会儿；这里统一延迟应用
            _loginSettle.Stop();
            _loginSettle.Start();
        }

        private void OnLoginSettled(object sender, EventArgs e)
        {
            _loginSettle.Stop();
            // View-only 已在 Connect 之前武装，这里只需按配置校正一次（可能中途被切换过）
            if (_viewOnly != null) _viewOnly.SetActive(_profile.ViewOnly);
            SyncResolutionToWindow();
        }

        private void OnRdpReconnecting(object sender, RdpReconnectingEventArgs e)
        {
            SetState(SurfaceState.Reconnecting);
            ShowBanner(string.Format(L.T("连接中断，正在自动重连…（第 {0}/{1} 次）"),
                e.AttemptCount, e.MaxAttemptCount));
            Log.Warn("自动重连中: " + e.AttemptCount + "/" + e.MaxAttemptCount);
        }

        private void OnRdpReconnected(object sender, EventArgs e)
        {
            SetState(SurfaceState.Connected);
            ShowBanner(null);   // 否则重连提示会永久盖在恢复后的画面上
            // 重连后远端可能已回到旧分辨率，清掉缓存让下一次同步真正下发
            _pendingWidth = 0; _pendingHeight = 0;
            if (_loggedIn)
            {
                _loginSettle.Stop();
                _loginSettle.Start();   // 复用登录后的延迟，等会话稳定再改显示设置
            }
            Log.Info("自动重连成功");
        }

        /// <summary>集中管理覆盖提示文字，避免某条路径忘了收起它。</summary>
        private void ShowBanner(string text)
        {
            if (_overlayText == null) return;
            if (string.IsNullOrEmpty(text)) { _overlayText.Visible = false; return; }
            _overlayText.Text = text;
            _overlayText.Visible = true;
            _overlayText.BringToFront();
        }

        private void OnRdpDisconnected(object sender, RdpDisconnectedEventArgs e)
        {
            int ext = 0;
            try { ext = Convert.ToInt32(_ocx.ExtendedDisconnectReason); }
            catch { }

            string reason = RdpDisconnectReason.Describe(e.DiscReason, ext);
            if (reason == null)
            {
                // 控件自带的本地化描述通常比我们的映射更准确
                try { reason = _ocx.GetErrorDescription(e.DiscReason, ext) as string; }
                catch { }
            }

            Log.Info(string.Format("分身桌面断开 discReason=0x{0:X} extended={1} everConnected={2} :: {3}",
                e.DiscReason, ext, _everConnected, reason));

            if (_closePending) { FinishClose(_everConnected, null); return; }
            FinishClose(_everConnected, reason);
        }

        /// <summary>兜底看门狗：事件订阅万一失败时，仍能靠轮询发现"从未连上"。</summary>
        private void OnWatchdogTick(object sender, EventArgs e)
        {
            _watchdogTicks++;
            int c = -1;
            try { if (_ocx != null) c = Convert.ToInt32(_ocx.Connected); }
            catch { }

            if (_closePending)
            {
                if (c == 0 || c == -1 || _watchdogTicks >= _closeDeadline)
                {
                    _watchdog.Stop();
                    FinishClose(_everConnected, null);
                }
                return;
            }

            if (c == 1) { _watchdog.Stop(); return; }

            // 30 秒仍未连上，且事件也没来，判定为失败
            if (c == 0 && !_everConnected && _watchdogTicks > 30)
            {
                _watchdog.Stop();
                FinishClose(false, RdpDisconnectReason.FirstRunHints);
            }
        }

        // ---------------- 动态分辨率 ----------------

        private void RestartResizeDebounce()
        {
            if (_profile.WindowMode == WindowMode.Fullscreen) return;
            _resizeDebounce.Stop();
            _resizeDebounce.Start();
            if (_viewOnly != null) _viewOnly.Reposition();
        }

        private void OnResizeSettled(object sender, EventArgs e)
        {
            _resizeDebounce.Stop();
            SyncResolutionToWindow();
            if (_viewOnly != null) _viewOnly.Reposition();
        }

        /// <summary>把远端分辨率贴齐当前窗口（或自定义值）。</summary>
        public void SyncResolutionToWindow()
        {
            if (!_loggedIn || _ocx == null) return;

            int w, h;
            if (_profile.ResolutionMode == ResolutionMode.Custom)
            {
                w = EvenWidth(Clamp(_profile.CustomWidth, 200, 8192));
                h = Clamp(_profile.CustomHeight, 200, 8192);
            }
            else
            {
                w = EvenWidth(Clamp(ClientSize.Width, 200, 8192));
                h = Clamp(ClientSize.Height, 200, 8192);
            }
            ApplyResolution(w, h);
        }

        /// <summary>
        /// 应用分辨率。主路径是 UpdateSessionDisplaySettings（不断线），
        /// 失败则降级到 Reconnect(w,h)（原会话快速重连）。
        /// </summary>
        public bool ApplyResolution(int width, int height)
        {
            if (_ocx == null) return false;
            width = EvenWidth(Clamp(width, 200, 8192));
            height = Clamp(height, 200, 8192);
            if (width == _pendingWidth && height == _pendingHeight) return true;

            uint desktopScale, deviceScale;
            ResolveScale(out desktopScale, out deviceScale);

            try
            {
                // 物理尺寸传 0 表示不指定（有效范围是 10–10000 毫米）
                _ocx.UpdateSessionDisplaySettings(
                    (uint)width, (uint)height, 0u, 0u, 0u, desktopScale, deviceScale);
                _pendingWidth = width; _pendingHeight = height;
                Log.Info(string.Format("分辨率已更新 {0}×{1} @{2}%", width, height, desktopScale));
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("UpdateSessionDisplaySettings 失败，降级为重连改分辨率: " + ex.Message);
            }

            try
            {
                int status = Convert.ToInt32(_ocx.Reconnect((uint)width, (uint)height));
                // 0 = controlReconnectStarted, 1 = controlReconnectBlocked
                if (status == 0)
                {
                    _pendingWidth = width; _pendingHeight = height;
                    Log.Info(string.Format("已通过重连切换到 {0}×{1}", width, height));
                    return true;
                }
                Log.Warn("Reconnect 被拒绝，status=" + status);
            }
            catch (Exception ex)
            {
                Log.Error("Reconnect 改分辨率失败", ex);
            }
            return false;
        }

        // ---------------- 对外操作 ----------------

        /// <summary>手动模式下把主桌面的剪贴板推给分身桌面。</summary>
        public bool PushClipboardToDesktop()
        {
            try
            {
                var clip = _host != null ? _host.Ocx as IMsRdpClipboard : null;
                if (clip == null) return false;
                clip.SendClipboardToServer();
                Log.Info("已把剪贴板推送到分身桌面");
                return true;
            }
            catch (Exception ex) { Log.Error("推送剪贴板失败", ex); return false; }
        }

        /// <summary>手动模式下把分身桌面的剪贴板取回主桌面。</summary>
        public bool PullClipboardFromDesktop()
        {
            try
            {
                var clip = _host != null ? _host.Ocx as IMsRdpClipboard : null;
                if (clip == null) return false;
                clip.SendClipboardToClient();
                Log.Info("已从分身桌面取回剪贴板");
                return true;
            }
            catch (Exception ex) { Log.Error("取回剪贴板失败", ex); return false; }
        }

        public void SetViewOnly(bool on)
        {
            _profile.ViewOnly = on;
            if (_viewOnly != null) _viewOnly.SetActive(on);
        }

        public void SetAlwaysOnTop(bool on)
        {
            _profile.AlwaysOnTop = on;
            TopMost = on || _profile.WindowMode == WindowMode.Pip;
        }

        public void MoveToMonitor(MonitorInfo monitor)
        {
            if (monitor == null) return;
            _monitor = monitor;
            ApplyWindowMode();
            if (_viewOnly != null) _viewOnly.Reposition();
            SyncResolutionToWindow();
        }

        // ---------------- 关闭流程 ----------------

        private void OnFormClosingInternal(object sender, FormClosingEventArgs e)
        {
            // 系统关机/任务管理器强关：绝不能取消，尽力断开后放行
            if (e.CloseReason == CloseReason.WindowsShutDown ||
                e.CloseReason == CloseReason.TaskManagerClosing)
            {
                try { if (_ocx != null) _ocx.Disconnect(); }
                catch { }
                return;
            }

            int c = 0;
            try { if (_ocx != null) c = Convert.ToInt32(_ocx.Connected); }
            catch { c = 0; }

            if (_ocx == null || c == 0)
            {
                // 直接放行关闭。此处不能调 FinishClose——它末尾会再调 Close()，
                // 在 FormClosing 里递归关闭是未定义行为。改由 OnFormClosed 统一收口。
                _watchdog.Stop();
                return;
            }
            if (_closePending) { e.Cancel = true; return; }

            // 还连着：先发 Disconnect 并取消本次关闭，等控件真正断开后再销毁，
            // 否则会在连接线程还活着时强拆 mstscax，可能挂死或崩溃。
            _closePending = true;
            _closeDeadline = _watchdogTicks + 4;
            SetState(SurfaceState.Closing);
            e.Cancel = true;
            _watchdog.Start();
            try { _ocx.Disconnect(); }
            catch
            {
                _closePending = false;
                _watchdog.Stop();
                e.Cancel = false;
            }
        }

        private bool _closedRaised;

        /// <summary>
        /// 收口关闭流程。可能从三个地方进来：OnDisconnected（COM 回调）、
        /// 看门狗、以及 FormClosed 兜底。用 _closedRaised 保证只生效一次。
        /// </summary>
        private void FinishClose(bool everConnected, string reason)
        {
            if (_closedRaised) return;
            _closedRaised = true;

            _watchdog.Stop();
            _resizeDebounce.Stop();
            _loginSettle.Stop();
            _closePending = false;
            _ocx = null;

            // 关键：Closed2 的处理方会弹模态对话框。若在 mstscax 的 OnDisconnected
            // 回调栈里同步弹窗，模态消息循环会让用户能再点一次「启动桌面」，
            // 于是旧 OCX 尚未销毁就又建了一个 —— 必须推迟到下一轮消息循环。
            var h = Closed2;
            if (h != null)
            {
                var args = new SurfaceClosedEventArgs(everConnected, reason);
                Control anchor = Owner;   // 自己即将销毁，借宿主窗口投递
                Action raise = delegate
                {
                    try { h(this, args); }
                    catch (Exception ex) { Log.Error("通知桌面关闭失败", ex); }
                };
                if (anchor != null && !anchor.IsDisposed && anchor.IsHandleCreated)
                {
                    try { anchor.BeginInvoke((MethodInvoker)delegate { raise(); }); }
                    catch { raise(); }
                }
                else
                {
                    // 没有宿主可借时，用 WinForms 的 Idle 回调推迟一轮
                    EventHandler idle = null;
                    idle = delegate
                    {
                        Application.Idle -= idle;
                        raise();
                    };
                    Application.Idle += idle;
                }
            }

            try { if (!IsDisposed) Close(); }
            catch { }
        }

        private void SetState(SurfaceState s)
        {
            if (_state == s) return;
            _state = s;
            var h = StateChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_viewOnly != null) { _viewOnly.Dispose(); _viewOnly = null; }
                if (_watchdog != null) _watchdog.Dispose();
                if (_resizeDebounce != null) _resizeDebounce.Dispose();
                if (_loginSettle != null) _loginSettle.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---------------- 工具 ----------------

        /// <summary>协议要求宽度必须为偶数。</summary>
        private static int EvenWidth(int w) { return (w % 2 == 0) ? w : w - 1; }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private static void Try(string name, Action a)
        {
            try { a(); }
            catch (Exception ex) { Log.Debug("可选设置 " + name + " 未生效: " + ex.Message); }
        }

        private dynamic GetAdvanced()
        {
            try { return _ocx.AdvancedSettings9; } catch { }
            try { return _ocx.AdvancedSettings8; } catch { }
            try { return _ocx.AdvancedSettings7; } catch { }
            try { return _ocx.AdvancedSettings2; } catch { }
            return null;
        }
    }
}
