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

        public bool UserInitiated { get; set; }

        public int DisconnectReason { get; set; }

        public int ExtendedReason { get; set; }
    }

    /// <summary>
    /// 承载分身桌面画面的窗口。按规划，RDP 画面永远放在独立的顶层 WinForms 窗口里，
    /// 这样彻底避开 WPF/WinForms 混排的 airspace 遮挡与 Per-Monitor DPI 缩放问题。
    /// </summary>
    internal class DesktopSurface : Form
    {
        private const int WatchdogIntervalMs = 1000;
        private const int ResizeDebounceMs = 500;
        private const int LoginSettleMs = 1500;

        private const int NeverConnectedFailSeconds = 30;
        private const int ConnectTimeoutSeconds = 120;
        private const int CloseGraceSeconds = 4;

        private const int MinDesktopSize = 200;
        private const int MaxDesktopSize = 8192;
        private const int ColorDepthBits = 32;
        private const int MaxAutoReconnectAttempts = 20;
        private const int ControlReconnectStarted = 0;

        private const int LoopbackPerformanceFlags = (int)(RdpPerformanceFlags.EnableDesktopComposition
                                                         | RdpPerformanceFlags.EnableFontSmoothing
                                                         | RdpPerformanceFlags.EnableEnhancedGraphics);

        private const int BaseDpi = 96;
        private const int MinScalePercent = 100;
        private const int MaxScalePercent = 500;

        private const int PipMinWidth = 480, PipMinHeight = 320;
        private const int PipScreenFraction = 3;
        private const int PipMarginRight = 40, PipMarginBottom = 60;
        private const int WindowedOffset = 60, WindowedInset = 240;
        private const int WindowedMinWidth = 800, WindowedMinHeight = 600;
        private const int MinRestoredWidth = 320, MinRestoredHeight = 240;

        private const int MaxFullscreenSnaps = 3;

        private const int WM_DPICHANGED = 0x02E0;

        private const int DiscReasonLocalNotError = 1;

        private static int SecondsToTicks(int seconds) { return seconds * 1000 / WatchdogIntervalMs; }

        private RdpHostControl _host;
        private RdpEventSink _sink;
        private dynamic _ocx;
        private ViewOnlyGuard _viewOnly;
        private Label _overlayText;

        private readonly Timer _watchdog;
        private readonly Timer _resizeDebounce;
        private readonly Timer _loginSettle;    // 登录完成后延迟应用显示设置

        private readonly System.Threading.SynchronizationContext _uiContext;

        private SurfaceState _state = SurfaceState.Idle;
        private bool _everConnected;
        private bool _loggedIn;

        private int _watchdogTicks;
        private int _connectingSinceTick;
        private bool _connReadFailLogged;

        private bool _closePending;
        private int _closeDeadline;
        private bool _closeRequested;
        private string _pendingCloseReason;
        private bool _programmaticClose;
        private bool _inFormClosing;
        private bool _closedRaised;

        private int _lastDiscReason;
        private int _lastExtendedReason;
        private bool _hasLogonError;
        private int _logonError;

        private bool _authWarningVisible;
        private bool _topMostWanted;

        private bool _inSizeMove;
        private int _fullscreenSnaps;
        private WindowMode _appliedMode;

        private DesktopProfile _profile;
        private MonitorInfo _monitor;

        private bool _viewOnlyRequested;
        private bool _alwaysOnTopRequested;

        private bool _resolutionPolicySet;
        private ResolutionMode _resolutionMode;
        private int _customWidth, _customHeight;

        private int _remoteWidth, _remoteHeight;
        private uint _appliedDesktopScale, _appliedDeviceScale;

        public SurfaceState State { get { return _state; } }
        public bool ViewOnly { get { return _viewOnly != null && _viewOnly.IsActive; } }

        /// <summary>远端当前实际生效的分辨率（0 表示尚未确定）。</summary>
        public int CurrentWidth { get { return _remoteWidth; } }
        public int CurrentHeight { get { return _remoteHeight; } }

        /// <summary>连接建立的时刻，用于显示运行时长。</summary>
        public DateTime? ConnectedAt { get; private set; }

        /// <summary>画面所在的显示器。</summary>
        public MonitorInfo Monitor { get { return _monitor; } }

        public event EventHandler StateChanged;
        public event EventHandler<SurfaceClosedEventArgs> Closed2;

        public event EventHandler BoundsSaved;

        public DesktopSurface(DesktopProfile profile, MonitorInfo monitor)
        {
            _profile = profile;
            _monitor = monitor;
            _viewOnlyRequested = profile.ViewOnly;
            _alwaysOnTopRequested = profile.AlwaysOnTop;
            _uiContext = new WindowsFormsSynchronizationContext();

            Text = L.T(AppInfo.Title);
            BackColor = Color.Black;
            StartPosition = FormStartPosition.Manual;
            // ShowInTaskbar 只能在句柄创建前设定：它的 setter 会 RecreateHandle()，
            // 那会摧毁窗口句柄，并把寄宿其中的 RDP 控件一起带走、连接直接掉线。
            ShowInTaskbar = true;
            KeyPreview = false;

            ApplyWindowMode(SavedGeometry());

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

            _watchdog = new Timer { Interval = WatchdogIntervalMs };
            _watchdog.Tick += OnWatchdogTick;

            _resizeDebounce = new Timer { Interval = ResizeDebounceMs };
            _resizeDebounce.Tick += OnResizeSettled;

            _loginSettle = new Timer { Interval = LoginSettleMs };
            _loginSettle.Tick += OnLoginSettled;

            Shown += OnShownFirst;
            FormClosing += OnFormClosingInternal;
            // 兜底：无论走哪条关闭路径，窗体真正关掉后一定要通知外界，
            // 否则 AppContext 会一直以为桌面还在，关机阻止原因也摘不掉。
            FormClosed += delegate { FinishClose(_everConnected, null); };
        }

        private bool WantTopMost
        {
            get { return _alwaysOnTopRequested || _profile.WindowMode == WindowMode.Pip; }
        }

        private void ApplyTopMost()
        {
            bool want = WantTopMost && !_authWarningVisible;
            _topMostWanted = want;
            if (TopMost != want) TopMost = want;
        }

        private Rectangle? SavedGeometry()
        {
            if (_profile.WindowWidth <= 0 || _profile.WindowHeight <= 0) return null;
            return new Rectangle(_profile.WindowX, _profile.WindowY, _profile.WindowWidth, _profile.WindowHeight);
        }

        private void ApplyWindowMode(Rectangle? relative)
        {
            Rectangle b = _monitor.Bounds;
            FormBorderStyle border;
            Rectangle target;
            switch (_profile.WindowMode)
            {
                case WindowMode.Fullscreen:
                    border = FormBorderStyle.None;
                    target = b;
                    break;

                case WindowMode.Pip:
                    border = FormBorderStyle.SizableToolWindow;
                    target = PlaceOnMonitor(relative, DefaultPipBounds(b));
                    break;

                default: // Windowed
                    border = FormBorderStyle.Sizable;
                    target = PlaceOnMonitor(relative, DefaultWindowedBounds(b));
                    break;
            }
            if (FormBorderStyle != border) FormBorderStyle = border;
            SetBoundsKeepingState(target);
            _appliedMode = _profile.WindowMode;
            ApplyTopMost();
        }

        private static Rectangle DefaultPipBounds(Rectangle b)
        {
            int w = Math.Max(PipMinWidth, b.Width / PipScreenFraction);
            int h = Math.Max(PipMinHeight, b.Height / PipScreenFraction);
            return new Rectangle(b.Right - w - PipMarginRight, b.Bottom - h - PipMarginBottom, w, h);
        }

        private static Rectangle DefaultWindowedBounds(Rectangle b)
        {
            return new Rectangle(b.X + WindowedOffset, b.Y + WindowedOffset,
                Math.Max(WindowedMinWidth, b.Width - WindowedInset),
                Math.Max(WindowedMinHeight, b.Height - WindowedInset));
        }

        private Rectangle PlaceOnMonitor(Rectangle? relative, Rectangle fallback)
        {
            if (!relative.HasValue) return fallback;
            Rectangle b = _monitor.Bounds;
            Rectangle work = _monitor.WorkingArea.IsEmpty ? b : _monitor.WorkingArea;
            Rectangle rel = relative.Value;

            int w = Math.Min(rel.Width, work.Width);
            int h = Math.Min(rel.Height, work.Height);
            if (w < MinRestoredWidth || h < MinRestoredHeight) return fallback;

            int x = Clamp(b.X + rel.X, work.Left, work.Right - w);
            int y = Clamp(b.Y + rel.Y, work.Top, work.Bottom - h);
            return new Rectangle(x, y, w, h);
        }

        private void SetBoundsKeepingState(Rectangle target)
        {
            FormWindowState ws = WindowState;
            bool remax = ws == FormWindowState.Maximized && _profile.WindowMode != WindowMode.Fullscreen;
            if (ws == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
            if (Bounds != target) Bounds = target;
            if (remax) WindowState = FormWindowState.Maximized;
        }

        private Rectangle? CurrentRelativeGeometry()
        {
            Rectangle r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (r.Width <= 0 || r.Height <= 0) return SavedGeometry();
            Rectangle mb = ScreenBoundsOf(r);
            return new Rectangle(r.X - mb.X, r.Y - mb.Y, r.Width, r.Height);
        }

        private Rectangle ScreenBoundsOf(Rectangle r)
        {
            try { return Screen.FromRectangle(r).Bounds; }
            catch (Exception ex)
            {
                Log.Debug("查询窗口所在显示器失败，按目标屏计算: " + ex.Message);
                return _monitor.Bounds;
            }
        }

        private void SaveWindowGeometry()
        {
            if (_profile.WindowMode == WindowMode.Fullscreen || WindowState != FormWindowState.Normal || _closedRaised)
                return;

            Rectangle r = Bounds;
            Rectangle mb = ScreenBoundsOf(r);
            int x = r.X - mb.X, y = r.Y - mb.Y;
            if (x == _profile.WindowX && y == _profile.WindowY &&
                r.Width == _profile.WindowWidth && r.Height == _profile.WindowHeight) return;

            _profile.WindowX = x;
            _profile.WindowY = y;
            _profile.WindowWidth = r.Width;
            _profile.WindowHeight = r.Height;
            Log.Debug(string.Format("已记下窗口几何 {0},{1} {2}×{3}（相对所在屏 {4}）", x, y, r.Width, r.Height, mb));

            var h = BoundsSaved;
            if (h == null) return;
            try { h(this, EventArgs.Empty); }
            catch (Exception ex) { Log.Error("通知窗口几何已保存失败", ex); }
        }

        protected override void WndProc(ref Message m)
        {
            // 置顶的可靠做法：在 WM_WINDOWPOSCHANGING 里把 hwndInsertAfter 钳回 HWND_TOPMOST。
            // 这是无竞态的，比起定时器反复 SetWindowPos 既省电又不会抢焦点。
            if (m.Msg == NativeMethods.WM_WINDOWPOSCHANGING && _topMostWanted)
            {
                var wp = (NativeMethods.WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(NativeMethods.WINDOWPOS));
                if ((wp.flags & NativeMethods.SWP_NOZORDER) == 0)
                {
                    wp.hwndInsertAfter = NativeMethods.HWND_TOPMOST;
                    Marshal.StructureToPtr(wp, m.LParam, false);
                }
            }
            base.WndProc(ref m);

            if (m.Msg == WM_DPICHANGED && !_closedRaised)
            {
                Log.Info("画面窗口 DPI 变为 " + WindowDpi() + "，稍后重新下发分辨率与缩放");
                OnGeometryChanged();
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            OnGeometryChanged();
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            OnGeometryChanged();
        }

        protected override void OnResizeBegin(EventArgs e)
        {
            base.OnResizeBegin(e);
            _inSizeMove = true;
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            _inSizeMove = false;
            SaveWindowGeometry();
            OnGeometryChanged();
        }

        private void OnGeometryChanged()
        {
            if (!IsHandleCreated || _closedRaised || _resizeDebounce == null) return;
            if (_viewOnly != null) _viewOnly.Reposition();
            if (ClosingStarted) return;
            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        }

        private void OnResizeSettled(object sender, EventArgs e)
        {
            _resizeDebounce.Stop();
            if (_inSizeMove || _closedRaised) return;
            if (WindowState == FormWindowState.Minimized) return;

            if (_profile.WindowMode == WindowMode.Fullscreen) SnapFullscreenBounds();
            if (_viewOnly != null) _viewOnly.Reposition();
            SyncResolutionToWindow();
        }

        private void SnapFullscreenBounds()
        {
            Rectangle want = _monitor.Bounds;
            if (WindowState == FormWindowState.Normal && Bounds == want)
            {
                _fullscreenSnaps = 0;
                return;
            }
            if (!MonitorStillThere()) return;

            if (_fullscreenSnaps >= MaxFullscreenSnaps)
            {
                if (_fullscreenSnaps == MaxFullscreenSnaps)
                {
                    _fullscreenSnaps++;
                    Log.Warn("全屏画面反复被移出目标屏，停止纠正: " + Bounds + " 目标 " + want);
                }
                return;
            }
            _fullscreenSnaps++;
            Log.Info("全屏画面离开了目标屏的边界，纠正回去: " + Bounds + " -> " + want);
            SetBoundsKeepingState(want);
        }

        private bool MonitorStillThere()
        {
            try
            {
                foreach (Screen s in Screen.AllScreens)
                {
                    if (string.Equals(s.DeviceName, _monitor.DeviceName, StringComparison.Ordinal))
                        return s.Bounds == _monitor.Bounds;
                }
            }
            catch (Exception ex) { Log.Debug("枚举显示器失败: " + ex.Message); }
            return false;
        }

        // ---------------- 连接 ----------------

        private void OnShownFirst(object sender, EventArgs e)
        {
            Shown -= OnShownFirst;
            try
            {
                _sink = new RdpEventSink();
                _sink.Connecting += UiHandler(delegate { SetState(SurfaceState.Connecting); });
                _sink.Connected += UiHandler(OnRdpConnected);
                _sink.LoginComplete += UiHandler(OnRdpLoginComplete);
                _sink.Disconnected += UiHandler<RdpDisconnectedEventArgs>(OnRdpDisconnected);
                _sink.RemoteSizeChanged += UiHandler<RdpSizeChangedEventArgs>(OnRdpRemoteSizeChanged);
                _sink.Reconnecting += UiHandler<RdpReconnectingEventArgs>(OnRdpReconnecting);
                _sink.Reconnected += UiHandler(OnRdpReconnected);
                _sink.LogonError += UiHandler<RdpLogonErrorEventArgs>(OnRdpLogonError);
                _sink.IdleTimeout += UiHandler(OnRdpIdleTimeout);
                _sink.AuthenticationWarningDisplayed += UiHandler(OnRdpAuthWarningDisplayed);
                _sink.AuthenticationWarningDismissed += UiHandler(OnRdpAuthWarningDismissed);
                _sink.ConfirmClose = delegate { return true; };

                _host = new RdpHostControl { Dock = DockStyle.Fill, Sink = _sink };
                Controls.Add(_host);
                _host.CreateControl();
                _ocx = _host.Ocx;

                _viewOnly = new ViewOnlyGuard(this, _host);
                // 必须在 Connect 之前就武装：否则连接与登录的整个过程里
                // 用户的物理键鼠都能打进子会话，而界面上「仅查看」是勾着的。
                if (_viewOnlyRequested) _viewOnly.SetActive(true);

                Configure();
                SetState(SurfaceState.Connecting);
                _watchdogTicks = 0;
                _connectingSinceTick = 0;
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
            Size size = TargetDesktopSize();

            _ocx.Server = "localhost";
            Try("DesktopWidth", delegate { _ocx.DesktopWidth = size.Width; });
            Try("DesktopHeight", delegate { _ocx.DesktopHeight = size.Height; });
            Try("ColorDepth", delegate { _ocx.ColorDepth = ColorDepthBits; });

            dynamic adv = GetAdvanced();
            if (adv != null)
            {
                TryImportant("EnableCredSspSupport", delegate { adv.EnableCredSspSupport = true; });
                Try("AuthenticationLevel", delegate { adv.AuthenticationLevel = 0; });
                // 容器自己管全屏，控件就不会再创建一个与我们抢置顶的全屏窗口
                Try("ContainerHandledFullScreen", delegate { adv.ContainerHandledFullScreen = 1; });
                Try("DisplayConnectionBar", delegate { adv.DisplayConnectionBar = false; });
                // 关闭：完全不重定向；共享/手动：都需开启重定向，
                // 区别在于「手动」会额外要求控件停止自动同步（见下方 ManualClipboardSyncEnabled）
                TryImportant("RedirectClipboard", delegate
                {
                    adv.RedirectClipboard = _profile.Clipboard != ClipboardMode.Off;
                });
                Try("SmartSizing", delegate { adv.SmartSizing = true; });
                Try("EnableAutoReconnect", delegate { adv.EnableAutoReconnect = true; });
                Try("MaxReconnectAttempts", delegate { adv.MaxReconnectAttempts = MaxAutoReconnectAttempts; });
                Try("PerformanceFlags", delegate { adv.PerformanceFlags = LoopbackPerformanceFlags; });
                if (_viewOnlyRequested)
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
                    TryImportant("UserName", delegate { _ocx.UserName = user; });
                    if (adv != null)
                        TryImportant("ClearTextPassword", delegate { adv.ClearTextPassword = pwd; });
                    Log.Info("已使用保存的凭据登录分身桌面");
                }
            }

            int hookMode = (int)(_profile.WindowMode == WindowMode.Fullscreen
                ? RdpKeyboardHookMode.Remote
                : RdpKeyboardHookMode.FullScreenOnly);
            TryImportant("KeyboardHookMode", delegate { _ocx.SecuredSettings2.KeyboardHookMode = hookMode; });

            int audio = (int)_profile.DesktopAudio;
            if (audio < (int)DesktopAudioMode.Local || audio > (int)DesktopAudioMode.Mute)
                audio = (int)DesktopAudioMode.Local;
            TryImportant("AudioRedirectionMode", delegate { _ocx.SecuredSettings2.AudioRedirectionMode = audio; });

            // IMsRdpExtendedSettings 派生自 IUnknown 而非 dual，dynamic 会抛
            // RuntimeBinderException，必须走 ComImport 强转。
            var ext = (IMsRdpExtendedSettings)_host.Ocx;

            object vTrue = true;
            ext.set_Property("ConnectToChildSession", ref vTrue);

            // 手动模式：关掉自动同步，改由用户显式推送，避免两边剪贴板互相覆盖
            if (_profile.Clipboard == ClipboardMode.Manual)
            {
                TryImportant("ManualClipboardSyncEnabled", delegate
                {
                    object v = true;
                    ext.set_Property("ManualClipboardSyncEnabled", ref v);
                });
            }

            Try("EnableHardwareMode", delegate { object v = true; ext.set_Property("EnableHardwareMode", ref v); });
            // 环回专用：允许帧缓冲内存共享
            Try("EnableFrameBufferRedirection", delegate { object v = true; ext.set_Property("EnableFrameBufferRedirection", ref v); });

            uint desktopScale, deviceScale;
            ResolveScale(out desktopScale, out deviceScale);
            SetScaleFactors(ext, desktopScale, deviceScale);
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
                uint dpi = WindowDpi();
                pct = (int)(dpi * 100 / BaseDpi);
            }
            desktopScale = (uint)Clamp(pct, MinScalePercent, MaxScalePercent);
            // Devolutions 的实测映射：125% → 140；150–175% → 140；200%+ → 180
            deviceScale = desktopScale >= 200 ? 180u : (desktopScale >= 125 ? 140u : 100u);
        }

        private uint WindowDpi()
        {
            uint dpi = 0;
            try { if (IsHandleCreated) dpi = NativeMethods.GetDpiForWindow(Handle); }
            catch (Exception ex) { Log.Debug("读取窗口 DPI 失败，按 100% 处理: " + ex.Message); }
            return dpi == 0 ? (uint)BaseDpi : dpi;
        }

        private static void SetScaleFactors(IMsRdpExtendedSettings ext, uint desktopScale, uint deviceScale)
        {
            if (ext == null) return;
            Try("ScaleFactors", delegate
            {
                object ds = desktopScale, dev = deviceScale;
                ext.set_Property("DesktopScaleFactor", ref ds);
                ext.set_Property("DeviceScaleFactor", ref dev);
            });
        }

        // ---------------- 事件处理 ----------------

        private EventHandler UiHandler(EventHandler handler)
        {
            return delegate(object s, EventArgs a) { RunOnUi(delegate { handler(s, a); }); };
        }

        private EventHandler<T> UiHandler<T>(EventHandler<T> handler) where T : EventArgs
        {
            return delegate(object s, T a) { RunOnUi(delegate { handler(s, a); }); };
        }

        private void RunOnUi(Action a)
        {
            if (IsDisposed || _closedRaised) return;
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (!IsDisposed && !_closedRaised) a();
                    });
                }
                catch (Exception ex) { Log.Warn("RDP 事件无法投递回 UI 线程: " + ex.Message); }
                return;
            }
            a();
        }

        private void OnRdpConnected(object sender, EventArgs e)
        {
            _everConnected = true;
            if (ConnectedAt == null) ConnectedAt = DateTime.Now;
            if (_authWarningVisible)
            {
                _authWarningVisible = false;
                ApplyTopMost();
            }
            SetState(SurfaceState.Connected);
            ShowBanner(null);
            if (!_closePending) _watchdog.Stop();
            Log.Info("分身桌面已连接");
        }

        private void OnRdpLoginComplete(object sender, EventArgs e)
        {
            _loggedIn = true;
            _hasLogonError = false;
            Log.Info("分身桌面登录完成");
            if (ClosingStarted) return;
            // 动态分辨率只有完全登录后才可靠，且需要再等一会儿；这里统一延迟应用
            _loginSettle.Stop();
            _loginSettle.Start();
        }

        private void OnLoginSettled(object sender, EventArgs e)
        {
            _loginSettle.Stop();
            if (_viewOnly != null) _viewOnly.SetActive(_viewOnlyRequested);
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
            _remoteWidth = 0; _remoteHeight = 0;
            _appliedDesktopScale = 0; _appliedDeviceScale = 0;
            if (_loggedIn && !ClosingStarted)
            {
                _loginSettle.Stop();
                _loginSettle.Start();   // 复用登录后的延迟，等会话稳定再改显示设置
            }
            Log.Info("自动重连成功");
        }

        private void OnRdpRemoteSizeChanged(object sender, RdpSizeChangedEventArgs e)
        {
            if (e.Width <= 0 || e.Height <= 0) return;
            if (e.Width == _remoteWidth && e.Height == _remoteHeight) return;
            Log.Info(string.Format("远端桌面尺寸变为 {0}×{1}（此前记录 {2}×{3}）",
                e.Width, e.Height, _remoteWidth, _remoteHeight));
            _remoteWidth = e.Width;
            _remoteHeight = e.Height;
            RaiseStateChanged();
        }

        private void OnRdpLogonError(object sender, RdpLogonErrorEventArgs e)
        {
            string desc = RdpDisconnectReason.DescribeLogonError(e.Error);
            if (desc == null)
            {
                Log.Info("分身桌面登录过程通知 lError=" + e.Error);
                return;
            }
            _hasLogonError = true;
            _logonError = e.Error;
            Log.Warn(string.Format("分身桌面登录失败 lError={0} (0x{0:X8}) :: {1}", e.Error, desc));
        }

        private void OnRdpIdleTimeout(object sender, EventArgs e)
        {
            Log.Info("分身桌面客户端空闲超时通知（MinutesToIdleTimeout 到期）");
        }

        private void OnRdpAuthWarningDisplayed(object sender, EventArgs e)
        {
            _authWarningVisible = true;
            bool wasTop = TopMost;
            ApplyTopMost();
            Log.Info("分身桌面弹出了认证警告（证书/身份确认）" + (wasTop ? "，暂时取消置顶以免挡住它" : ""));
        }

        private void OnRdpAuthWarningDismissed(object sender, EventArgs e)
        {
            if (!_authWarningVisible) return;
            _authWarningVisible = false;
            _connectingSinceTick = _watchdogTicks;
            ApplyTopMost();
            Log.Info("认证警告已关闭");
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
            catch (Exception ex) { Log.Debug("读取扩展断开原因失败: " + ex.Message); }
            _lastDiscReason = e.DiscReason;
            _lastExtendedReason = ext;

            string reason = RdpDisconnectReason.Describe(e.DiscReason, ext);
            if (reason == null)
            {
                // 控件自带的本地化描述通常比我们的映射更准确
                try { reason = _ocx.GetErrorDescription(e.DiscReason, ext) as string; }
                catch (Exception ex) { Log.Debug("读取控件的断开描述失败: " + ex.Message); }
            }

            Log.Info(string.Format("分身桌面断开 discReason=0x{0:X} extended={1} everConnected={2} closeRequested={3} :: {4}",
                e.DiscReason, ext, _everConnected, _closeRequested, reason));

            if (_closePending || _closeRequested) { FinishClose(_everConnected, _pendingCloseReason); return; }
            FinishClose(_everConnected, WithLogonError(reason));
        }

        private void OnWatchdogTick(object sender, EventArgs e)
        {
            _watchdogTicks++;
            RdpConnectionState c = ReadConnectionState();

            if (_closePending)
            {
                if (c == RdpConnectionState.Disconnected || c == RdpConnectionState.Unknown ||
                    _watchdogTicks >= _closeDeadline)
                {
                    _watchdog.Stop();
                    FinishClose(_everConnected, _pendingCloseReason);
                }
                return;
            }

            if (c == RdpConnectionState.Connected) { _watchdog.Stop(); return; }
            if (_everConnected) return;

            if (c == RdpConnectionState.Disconnected && _watchdogTicks > SecondsToTicks(NeverConnectedFailSeconds))
            {
                _watchdog.Stop();
                Log.Warn("看门狗：" + NeverConnectedFailSeconds + " 秒仍未连上，也没收到断开事件");
                FinishClose(false, WithLogonError(RdpDisconnectReason.FirstRunHints));
                return;
            }

            if (!_authWarningVisible &&
                _watchdogTicks - _connectingSinceTick > SecondsToTicks(ConnectTimeoutSeconds))
            {
                Log.Warn("连接超时：连接中超过 " + ConnectTimeoutSeconds + " 秒仍未连上（Connected=" + c + "），主动断开");
                BeginProgrammaticClose(WithLogonError(RdpDisconnectReason.ConnectTimeout));
            }
        }

        private RdpConnectionState ReadConnectionState()
        {
            if (_ocx == null) return RdpConnectionState.Unknown;
            try
            {
                int c = Convert.ToInt32(_ocx.Connected);
                switch (c)
                {
                    case (int)RdpConnectionState.Disconnected: return RdpConnectionState.Disconnected;
                    case (int)RdpConnectionState.Connected: return RdpConnectionState.Connected;
                    case (int)RdpConnectionState.Connecting: return RdpConnectionState.Connecting;
                }
            }
            catch (Exception ex)
            {
                if (!_connReadFailLogged)
                {
                    _connReadFailLogged = true;
                    Log.Debug("读取 RDP 连接状态失败: " + ex.Message);
                }
            }
            return RdpConnectionState.Unknown;
        }

        private string WithLogonError(string reason)
        {
            if (!_hasLogonError) return reason;
            string head = RdpDisconnectReason.FormatLogonFailure(_logonError);
            return string.IsNullOrEmpty(reason) ? head : head + "\r\n" + reason;
        }

        // ---------------- 动态分辨率 ----------------

        private static Size ClampDesktopSize(int width, int height)
        {
            int w = Clamp(width, MinDesktopSize, MaxDesktopSize);
            if (w % 2 != 0) w--;
            return new Size(w, Clamp(height, MinDesktopSize, MaxDesktopSize));
        }

        private bool UsesCustomResolution
        {
            get
            {
                ResolutionMode mode = _resolutionPolicySet ? _resolutionMode : _profile.ResolutionMode;
                return mode == ResolutionMode.Custom;
            }
        }

        private Size TargetDesktopSize()
        {
            if (UsesCustomResolution)
            {
                return _resolutionPolicySet
                    ? ClampDesktopSize(_customWidth, _customHeight)
                    : ClampDesktopSize(_profile.CustomWidth, _profile.CustomHeight);
            }
            Size c = ClientSize;
            return ClampDesktopSize(c.Width, c.Height);
        }

        public void SetResolutionPolicy(ResolutionMode mode, int width, int height)
        {
            _resolutionPolicySet = true;
            _resolutionMode = mode;
            _customWidth = width;
            _customHeight = height;
        }

        private bool ClosingStarted
        {
            get { return _state == SurfaceState.Closing || _closePending || _closedRaised; }
        }

        /// <summary>把远端分辨率贴齐当前窗口（或自定义值）。</summary>
        public void SyncResolutionToWindow()
        {
            if (!_loggedIn || _ocx == null || ClosingStarted) return;
            if (!UsesCustomResolution && WindowState == FormWindowState.Minimized) return;

            Size s = TargetDesktopSize();
            ApplyResolution(s.Width, s.Height);
        }

        /// <summary>
        /// 应用分辨率。主路径是 UpdateSessionDisplaySettings（不断线），
        /// 失败则降级到 Reconnect(w,h)（原会话快速重连）。
        /// </summary>
        public bool ApplyResolution(int width, int height)
        {
            if (_ocx == null || ClosingStarted) return false;
            Size size = ClampDesktopSize(width, height);
            width = size.Width;
            height = size.Height;

            uint desktopScale, deviceScale;
            ResolveScale(out desktopScale, out deviceScale);
            if (width == _remoteWidth && height == _remoteHeight &&
                desktopScale == _appliedDesktopScale && deviceScale == _appliedDeviceScale) return true;

            try
            {
                // 物理尺寸传 0 表示不指定（有效范围是 10–10000 毫米）
                _ocx.UpdateSessionDisplaySettings(
                    (uint)width, (uint)height, 0u, 0u, 0u, desktopScale, deviceScale);
                RememberApplied(width, height, desktopScale, deviceScale);
                Log.Info(string.Format("分辨率已更新 {0}×{1} @{2}%", width, height, desktopScale));
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("UpdateSessionDisplaySettings 失败，降级为重连改分辨率: " + ex.Message);
            }

            try
            {
                SetScaleFactors(_host != null ? _host.Ocx as IMsRdpExtendedSettings : null, desktopScale, deviceScale);
                int status = Convert.ToInt32(_ocx.Reconnect((uint)width, (uint)height));
                if (status == ControlReconnectStarted)
                {
                    RememberApplied(width, height, desktopScale, deviceScale);
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

        private void RememberApplied(int width, int height, uint desktopScale, uint deviceScale)
        {
            _remoteWidth = width;
            _remoteHeight = height;
            _appliedDesktopScale = desktopScale;
            _appliedDeviceScale = deviceScale;
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
            _viewOnlyRequested = on;
            if (_viewOnly != null) _viewOnly.SetActive(on);
        }

        public void SetAlwaysOnTop(bool on)
        {
            _alwaysOnTopRequested = on;
            ApplyTopMost();
        }

        public void MoveToMonitor(MonitorInfo monitor)
        {
            if (monitor == null) return;
            Rectangle? relative = null;
            if (_profile.WindowMode != WindowMode.Fullscreen)
            {
                relative = IsHandleCreated && _appliedMode == _profile.WindowMode
                    ? CurrentRelativeGeometry()
                    : SavedGeometry();
            }
            _monitor = monitor;
            _fullscreenSnaps = 0;
            ApplyWindowMode(relative);
            if (_viewOnly != null) _viewOnly.Reposition();
            SyncResolutionToWindow();
        }

        // ---------------- 关闭流程 ----------------

        private void OnFormClosingInternal(object sender, FormClosingEventArgs e)
        {
            if (!_closedRaised) _closeRequested = true;

            // 系统关机/任务管理器强关：绝不能取消，尽力断开后放行
            if (e.CloseReason == CloseReason.WindowsShutDown ||
                e.CloseReason == CloseReason.TaskManagerClosing)
            {
                _inFormClosing = true;
                try { if (_ocx != null) _ocx.Disconnect(); }
                catch (Exception ex) { Log.Debug("关机时断开分身桌面失败: " + ex.Message); }
                finally { _inFormClosing = false; }
                return;
            }

            RdpConnectionState c = ReadConnectionState();
            if (c != RdpConnectionState.Connected && c != RdpConnectionState.Connecting)
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
            _closeDeadline = _watchdogTicks + SecondsToTicks(CloseGraceSeconds);
            SetState(SurfaceState.Closing);
            StopDisplayTimers();
            e.Cancel = true;
            _watchdog.Start();
            _inFormClosing = true;
            try { _ocx.Disconnect(); }
            catch (Exception ex)
            {
                Log.Warn("断开分身桌面失败，直接关闭窗口: " + ex.Message);
                _closePending = false;
                _watchdog.Stop();
                e.Cancel = false;
            }
            finally { _inFormClosing = false; }

            if (_closedRaised) e.Cancel = false;
        }

        private void BeginProgrammaticClose(string reason)
        {
            if (_closePending || _closedRaised) return;
            _programmaticClose = true;
            _pendingCloseReason = reason;
            _closePending = true;
            _closeDeadline = _watchdogTicks + SecondsToTicks(CloseGraceSeconds);
            SetState(SurfaceState.Closing);
            StopDisplayTimers();
            _watchdog.Start();
            try
            {
                if (_ocx == null) { FinishClose(_everConnected, reason); return; }
                _ocx.Disconnect();
            }
            catch (Exception ex)
            {
                Log.Warn("主动断开分身桌面失败，直接关闭: " + ex.Message);
                FinishClose(_everConnected, reason);
            }
        }

        private void StopDisplayTimers()
        {
            _resizeDebounce.Stop();
            _loginSettle.Stop();
        }

        private void FinishClose(bool everConnected, string reason)
        {
            if (_closedRaised) return;
            _closedRaised = true;

            _watchdog.Stop();
            StopDisplayTimers();
            _closePending = false;
            _ocx = null;

            bool user = _closeRequested;
            var args = new SurfaceClosedEventArgs(everConnected, user ? null : reason);
            args.UserInitiated = user;
            int disc = _lastDiscReason;
            if (_programmaticClose && !user) disc = DiscReasonLocalNotError;
            args.DisconnectReason = disc;
            args.ExtendedReason = _lastExtendedReason;
            Log.Debug(string.Format("画面收口 userInitiated={0} everConnected={1} discReason=0x{2:X}（上报 0x{3:X}） extended={4} programmatic={5}",
                user, everConnected, _lastDiscReason, disc, _lastExtendedReason, _programmaticClose));
            RaiseClosedLater(args);

            if (_inFormClosing) return;
            try { if (!IsDisposed) Close(); }
            catch (Exception ex) { Log.Debug("关闭画面窗口失败: " + ex.Message); }
        }

        /// <summary>
        /// 关键：Closed2 的处理方会弹模态对话框。若在 mstscax 的 OnDisconnected
        /// 回调栈里同步弹窗，模态消息循环会让用户能再点一次「启动桌面」，
        /// 于是旧 OCX 尚未销毁就又建了一个 —— 必须推迟到下一轮消息循环。
        /// </summary>
        private void RaiseClosedLater(SurfaceClosedEventArgs args)
        {
            var h = Closed2;
            if (h == null) return;
            System.Threading.SendOrPostCallback raise = delegate
            {
                try { h(this, args); }
                catch (Exception ex) { Log.Error("通知桌面关闭失败", ex); }
            };

            try
            {
                _uiContext.Post(raise, null);
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("投递关闭通知失败，改用 Idle 回调: " + ex.Message);
            }

            EventHandler idle = null;
            idle = delegate
            {
                Application.Idle -= idle;
                raise(null);
            };
            Application.Idle += idle;
        }

        private void SetState(SurfaceState s)
        {
            if (_state == s) return;
            if (_state == SurfaceState.Closing) return;
            _state = s;
            RaiseStateChanged();
        }

        private void RaiseStateChanged()
        {
            var h = StateChanged;
            if (h == null) return;
            try { h(this, EventArgs.Empty); }
            catch (Exception ex) { Log.Error("通知画面状态变化失败", ex); }
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

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private static void Try(string name, Action a)
        {
            try { a(); }
            catch (Exception ex) { Log.Debug("可选设置 " + name + " 未生效: " + ex.Message); }
        }

        private static void TryImportant(string name, Action a)
        {
            try { a(); }
            catch (Exception ex) { Log.Warn("设置 " + name + " 未生效: " + ex.Message); }
        }

        private dynamic GetAdvanced()
        {
            try { return _ocx.AdvancedSettings9; }
            catch (Exception ex) { Log.Debug("AdvancedSettings9 不可用: " + ex.Message); }
            try { return _ocx.AdvancedSettings8; }
            catch (Exception ex) { Log.Debug("AdvancedSettings8 不可用: " + ex.Message); }
            try { return _ocx.AdvancedSettings7; }
            catch (Exception ex) { Log.Debug("AdvancedSettings7 不可用: " + ex.Message); }
            try { return _ocx.AdvancedSettings2; }
            catch (Exception ex) { Log.Warn("拿不到任何 AdvancedSettings，凭据/剪贴板等设置都无法下发: " + ex.Message); }
            return null;
        }
    }
}
