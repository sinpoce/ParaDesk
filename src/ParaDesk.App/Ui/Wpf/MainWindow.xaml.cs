using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ParaDesk.Core;
using ParaDesk.Elevated;

namespace ParaDesk.Shell
{
    /// <summary>
    /// 管理界面（WPF Fluent）。所有业务动作仍然只经过 AppContext，
    /// 这一层只负责呈现与转发，因此后续换皮不影响逻辑。
    /// </summary>
    internal partial class MainWindow
    {
        private readonly ParaDesk.Ui.AppContext _app;
        private bool _loading;
        private bool _closed;

        private static readonly int[] ScaleValues = { 0, 100, 125, 150, 175, 200, 250, 300 };

        private DispatcherTimer _ticker;

        private const int RefreshCoalesceMs = 80;
        private DispatcherTimer _refreshTimer;
        private bool _refreshWhileHidden;
        private bool _shownOnce;
        private bool _lastAttached;

        public MainWindow(ParaDesk.Ui.AppContext app)
        {
            _app = app;
            InitializeComponent();
            AboutTitle.Text = AppInfo.Title;
            AboutVersion.Text = L.T("版本 ") + AppInfo.Version;

            // 运行时长要跳秒，但整页刷新代价大（含注册表与显示器枚举），
            // 所以单开一个轻量 ticker 只更新指标数字。
            _ticker = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _ticker.Tick += OnTick;
            _ticker.Start();

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RefreshCoalesceMs) };
            _refreshTimer.Tick += OnRefreshTimer;

            _startupSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(StartupSaveDelayMs) };
            _startupSaveTimer.Tick += delegate { SaveStartupCommand(); };
            TbStartupCmd.LostFocus += OnStartupCmdLostFocus;
            TbStartupDir.LostFocus += OnStartupCmdLostFocus;
            TbStartupCmd.PlaceholderText = L.T("例如 wt -d C:\\repo 或 powershell -NoExit -Command claude");
            TbStartupDir.PlaceholderText = L.T("工作目录（留空为用户主目录）");

            InitHotkeySlots();
            IsVisibleChanged += OnVisibleChangedRefresh;

            RefreshAllNow();
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (!IsVisible) return;   // 收进托盘时不做无谓刷新
            try
            {
                // 录制计时：必须放在桌面指标之前，且不受指标面板可见性影响，
                // 否则没开分身桌面时录制时长就不会跳（上一版就是栽在这里）。
                if (_app.IsRecording)
                {
                    RecElapsed.Text = FormatUptime(_app.RecordingStartedAt);
                }
                else if (RecElapsed.Text.Length > 0 && PageRecord.Visibility == Visibility.Visible)
                {
                    // 录制刚结束，清掉残留的计时数字
                    RecElapsed.Text = "";
                }

                if (MetricsPanel.Visibility != Visibility.Visible) return;
                MetricUptime.Text = FormatUptime(_app.SurfaceConnectedAt);

                int w = _app.SurfaceWidth, h = _app.SurfaceHeight;
                if (w > 0 && h > 0) MetricResolution.Text = w + "×" + h;
            }
            catch (Exception ex) { Log.Debug("更新状态指标失败: " + ex.Message); }
        }

        // ---------------- 导航 ----------------

        private void OnNavChecked(object sender, RoutedEventArgs e)
        {
            var rb = sender as System.Windows.Controls.RadioButton;
            if (rb == null || PageDesktop == null) return;
            string tag = rb.Tag as string;

            PageDesktop.Visibility = tag == "Desktop" ? Visibility.Visible : Visibility.Collapsed;
            PageDisplay.Visibility = tag == "Display" ? Visibility.Visible : Visibility.Collapsed;
            PageInput.Visibility = tag == "Input" ? Visibility.Visible : Visibility.Collapsed;
            PageRecord.Visibility = tag == "Record" ? Visibility.Visible : Visibility.Collapsed;
            PageHotkeys.Visibility = tag == "Hotkeys" ? Visibility.Visible : Visibility.Collapsed;
            PageSettings.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
            PageDiag.Visibility = tag == "Diag" ? Visibility.Visible : Visibility.Collapsed;
            PageAbout.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;

            // 切页后回到顶部：上一页滚到底再切页，新页会停在中间，很困惑
            if (PageScroller != null) PageScroller.ScrollToTop();

            if (tag == "Record") RefreshRecording(true);
            if (tag == "Hotkeys") WarnReservedHotkeys();
            if (tag == "Diag" && !_diagLoaded) StartDiagnosticsCheck();
        }

        // ---------------- 刷新 ----------------

        public void RefreshAll()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke((Action)RefreshAll); return; }
            if (_closed || _refreshTimer == null) return;
            if (_refreshTimer.IsEnabled) return;
            _refreshTimer.Start();
        }

        private void OnRefreshTimer(object sender, EventArgs e)
        {
            _refreshTimer.Stop();
            if (_closed) return;
            if (!IsVisible) { _refreshWhileHidden = true; return; }
            RefreshAllNow();
        }

        private void OnVisibleChangedRefresh(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible || _closed) return;
            if (!_shownOnce)
            {
                _shownOnce = true;
                if (!_refreshWhileHidden) return;
            }
            _refreshWhileHidden = false;
            RefreshAllNow();
        }

        private void RefreshAllNow()
        {
            if (_refreshTimer != null) _refreshTimer.Stop();
            if (_closed) return;

            bool attachedChanged;
            _loading = true;
            try
            {
                // 用带缓存的版本：Check() 会创建子会话传输通道，
                // 不能由每次界面刷新触发
                var env = SystemStatus.CheckCached();
                var s = _app.Settings;
                var p = s.GetActiveProfile();
                bool attached = _app.DesktopAttached;
                bool exists = SystemStatus.HasChildSession();
                attachedChanged = attached != _lastAttached;
                _lastAttached = attached;

                UpdateStatusCard(env, p, attached, exists);

                bool setupBusy = _app.SetupRunning;
                string busyText = _app.UndoRunning ? L.T("正在撤销…") : L.T("正在配置…");
                BtnPrimary.Content = setupBusy
                    ? busyText
                    : attached ? L.T("收起桌面") : (exists ? L.T("重新接入") : L.T("启动桌面"));
                BtnPrimary.IsEnabled = !setupBusy && (attached || !env.Blocked);
                BtnClose.IsEnabled = attached || exists;
                BtnReconfigure.IsEnabled = !setupBusy;
                BtnUndoSetup.IsEnabled = !setupBusy;
                BtnUndoSetup.Content = _app.UndoRunning ? L.T("正在撤销…") : L.T("撤销配置…");

                CbProfile.Items.Clear();
                int selProfile = 0;
                for (int i = 0; i < s.Profiles.Count; i++)
                {
                    CbProfile.Items.Add(new ProfileItem(s.Profiles[i].Name));
                    if (string.Equals(s.Profiles[i].Name, p.Name, StringComparison.Ordinal)) selProfile = i;
                }
                CbProfile.SelectedIndex = selProfile;
                BtnProfileDel.IsEnabled = s.Profiles.Count > 1;

                var mons = MonitorService.Enumerate();
                string device = p.MonitorDevice;
                if (string.IsNullOrEmpty(device))
                {
                    var def = MonitorService.DefaultTarget();
                    if (def != null) device = def.DeviceName;
                }
                LayoutView.SetMonitors(mons);
                LayoutView.SelectedDevice = device;
                // 用画面真实所在的屏，不是配置值：MonitorDevice 为 null（自动）
                // 或目标屏被拔掉走了回退时，用配置值这个标记根本画不出来
                LayoutView.ActiveDevice = _app.ActiveMonitorDevice;

                PopulateMonitors(mons, p);
                UpdateMonitorHint(mons, p, attached);
                string scope = string.Format(
                    L.T("以下设置随方案「{0}」保存，换方案会换成另一套。"), p.Name);
                DisplayScopeHint.Text = scope;
                InsideScopeHint.Text = scope;

                CbWindowMode.SelectedIndex = (int)p.WindowMode;
                PopulateResolutions(device, p);
                UpdateResolutionHint(mons, p);
                PopulateFps(mons, device);
                CbScale.SelectedIndex = ScaleIndex(p.ScalePercent);
                SwViewOnly.IsChecked = p.ViewOnly;
                SwViewOnlyQuick.IsChecked = p.ViewOnly;
                ViewOnlyQuickHint.Text = DescribeViewOnlyHint(s);
                SwTopMost.IsChecked = p.AlwaysOnTop;
                CbClipboard.SelectedIndex = (int)p.Clipboard;
                CbClipboard.IsEnabled = !attached;   // 剪贴板模式需重新连接才生效
                ClipManualPanel.Visibility = (p.Clipboard == ClipboardMode.Manual && attached)
                    ? Visibility.Visible : Visibility.Collapsed;
                ClipHint.Text = attached
                    ? L.T("修改后需重新启动分身桌面才生效")
                    : L.T("系统默认与主桌面共享；手动模式可避免两边互相覆盖");

                RefreshInsideDesktop(p);

                SwStartup.IsChecked = StartupRegistration.IsEnabled();
                SwStartMinimized.IsChecked = s.StartMinimized;
                SwAutoStartDesktop.IsChecked = s.AutoStartDesktop;
                SwCheckUpdates.IsChecked = s.CheckUpdates;
                SwTray.IsChecked = s.MinimizeToTray;
                SwAutoReattach.IsChecked = s.AutoReattach;
                SwConfirmClose.IsChecked = s.ConfirmBeforeClose;
                SwLogoffOnShutdown.IsChecked = s.AutoLogoffOnShutdown;
                CbLanguage.SelectedIndex = s.Language == "zh" ? 1 : (s.Language == "en" ? 2 : 0);

                CbWindowMode.IsEnabled = !attached;
                CbScale.IsEnabled = !attached;

                foreach (var slot in _hotkeySlots) ShowSavedHotkey(s, slot);

                CredHint.Text = CredentialStore.Exists
                    ? L.T("已保存凭据（DPAPI 加密，仅本机本账户可解）")
                    : L.T("通常不需要——启动桌面时的那次配置已启用免密登录策略");

                // 沙盒功能没开时不再让用户自己去「启用或关闭 Windows 功能」翻，
                // 按钮直接变成"启用"，由程序提权装。家庭版是真装不了，只能如实说。
                string sbReason;
                bool sbOk = Providers.SandboxProvider.IsAvailable(out sbReason);
                _sandboxNeedsEnable = !sbOk && Providers.SandboxProvider.CanEnable();

                BtnSandbox.IsEnabled = (sbOk || _sandboxNeedsEnable) && !setupBusy;
                BtnSandbox.Content = setupBusy ? busyText
                    : _sandboxNeedsEnable ? L.T("启用沙盒功能")
                    : Providers.SandboxProvider.IsRunning() ? L.T("沙盒运行中")
                    : L.T("启动沙盒桌面");
                SandboxHint.Text = sbOk
                    ? L.T("系统同时只允许一个分身桌面；需要更多隔离桌面时可用 Windows 沙盒（即抛环境，不共享文件）。")
                    : _sandboxNeedsEnable
                        ? L.T("需要先启用 Windows 沙盒功能。点右侧即可，需要一次管理员授权并重启电脑。")
                        : sbReason;

                AboutEnv.Text = string.Format(
                    L.T("系统 {0} (build {1})　子会话 {2}　监听器 {3}"),
                    env.EditionId, env.BuildNumber,
                    env.ChildSessionsEnabled ? L.T("已启用") : L.T("未启用"),
                    env.RdpListenerEnabled ? L.T("已启用") : L.T("未启用"));
            }
            finally { _loading = false; }

            // 录制可能由热键或托盘发起，那两条路径不经过本窗口。
            // 不在这里同步，录制页会一直显示"未在录制"、按钮状态也是错的。
            if (PageRecord != null && PageRecord.Visibility == Visibility.Visible)
                RefreshRecording(attachedChanged);
            else
                UpdateRecordingStatus();

            // 刷新会把界面文案重新赋值成中文，所以翻译必须在最后再走一遍
            Localizer.Translate(this);

            UpdateNotice();
        }

        private sealed class StatusTone
        {
            public readonly SolidColorBrush Fore;
            public readonly SolidColorBrush Tint;

            public StatusTone(byte r, byte g, byte b)
            {
                Fore = new SolidColorBrush(Color.FromRgb(r, g, b));
                Fore.Freeze();
                Tint = new SolidColorBrush(Color.FromArgb(0x28, r, g, b));
                Tint.Freeze();
            }
        }

        private static readonly StatusTone ToneBlue = new StatusTone(0x00, 0x78, 0xD4);
        private static readonly StatusTone ToneAmber = new StatusTone(0xB8, 0x86, 0x0B);
        private static readonly StatusTone ToneGreen = new StatusTone(0x10, 0x89, 0x3E);
        private static readonly StatusTone ToneRed = new StatusTone(0xC4, 0x2B, 0x1C);
        private static readonly StatusTone ToneGray = new StatusTone(0x8A, 0x88, 0x86);

        /// <summary>
        /// 状态卡片。四种状态各有自己的配色、图标、徽章文案，
        /// 有会话时额外显示运行时长/分辨率/显示位置/会话 ID 四项指标。
        /// </summary>
        private void UpdateStatusCard(EnvironmentReport env, DesktopProfile p, bool attached, bool exists)
        {
            StatusTone tone;
            string title, detail, badge;
            var symbol = global::Wpf.Ui.Controls.SymbolRegular.Desktop24;
            bool busy = false;

            var surfaceState = _app.SurfaceState;

            if (attached && surfaceState == Rdp.SurfaceState.Connecting)
            {
                tone = ToneBlue; title = L.T("正在连接…"); badge = L.T("连接中"); busy = true;
                symbol = global::Wpf.Ui.Controls.SymbolRegular.PlugConnected24;
                detail = L.T("首次连接会为你的账户建立第二个登录，可能需要一两分钟。");
            }
            else if (attached && surfaceState == Rdp.SurfaceState.Reconnecting)
            {
                tone = ToneAmber; title = L.T("正在重连…"); badge = L.T("重连中"); busy = true;
                symbol = global::Wpf.Ui.Controls.SymbolRegular.ArrowSync24;
                detail = L.T("连接中断，正在自动恢复。分身桌面里的程序不受影响。");
            }
            else if (attached)
            {
                tone = ToneGreen; title = L.T("分身桌面运行中"); badge = L.T("运行中");
                symbol = global::Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24;
                detail = L.T("在里面打开终端即可开始工作；你的主屏键鼠不受影响。");
                if (p.ViewOnly)
                    detail = L.T("已开启仅查看：你的键鼠不会作用到分身桌面。") +
                             (L.Current == "en" ? " " : "") + detail;
            }
            else if (exists)
            {
                tone = ToneAmber; title = L.T("已收起（后台运行中）"); badge = L.T("后台");
                symbol = global::Wpf.Ui.Controls.SymbolRegular.EyeOff24;
                detail = L.T("分身桌面里的程序仍在运行。点「重新接入」可再次看到画面。");
            }
            else if (!env.ReadyToStart)
            {
                tone = ToneRed; title = L.T("尚未就绪"); badge = L.T("需配置");
                symbol = global::Wpf.Ui.Controls.SymbolRegular.Warning24;
                detail = env.NextAction ?? L.T("环境检查未通过。");
            }
            else
            {
                tone = ToneGray; title = L.T("未启动"); badge = L.T("就绪");
                symbol = global::Wpf.Ui.Controls.SymbolRegular.Desktop24;
                detail = L.T("一切就绪，点「启动桌面」即可在选定显示器上开出分身桌面。");
            }

            StatusTitle.Text = title;
            StatusDetail.Text = detail;
            StatusBadgeText.Text = badge;
            StatusIcon.Symbol = symbol;
            StatusProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

            StatusIcon.Foreground = tone.Fore;
            StatusBadgeText.Foreground = tone.Fore;
            StatusIconBg.Background = tone.Tint;
            StatusBadge.Background = tone.Tint;

            // 指标区
            bool showMetrics = attached || exists;
            MetricsPanel.Visibility = showMetrics ? Visibility.Visible : Visibility.Collapsed;
            if (!showMetrics) return;

            MetricUptime.Text = FormatUptime(_app.SurfaceConnectedAt);

            int w = _app.SurfaceWidth, h = _app.SurfaceHeight;
            if (w > 0 && h > 0) MetricResolution.Text = w + "×" + h;
            else if (p.ResolutionMode == ResolutionMode.Custom)
                MetricResolution.Text = p.CustomWidth + "×" + p.CustomHeight;
            else MetricResolution.Text = L.T("跟随");

            var mon = MonitorService.Resolve(p.MonitorDevice);
            MetricMonitor.Text = mon != null ? MonitorNaming.NameOf(mon) : "—";

            uint sid = SystemStatus.ChildSessionId();
            MetricSession.Text = (sid == ParaDesk.Native.NativeMethods.NoChildSession || sid == 0)
                ? "—" : sid.ToString();
        }

        private static string FormatUptime(DateTime? since)
        {
            if (since == null) return "—";
            var d = DateTime.Now - since.Value;
            if (d.TotalSeconds < 0) return "—";
            if (d.TotalHours >= 1)
                return ((int)d.TotalHours) + ":" + d.Minutes.ToString("00") + ":" + d.Seconds.ToString("00");
            return d.Minutes.ToString("00") + ":" + d.Seconds.ToString("00");
        }

        private void UpdateNotice()
        {
            var n = _app.LastNotice;
            if (n == null)
            {
                NoticePanel.Visibility = Visibility.Collapsed;
                return;
            }

            string title = string.IsNullOrEmpty(n.Title) ? L.T("来自分身桌面的提醒") : n.Title;
            var severity = n.Level == ParaDesk.Ui.NoticeInfo.LevelError
                ? global::Wpf.Ui.Controls.InfoBarSeverity.Error
                : n.Level == ParaDesk.Ui.NoticeInfo.LevelWarn
                    ? global::Wpf.Ui.Controls.InfoBarSeverity.Warning
                    : global::Wpf.Ui.Controls.InfoBarSeverity.Informational;

            SetBar(NoticeBar, severity, title + "  ·  " + n.Time.ToString("HH:mm"), n.Body);
            NoticePanel.Visibility = Visibility.Visible;
        }

        private void OnDismissNotice(object sender, RoutedEventArgs e)
        {
            NoticePanel.Visibility = Visibility.Collapsed;
            _app.DismissNotice();
        }

        // ---------------- 动作 ----------------

        private void OnPrimary(object sender, RoutedEventArgs e)
        {
            if (_app.DesktopAttached) { _app.DetachDesktop(); RefreshAll(); return; }

            // 没配置过就顺手配掉。用户想的是"开一个分身桌面"，
            // "首次配置"是实现细节，不该逼他先理解这个概念再点第二个按钮。
            var env = SystemStatus.CheckCached();
            if (env.NeedsSetup)
            {
                RunSetupThen(delegate { StartDesktopNow(); });
                return;
            }

            StartDesktopNow();
        }

        private void StartDesktopNow()
        {
            string err = _app.StartDesktop();
            if (err != null) Warn(err);
            RefreshAll();
        }

        /// <summary>
        /// 征得同意后跑一次提权配置，成功再执行 next。
        ///
        /// 先自己弹一个说清楚要改什么的确认框，再弹 UAC：
        /// 毫无预兆地跳出管理员授权，对用户来说和恶意软件没有区别。
        /// </summary>
        private void RunSetupThen(Action next)
        {
            if (RejectIfSetupBusy()) return;

            var r = MessageBox.Show(this,
                L.T("分身桌面需要先让 Windows 打开「子会话」功能，这要一次管理员授权（只需一次，之后不再需要）。") +
                "\r\n\r\n" + L.T("同时会关掉跨设备恢复，并挡掉它在分身桌面里弹的那个系统错误框。") +
                "\r\n\r\n" + L.T("接下来会弹出管理员授权窗口，请选择「是」。") +
                "\r\n\r\n" + L.T("现在配置吗？"),
                AppInfo.ProductName, MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (r != MessageBoxResult.OK) return;

            BtnPrimary.IsEnabled = false;
            BtnPrimary.Content = L.T("正在配置…");

            _app.RunSetup(delegate(SetupResult res)
            {
                if (res == SetupResult.Success || res == SetupResult.RebootRequired)
                    ApplyErrorDialogFix();

                if (!IsLoaded) throw new ObjectDisposedException("MainWindow");

                switch (res)
                {
                    case SetupResult.Success:
                        SystemStatus.Invalidate();
                        RefreshAll();
                        InvalidateDiagnostics();
                        if (next != null) next();
                        return;
                    case SetupResult.RebootRequired:
                        // 提权进程已经解释过要重启，这里不重复弹窗
                        break;
                    case SetupResult.Cancelled:
                        Info(L.T("已取消（未获得管理员授权）。"));
                        break;
                    default:
                        Warn(L.T("配置未完成，详见日志：") + "\r\n" + Log.Path0);
                        break;
                }
                SystemStatus.Invalidate();
                RefreshAll();
                InvalidateDiagnostics();
            });
        }

        private static void ApplyErrorDialogFix()
        {
            try
            {
                if (CrossDeviceSettings.ApplyAll(true))
                    Log.Info("配置系统时已启用错误框拦截");
                else
                    Log.Warn("配置系统时启用错误框拦截失败");
            }
            catch (Exception ex) { Log.Error("配置系统时启用错误框拦截失败", ex); }
        }

        private bool RejectIfSetupBusy()
        {
            if (!_app.SetupRunning) return false;
            Info(L.T("系统配置或撤销正在进行中，请等它完成后再试。"));
            return true;
        }

        /// <summary>诊断页的"重新配置"：只配置，不接着启动。</summary>
        private void OnReconfigure(object sender, RoutedEventArgs e)
        {
            RunSetupThen(null);
        }

        private void OnCloseDesktop(object sender, RoutedEventArgs e)
        {
            _app.CloseDesktop(true);
            RefreshAll();
        }

        private void OnIdentify(object sender, RoutedEventArgs e)
        {
            IdentifyOverlay.Show(3);
        }

        /// <summary>给当前选中的那块屏起名。「自动」项没有具体设备，改不了。</summary>
        private void OnRenameMonitor(object sender, RoutedEventArgs e)
        {
            var item = CbMonitor.SelectedItem as MonitorItem;
            string device = item != null ? item.Device : null;

            // 选的是「自动」时，改的是它当前实际落在的那块屏
            if (string.IsNullOrEmpty(device))
            {
                var fallback = MonitorService.Resolve(null);
                if (fallback == null) return;
                device = fallback.DeviceName;
            }

            var mon = MonitorService.Find(MonitorService.Enumerate(), device);
            if (mon == null) { Warn(L.T("这块显示器已断开，插回后才能改名。")); return; }

            var dlg = new RenameDialog(mon);
            if (dlg.ShowDialog() != true) return;

            if (MonitorNaming.Rename(device, dlg.ResultName))
            {
                SettingsStore.Save(_app.Settings);
                _app.RefreshTray();   // 托盘里的录制目标名也会变
                RefreshAll();
            }
        }

        // ---------------- 诊断页 ----------------

        private bool _diagLoaded;
        private bool _diagChecking;

        private void StartDiagnosticsCheck()
        {
            if (_diagChecking || _closed) return;
            _diagChecking = true;

            BtnDiagRecheck.IsEnabled = false;
            SetBar(DiagBar, global::Wpf.Ui.Controls.InfoBarSeverity.Informational,
                L.T("正在检查环境…"),
                L.T("通常一两秒即可完成。"));

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                EnvironmentReport env = null;
                bool captureOk = false;
                string error = null;
                try
                {
                    env = SystemStatus.Check();
                    captureOk = Recording.CaptureItemFactory.IsSupported;
                }
                catch (Exception ex)
                {
                    Log.Error("环境检查失败", ex);
                    error = ex.Message;
                }

                try
                {
                    Dispatcher.BeginInvoke((Action)delegate { OnDiagnosticsChecked(env, captureOk, error); });
                }
                catch (Exception ex) { Log.Error("回填环境检查结果失败", ex); }
            });
        }

        private void OnDiagnosticsChecked(EnvironmentReport env, bool captureOk, string error)
        {
            _diagChecking = false;
            if (_closed) return;
            BtnDiagRecheck.IsEnabled = true;

            if (env == null)
            {
                SetBar(DiagBar, global::Wpf.Ui.Controls.InfoBarSeverity.Error,
                    L.T("环境检查失败"),
                    error ?? L.T("详见日志。"));
                return;
            }

            _diagLoaded = true;
            DiagCheckList.Children.Clear();

            // Fix 列：只有"提权跑一次配置就能解决"的项才给按钮。
            // 给一个点了没用的按钮，比不给按钮更让人恼火。
            AddDiagRow(L.T("Windows 版本"), !env.IsHomeEdition,
                env.EditionId + " build " + env.BuildNumber, L.T("家庭版不支持子会话功能"), false);
            AddDiagRow(L.T("远程桌面客户端控件"), env.RdpControlRegistered,
                L.T("已就绪"), L.T("系统组件缺失，无法修复"), false);
            AddDiagRow(L.T("子会话功能"), env.ChildSessionsEnabled,
                L.T("已启用"), L.T("未启用"), true);
            AddDiagRow(L.T("远程桌面监听器"), env.RdpListenerEnabled,
                L.T("已启用"), L.T("未启用"), true);
            AddDiagRow(L.T("远程桌面服务"), env.TermServiceRunning,
                L.T("运行中"), L.T("未运行"), true);
            // 通道未就绪多半是"配置写好了但还没重启"，修不了，只能等重启
            AddDiagRow(L.T("子会话通道"), env.TransportReady, L.T("可用"),
                SystemStatus.DescribeTransportError(env.TransportStatus), false);
            AddDiagRow(L.T("屏幕捕获（录制）"), captureOk,
                L.T("支持"), L.T("需要 Windows 10 1903 或更高版本"), false);

            if (env.ReadyToStart)
            {
                SetBar(DiagBar, global::Wpf.Ui.Controls.InfoBarSeverity.Success,
                    L.T("环境正常"),
                    L.T("所有前置条件均已满足。"));
            }
            else
            {
                SetBar(DiagBar, global::Wpf.Ui.Controls.InfoBarSeverity.Warning,
                    L.T("有未通过项"),
                    env.NextAction ?? L.T("详见上方列表。"));
            }
        }

        private void InvalidateDiagnostics()
        {
            _diagLoaded = false;
            if (PageDiag != null && PageDiag.Visibility == Visibility.Visible) StartDiagnosticsCheck();
        }

        private void OnDiagRecheck(object sender, RoutedEventArgs e)
        {
            StartDiagnosticsCheck();
        }

        private void AddDiagRow(string name, bool ok, string okText, string failText, bool fixable)
        {
            // 每行是独立的 Grid，列宽只能写死才能对齐；宽度按最长的英文标签取，
            // 再加 Wrap 兜底——英文比中文长得多，190 会把 "Remote Desktop client control" 切掉
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new global::Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = ok ? global::Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24
                            : global::Wpf.Ui.Controls.SymbolRegular.ErrorCircle24,
                FontSize = 17,
                Foreground = ok ? ToneGreen.Fore : ToneRed.Fore,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);

            var label = new TextBlock
            {
                Text = name,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            var detail = new TextBlock
            {
                Text = ok ? okText : failText,
                Opacity = 0.72,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(detail, 2);
            row.Children.Add(detail);

            if (!ok && fixable)
            {
                var fix = new global::Wpf.Ui.Controls.Button
                {
                    Content = L.T("修复"),
                    Padding = new Thickness(10, 2, 10, 2),
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                fix.Click += OnFixDiag;
                Grid.SetColumn(fix, 3);
                row.Children.Add(fix);
            }

            DiagCheckList.Children.Add(row);
        }

        /// <summary>
        /// 逐项修复。三个可修项（子会话功能／监听器／服务）都由同一个提权配置一次性搞定，
        /// 所以点哪一行都是跑同一件事——不必为每项单独写一条提权路径，
        /// </summary>
        private void OnFixDiag(object sender, RoutedEventArgs e)
        {
            RunSetupThen(null);
        }

        private bool _bundleBusy;

        private void OnExportDiagBundle(object sender, RoutedEventArgs e)
        {
            if (_bundleBusy) return;
            _bundleBusy = true;
            BtnDiagBundle.IsEnabled = false;
            BtnDiagBundle.Content = L.T("正在生成…");
            ShowBar(DiagActionBar, global::Wpf.Ui.Controls.InfoBarSeverity.Informational,
                L.T("正在生成诊断包…"), L.T("正在收集日志与环境信息。"));

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string err = null, path = null;
                try { path = ParaDesk.Diagnostics.DiagBundle.Create(null, out err); }
                catch (Exception ex)
                {
                    Log.Error("生成诊断包失败", ex);
                    err = ex.Message;
                }

                try
                {
                    Dispatcher.BeginInvoke((Action)delegate { OnDiagBundleDone(path, err); });
                }
                catch (Exception ex) { Log.Error("回填诊断包结果失败", ex); }
            });
        }

        private void OnDiagBundleDone(string path, string err)
        {
            _bundleBusy = false;
            if (_closed) return;
            BtnDiagBundle.IsEnabled = true;
            BtnDiagBundle.Content = L.T("导出诊断包");

            if (string.IsNullOrEmpty(path))
            {
                ShowBar(DiagActionBar, global::Wpf.Ui.Controls.InfoBarSeverity.Error,
                    L.T("生成诊断包失败"), err ?? L.T("详见日志。"));
                return;
            }

            ShowBar(DiagActionBar, global::Wpf.Ui.Controls.InfoBarSeverity.Success,
                L.T("已生成诊断包"), path);
            RevealInExplorer(path);
        }

        private void OnUndoSetup(object sender, RoutedEventArgs e)
        {
            if (RejectIfSetupBusy()) return;

            string msg =
                L.T("将撤销 ParaDesk 对系统所做的配置：") + "\r\n\r\n" +
                "• " + L.T("关闭 Windows 的子会话功能（第一次配置前就已开启的保持不变）") + "\r\n" +
                "• " + L.T("按第一次配置前记录的原值，恢复远程桌面监听器、防火墙规则、免密登录的凭据委派、「始终提示输入密码」和 TermService 启动类型") + "\r\n" +
                "• " + L.T("移除分身桌面里的守护程序启动项") + "\r\n\r\n" +
                L.T("如果没有第一次配置前的记录（例如由旧版本配置过），远程桌面监听器、防火墙规则、「始终提示输入密码」和 TermService 启动类型的原值无从得知，将保持不变，只删除 ParaDesk 写入的凭据委派条目；不需要远程桌面的话，可以在 Windows「设置 → 系统 → 远程桌面」里关闭。") + "\r\n\r\n" +
                L.T("需要一次管理员授权。之后若还要使用分身桌面，需要重新配置并重启电脑。");
            if (_app.DesktopAttached || SystemStatus.HasChildSession())
                msg += "\r\n\r\n" + L.T("分身桌面正在运行，建议先关闭它，里面的程序会随配置撤销而无法继续使用。");
            msg += "\r\n\r\n" + L.T("确定撤销吗？");

            if (MessageBox.Show(this, msg, AppInfo.ProductName, MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning) != MessageBoxResult.OK) return;

            BtnUndoSetup.IsEnabled = false;
            BtnUndoSetup.Content = L.T("正在撤销…");

            _app.RunUndoSetup(delegate(SetupResult res)
            {
                if (!IsLoaded) throw new ObjectDisposedException("MainWindow");

                BtnUndoSetup.IsEnabled = true;
                BtnUndoSetup.Content = L.T("撤销配置…");
                switch (res)
                {
                    case SetupResult.Success:
                        ShowBar(DiagActionBar, global::Wpf.Ui.Controls.InfoBarSeverity.Success,
                            L.T("撤销配置完成"), L.T("已撤销 ParaDesk 对系统所做的配置。"));
                        break;
                    case SetupResult.RebootRequired:
                        ShowBar(DiagActionBar, global::Wpf.Ui.Controls.InfoBarSeverity.Warning,
                            L.T("撤销配置完成"), L.T("已撤销配置，重启电脑后完全生效。"));
                        break;
                    case SetupResult.Cancelled:
                        Info(L.T("已取消（未获得管理员授权）。"));
                        break;
                    default:
                        Warn(L.T("撤销配置未完成，详见日志：") + "\r\n" + Log.Path0);
                        break;
                }
                SystemStatus.Invalidate();
                RefreshAll();
                InvalidateDiagnostics();
            });
        }

        // ---------------- 沙盒桌面 ----------------

        /// <summary>沙盒功能尚未启用，按钮此刻的含义是"启用"而不是"启动"。</summary>
        private bool _sandboxNeedsEnable;

        /// <summary>
        /// 提权装 Windows 沙盒可选功能。装组件比改注册表慢，
        /// 期间按钮置灰并显示进行中，否则用户会以为点了没反应而反复点。
        /// </summary>
        private void EnableSandboxFeature()
        {
            var r = MessageBox.Show(this,
                L.T("将启用 Windows 沙盒功能。这是 Windows 的可选组件，需要一次管理员授权，装完要重启电脑才能使用。") +
                "\r\n\r\n" + L.T("现在启用吗？"),
                AppInfo.ProductName, MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (r != MessageBoxResult.OK) return;

            BtnSandbox.IsEnabled = false;
            BtnSandbox.Content = L.T("正在启用…");

            _app.RunEnableSandbox(delegate(SetupResult res)
            {
                if (!IsLoaded) throw new ObjectDisposedException("MainWindow");

                switch (res)
                {
                    case SetupResult.Success:
                    case SetupResult.RebootRequired:
                        Info(L.T("沙盒功能已启用，重启电脑后即可使用。"));
                        break;
                    case SetupResult.Cancelled:
                        Info(L.T("已取消（未获得管理员授权）。"));
                        break;
                    default:
                        Warn(L.T("启用沙盒功能失败，详见日志。"));
                        break;
                }
                RefreshAll();
            });
        }

        private void OnLaunchSandbox(object sender, RoutedEventArgs e)
        {
            // 功能没装时这个按钮的职责是"装"，不是"启动"
            if (_sandboxNeedsEnable) { EnableSandboxFeature(); return; }

            string reason;
            if (!Providers.SandboxProvider.IsAvailable(out reason))
            {
                Warn(reason);
                return;
            }

            if (Providers.SandboxProvider.IsRunning())
            {
                Info(L.T("沙盒桌面已在运行。系统同时只允许一个沙盒实例。"));
                return;
            }

            // 默认把「文档」映射进去，作为与主机交换文件的通道
            string shared = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                AppInfo.ProductName + "Shared");
            try { System.IO.Directory.CreateDirectory(shared); }
            catch (Exception ex)
            {
                Log.Warn("创建沙盒共享文件夹失败: " + ex.Message);
                shared = null;
            }

            var confirm = MessageBox.Show(this,
                L.T("沙盒桌面是即抛环境：关闭后里面的一切都会丢失，且与主桌面不共享文件。") + "\r\n\r\n" +
                (shared != null ? L.T("已为你映射共享文件夹（可读写）：") + "\r\n" + shared + "\r\n\r\n" : "") +
                L.T("若你要的是「AI 接着我的工作继续干」，请用分身桌面而不是沙盒。") + "\r\n\r\n" +
                L.T("确定启动吗？"),
                AppInfo.ProductName, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            string cfg;
            string err = Providers.SandboxProvider.Launch(shared, true, 4096, out cfg);
            if (err != null) Warn(err);
            else Info(L.T("沙盒桌面正在启动，首次启动需要一点时间。"));
            RefreshAll();
        }

        // ---------------- 剪贴板 / 浏览器 ----------------

        private void OnClipboardMode(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            var p = _app.Settings.GetActiveProfile();
            if (CbClipboard.SelectedIndex >= 0) p.Clipboard = (ClipboardMode)CbClipboard.SelectedIndex;
            SettingsStore.Save(_app.Settings);
            RefreshAll();
        }

        private void OnClipPush(object sender, RoutedEventArgs e)
        {
            if (!_app.PushClipboard()) Warn(L.T("推送剪贴板失败，详见日志。"));
        }

        private void OnClipPull(object sender, RoutedEventArgs e)
        {
            if (!_app.PullClipboard()) Warn(L.T("取回剪贴板失败，详见日志。"));
        }

        private void OnCreateBrowserShortcut(object sender, RoutedEventArgs e)
        {
            var browsers = BrowserHelper.Detect();
            if (browsers.Count == 0)
            {
                Warn(L.T("未检测到 Edge 或 Chrome。"));
                return;
            }

            // 成功与失败必须分开统计——之前把失败信息也拼进同一段文字，
            // 结果全部失败时仍然显示"已创建以下快捷方式"，等于骗用户。
            var ok = new StringBuilder();
            var bad = new StringBuilder();
            foreach (var b in browsers)
            {
                string err;
                string link = BrowserHelper.CreateIsolatedShortcut(b, out err);
                if (link != null) ok.AppendLine("• " + System.IO.Path.GetFileName(link));
                else bad.AppendLine("• " + b.Name + "：" + err);
            }

            if (ok.Length == 0)
            {
                Warn(L.T("未能创建任何快捷方式：") + "\r\n\r\n" + bad);
                return;
            }

            string msg = L.T("已在桌面创建以下快捷方式：") + "\r\n\r\n" + ok + "\r\n" +
                         L.T("用它启动的浏览器使用独立配置，可与主桌面的浏览器同时运行。") + "\r\n" +
                         L.T("把它拖进分身桌面里使用即可（两边共用同一个桌面文件夹）。");
            if (bad.Length > 0) msg += "\r\n\r\n" + L.T("以下未能创建：") + "\r\n" + bad;
            Info(msg);
        }

        private const int StartupSaveDelayMs = 600;
        private DispatcherTimer _startupSaveTimer;
        private DesktopProfile _startupEditProfile;

        private DesktopProfile _startupShownProfile;

        private void RefreshInsideDesktop(DesktopProfile p)
        {
            bool stale = !ReferenceEquals(_startupShownProfile, p);

            if (stale && _startupEditProfile != null) SaveStartupCommand();

            bool focused = TbStartupCmd.IsKeyboardFocusWithin || TbStartupDir.IsKeyboardFocusWithin;
            if (_startupEditProfile == null && (stale || !focused))
            {
                SetTextIfDifferent(TbStartupCmd, p.StartupCommand);
                SetTextIfDifferent(TbStartupDir, p.StartupWorkingDir);
                _startupShownProfile = p;
                if (focused)
                {
                    TbStartupCmd.CaretIndex = TbStartupCmd.Text.Length;
                    TbStartupDir.CaretIndex = TbStartupDir.Text.Length;
                }
            }

            SwKeepAwake.IsChecked = p.KeepAwake;
            PopulateDesktopAudio(p);
        }

        private DesktopProfile StartupEditTarget()
        {
            var s = _app.Settings;
            var shown = _startupShownProfile;
            if (shown != null && s.Profiles != null && s.Profiles.Contains(shown)) return shown;
            return s.GetActiveProfile();
        }

        private static void SetTextIfDifferent(System.Windows.Controls.TextBox box, string value)
        {
            string v = value ?? "";
            if (!string.Equals(box.Text, v, StringComparison.Ordinal)) box.Text = v;
        }

        private void OnStartupCmdChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading || _startupSaveTimer == null) return;
            if (_startupEditProfile == null) _startupEditProfile = StartupEditTarget();
            _startupSaveTimer.Stop();
            _startupSaveTimer.Start();
        }

        private void OnStartupCmdLostFocus(object sender, RoutedEventArgs e)
        {
            if (_startupEditProfile != null) SaveStartupCommand();
        }

        private void SaveStartupCommand()
        {
            if (_startupSaveTimer != null) _startupSaveTimer.Stop();
            var p = _startupEditProfile;
            _startupEditProfile = null;
            if (p == null) return;

            var s = _app.Settings;
            if (s.Profiles == null || !s.Profiles.Contains(p)) return;

            string cmd = CleanText(TbStartupCmd.Text);
            string dir = CleanText(TbStartupDir.Text);
            if (string.Equals(p.StartupCommand, cmd, StringComparison.Ordinal) &&
                string.Equals(p.StartupWorkingDir, dir, StringComparison.Ordinal)) return;

            p.StartupCommand = cmd;
            p.StartupWorkingDir = dir;
            SettingsStore.Save(s);
            Log.Info("方案「" + p.Name + "」的登录后自动运行已改为: " + (cmd == null ? "（无）" : cmd.Length + " 个字符") +
                     (dir != null ? "（工作目录 " + dir + "）" : ""));

            if (cmd != null && !CrossDeviceSettings.EnsureAgentRegistered())
            {
                ShowInsideBar(L.T("未能注册分身桌面里的守护程序"),
                    L.T("登录后自动运行和保持唤醒都靠它执行，没注册时不会生效。详见日志。"));
                return;
            }

            if (dir != null)
            {
                string full = NormalizeStartupDir(dir);
                if (full == null || (!IsNetworkPath(full) && !LocalDirExists(full)))
                {
                    ShowInsideBar(L.T("工作目录不存在"), dir);
                    return;
                }
            }

            HideBar(InsideBar);
        }

        private static string NormalizeStartupDir(string dir)
        {
            if (dir == null) return null;
            try
            {
                string d = dir.Trim().Trim('"');
                if (d.Length == 0) return null;
                d = Environment.ExpandEnvironmentVariables(d);
                if (!System.IO.Path.IsPathRooted(d))
                {
                    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (string.IsNullOrEmpty(home)) return null;
                    d = System.IO.Path.Combine(home, d);
                }
                return d;
            }
            catch { return null; }
        }

        private static bool IsNetworkPath(string full)
        {
            try
            {
                if (full.Length >= 2 && (full[0] == '\\' || full[0] == '/') && (full[1] == '\\' || full[1] == '/'))
                    return true;
                if (full.Length >= 2 && full[1] == ':' && char.IsLetter(full[0]))
                    return new System.IO.DriveInfo(full.Substring(0, 1)).DriveType == System.IO.DriveType.Network;
                return false;
            }
            catch { return true; }
        }

        private static bool LocalDirExists(string full)
        {
            try { return System.IO.Directory.Exists(full); }
            catch { return false; }
        }

        private static string CleanText(string s)
        {
            if (s == null) return null;
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }

        private void OnPickStartupDir(object sender, RoutedEventArgs e)
        {
            string start = NormalizeStartupDir(CleanText(TbStartupDir.Text));
            if (start == null || IsNetworkPath(start) || !LocalDirExists(start))
                start = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            string picked = ParaDesk.Ui.FolderPicker.Pick(hwnd, L.T("选择登录后自动运行的工作目录"), start);
            if (string.IsNullOrEmpty(picked)) return;

            if (_startupEditProfile == null) _startupEditProfile = StartupEditTarget();
            TbStartupDir.Text = picked;
            SaveStartupCommand();
        }

        private void OnKeepAwake(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var p = _app.Settings.GetActiveProfile();
            p.KeepAwake = SwKeepAwake.IsChecked == true;
            SettingsStore.Save(_app.Settings);

            if (p.KeepAwake && !CrossDeviceSettings.EnsureAgentRegistered())
                ShowInsideBar(L.T("未能注册分身桌面里的守护程序"),
                    L.T("登录后自动运行和保持唤醒都靠它执行，没注册时不会生效。详见日志。"));
            else
                HideBar(InsideBar);
        }

        private void PopulateDesktopAudio(DesktopProfile p)
        {
            CbDesktopAudio.Items.Clear();
            CbDesktopAudio.Items.Add(new ChoiceItem((int)DesktopAudioMode.Local, L.T("在本机播放")));
            CbDesktopAudio.Items.Add(new ChoiceItem((int)DesktopAudioMode.Mute, L.T("静音")));
            if (p.DesktopAudio == DesktopAudioMode.Remote)
                CbDesktopAudio.Items.Add(new ChoiceItem((int)DesktopAudioMode.Remote, L.T("留在分身桌面")));
            SelectChoice(CbDesktopAudio, (int)p.DesktopAudio);
        }

        private void OnDesktopAudioChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            var item = CbDesktopAudio.SelectedItem as ChoiceItem;
            if (item == null) return;
            var p = _app.Settings.GetActiveProfile();
            if ((int)p.DesktopAudio == item.Value) return;
            p.DesktopAudio = (DesktopAudioMode)item.Value;
            SettingsStore.Save(_app.Settings);
            Log.Info("方案「" + p.Name + "」的分身桌面声音改为 " + p.DesktopAudio);
        }

        private void ShowInsideBar(string title, string message)
        {
            ShowBar(InsideBar, global::Wpf.Ui.Controls.InfoBarSeverity.Warning, title, message);
        }

        // ---------------- 录制 ----------------

        private bool _recLoading;

        /// <summary>刷新录制页。rebuildTargets 为 true 时重建目标列表。</summary>
        private void RefreshRecording(bool rebuildTargets)
        {
            _recLoading = true;
            try
            {
                var o = _app.Settings.Recording;
                bool busy = _app.IsRecording;

                if (rebuildTargets && !busy)
                {
                    string keep = SelectedTargetKey();
                    CbRecTarget.Items.Clear();
                    foreach (var t in _app.EnumerateCaptureTargets()) CbRecTarget.Items.Add(t);
                    if (CbRecTarget.Items.Count > 0)
                    {
                        int idx = 0;
                        for (int i = 0; i < CbRecTarget.Items.Count; i++)
                        {
                            var t = CbRecTarget.Items[i] as Recording.CaptureTarget;
                            if (t != null && KeyOf(t) == keep) { idx = i; break; }
                        }
                        CbRecTarget.SelectedIndex = idx;
                    }
                }

                CbRecAudio.SelectedIndex = (int)o.Audio;
                SwRecCursor.IsChecked = o.CaptureCursor;
                SwRecAuto.IsChecked = o.AutoRecordWithDesktop;
                RecFolder.Text = string.IsNullOrEmpty(o.OutputFolder)
                    ? Recording.RecordingOptions.DefaultFolder : o.OutputFolder;

                if (CbRecFps.Items.Count == 0 || IndexOfChoice(CbRecFps, o.FrameRate) < 0)
                    PopulateRecFps(o.FrameRate);
                SelectChoice(CbRecFps, o.FrameRate);
                if (CbRecSegment.Items.Count == 0 || IndexOfChoice(CbRecSegment, o.SegmentMinutes) < 0)
                    PopulateRecSegments(o.SegmentMinutes);
                SelectChoice(CbRecSegment, o.SegmentMinutes);
                CbRecBitrate.SelectedIndex = BitrateIndex(o.BitrateMbps);

                RecAudioHint.Text = L.T("系统声音录的是本机所有播放的声音（含分身桌面）；麦克风需在隐私设置中允许桌面应用访问。");
                CbRecAudio.IsEnabled = !busy;

                CbRecTarget.IsEnabled = !busy;
                CbRecFps.IsEnabled = !busy;
                CbRecBitrate.IsEnabled = !busy;
                CbRecSegment.IsEnabled = !busy;
                SwRecCursor.IsEnabled = !busy;
                BtnRecStart.IsEnabled = !busy && CbRecTarget.Items.Count > 0;
                BtnRecStop.IsEnabled = busy;
                BtnRecPause.IsEnabled = busy;
                BtnRecPause.Content = _app.IsRecordingPaused ? L.T("继续") : L.T("暂停");
                BtnScreenshot.IsEnabled = !_screenshotBusy;

                UpdateRecordingStatus();
            }
            finally { _recLoading = false; }
        }

        private void UpdateRecordingStatus()
        {
            bool busy = _app.IsRecording;
            var tone = busy ? ToneRed : ToneGray;

            RecTitle.Text = busy ? L.T("正在录制") : L.T("未在录制");
            RecDetail.Text = busy
                ? L.T("录制中的目标：") + (_app.RecordingTargetName ?? "—")
                : L.T("选择目标后点「开始录制」。");
            RecIcon.Symbol = busy
                ? global::Wpf.Ui.Controls.SymbolRegular.Record24
                : global::Wpf.Ui.Controls.SymbolRegular.Video24;
            RecElapsed.Text = busy ? FormatUptime(_app.RecordingStartedAt) : "";

            RecIcon.Foreground = tone.Fore;
            RecIconBg.Background = tone.Tint;
            RecElapsed.Foreground = tone.Fore;
        }

        private string SelectedTargetKey()
        {
            var t = CbRecTarget.SelectedItem as Recording.CaptureTarget;
            return t == null ? null : KeyOf(t);
        }

        private static string KeyOf(Recording.CaptureTarget t)
        {
            return t.Kind + "|" + (t.DeviceName ?? t.Title);
        }

        private static readonly int[] RecFps = { 1, 2, 5, 15, 24, 30, 60 };
        private const int TimelapseMaxFps = 5;
        private static readonly int[] RecSegments = { 0, 15, 30, 60 };
        private static readonly int[] RecBitrates = { 8, 12, 20, 40 };

        private void PopulateRecFps(int current)
        {
            CbRecFps.Items.Clear();
            bool found = false;
            foreach (int f in RecFps)
            {
                CbRecFps.Items.Add(new ChoiceItem(f, FpsLabel(f)));
                if (f == current) found = true;
            }
            if (!found && current > 0) CbRecFps.Items.Add(new ChoiceItem(current, FpsLabel(current)));
        }

        private static string FpsLabel(int fps)
        {
            return fps <= TimelapseMaxFps
                ? string.Format(L.T("{0} FPS（延时）"), fps)
                : fps + " FPS";
        }

        private void PopulateRecSegments(int current)
        {
            CbRecSegment.Items.Clear();
            bool found = false;
            foreach (int m in RecSegments)
            {
                CbRecSegment.Items.Add(new ChoiceItem(m, SegmentLabel(m)));
                if (m == current) found = true;
            }
            if (!found && current > 0) CbRecSegment.Items.Add(new ChoiceItem(current, SegmentLabel(current)));
        }

        private static string SegmentLabel(int minutes)
        {
            return minutes <= 0 ? L.T("不分段") : string.Format(L.T("每 {0} 分钟"), minutes);
        }

        private static int BitrateIndex(int mbps)
        {
            for (int i = 0; i < RecBitrates.Length; i++) if (RecBitrates[i] == mbps) return i;
            return 1;
        }

        private void OnRefreshTargets(object sender, RoutedEventArgs e)
        {
            RefreshRecording(true);
        }

        private void OnRecOptionChanged(object sender, SelectionChangedEventArgs e) { SaveRecOptions(); }
        private void OnRecOptionChanged2(object sender, RoutedEventArgs e) { SaveRecOptions(); }

        private void SaveRecOptions()
        {
            if (_recLoading) return;
            var o = _app.Settings.Recording;
            if (CbRecAudio.SelectedIndex >= 0) o.Audio = (Recording.AudioSource)CbRecAudio.SelectedIndex;
            var fps = CbRecFps.SelectedItem as ChoiceItem;
            if (fps != null) o.FrameRate = fps.Value;
            if (CbRecBitrate.SelectedIndex >= 0 && CbRecBitrate.SelectedIndex < RecBitrates.Length)
                o.BitrateMbps = RecBitrates[CbRecBitrate.SelectedIndex];
            var seg = CbRecSegment.SelectedItem as ChoiceItem;
            if (seg != null) o.SegmentMinutes = seg.Value;
            o.CaptureCursor = SwRecCursor.IsChecked == true;
            o.AutoRecordWithDesktop = SwRecAuto.IsChecked == true;
            SettingsStore.Save(_app.Settings);
        }

        private void OnStartRecording(object sender, RoutedEventArgs e)
        {
            var target = CbRecTarget.SelectedItem as Recording.CaptureTarget;
            if (target == null) { Warn(L.T("请先选择录制目标。")); return; }

            SaveRecOptions();
            string err = _app.StartRecording(target);
            ShowLastOutput(null);
            if (err != null)
            {
                SetBar(RecBar, global::Wpf.Ui.Controls.InfoBarSeverity.Error,
                    L.T("无法开始录制"),
                    err);
            }
            else
            {
                SetBar(RecBar, global::Wpf.Ui.Controls.InfoBarSeverity.Success,
                    L.T("录制已开始"),
                    L.T("文件将保存到 ") + RecFolder.Text);
            }
            RefreshRecording(false);
        }

        private void OnPauseRecording(object sender, RoutedEventArgs e)
        {
            _app.TogglePauseRecording();
            RefreshRecording(false);
        }

        private bool _screenshotBusy;

        private void OnScreenshot(object sender, RoutedEventArgs e)
        {
            if (_screenshotBusy) return;
            _screenshotBusy = true;
            BtnScreenshot.IsEnabled = false;
            BtnScreenshot.Content = L.T("正在截图…");

            try
            {
                _app.TakeScreenshot(null, delegate(string path, string err) { OnScreenshotDone(path, err); });
            }
            catch (Exception ex)
            {
                Log.Error("发起截图失败", ex);
                OnScreenshotDone(null, ex.Message);
            }
        }

        private void OnScreenshotDone(string path, string err)
        {
            _screenshotBusy = false;
            if (_closed) return;
            BtnScreenshot.Content = L.T("截图");
            BtnScreenshot.IsEnabled = true;

            if (string.IsNullOrEmpty(path))
            {
                SetBar(RecBar, global::Wpf.Ui.Controls.InfoBarSeverity.Error,
                    L.T("截图失败"),
                    err ?? L.T("详见日志。"));
                ShowLastOutput(null);
            }
            else
            {
                SetBar(RecBar, global::Wpf.Ui.Controls.InfoBarSeverity.Success,
                    L.T("已截图"),
                    path);
                ShowLastOutput(path);
            }
        }

        private void OnStopRecording(object sender, RoutedEventArgs e)
        {
            _app.StopRecording();
            RefreshRecording(false);
        }

        private void OnPickRecFolder(object sender, RoutedEventArgs e)
        {
            var o = _app.Settings.Recording;
            string start = string.IsNullOrEmpty(o.OutputFolder)
                ? Recording.RecordingOptions.DefaultFolder : o.OutputFolder;

            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            string picked = ParaDesk.Ui.FolderPicker.Pick(hwnd, L.T("选择录制文件的保存位置"), start);
            if (string.IsNullOrEmpty(picked)) return;

            try { System.IO.Directory.CreateDirectory(picked); }
            catch (Exception ex)
            {
                Warn(L.T("无法使用该文件夹：") + ex.Message);
                return;
            }

            o.OutputFolder = picked;
            SettingsStore.Save(_app.Settings);
            RefreshRecording(false);
            Log.Info("录制保存位置已改为 " + picked);
        }

        private void OnOpenRecFolder(object sender, RoutedEventArgs e)
        {
            string dir = _app.Settings.Recording.OutputFolder;
            if (string.IsNullOrEmpty(dir)) dir = Recording.RecordingOptions.DefaultFolder;
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                Log.Error("打开录制目录失败", ex);
                Warn(L.T("无法打开文件夹：") + ex.Message);
            }
        }

        private string _lastOutputPath;

        private void ShowLastOutput(string path)
        {
            _lastOutputPath = string.IsNullOrEmpty(path) ? null : path;
            RecFileActions.Visibility = _lastOutputPath != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnOpenLastOutput(object sender, RoutedEventArgs e)
        {
            string path = _lastOutputPath;
            if (path == null) return;
            if (!System.IO.File.Exists(path)) { Warn(L.T("文件已不存在：") + path); return; }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("打开文件失败: " + path, ex);
                Warn(L.T("无法打开该文件：") + ex.Message);
            }
        }

        private void OnRevealLastOutput(object sender, RoutedEventArgs e)
        {
            RevealInExplorer(_lastOutputPath);
        }

        private void RevealInExplorer(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (System.IO.File.Exists(path))
                {
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                    return;
                }
                string dir = System.IO.Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
                {
                    Warn(L.T("文件已不存在：") + path);
                    return;
                }
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                Log.Error("在资源管理器中定位文件失败: " + path, ex);
                Warn(L.T("无法打开文件夹：") + ex.Message);
            }
        }

        /// <summary>录制结束后由 AppContext 回调，用于提示结果。</summary>
        public void OnRecordingFinished(string path, string error)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((Action)delegate { OnRecordingFinished(path, error); });
                return;
            }
            if (_closed) return;
            if (error == null)
            {
                SetBar(RecBar, global::Wpf.Ui.Controls.InfoBarSeverity.Success,
                    L.T("录制完成"),
                    path);
                ShowLastOutput(path);
            }
            else
            {
                SetBar(RecBar, global::Wpf.Ui.Controls.InfoBarSeverity.Error,
                    L.T("录制未成功"),
                    error);
                ShowLastOutput(null);
            }
            RefreshRecording(true);
        }

        // ---------------- 热键 ----------------

        private const int HotkeyModMask = 0x1 | 0x2 | 0x4 | 0x8;

        private sealed class HotkeySlot
        {
            public readonly string Action;
            public readonly string Label;
            public readonly HotkeyBox Box;

            public HotkeySlot(string action, string label, HotkeyBox box)
            {
                Action = action;
                Label = label;
                Box = box;
            }
        }

        private HotkeySlot[] _hotkeySlots;

        private void InitHotkeySlots()
        {
            _hotkeySlots = new[]
            {
                new HotkeySlot("toggleDesktop", "显示 / 收起分身桌面", HkToggle),
                new HotkeySlot("toggleViewOnly", "切换「仅查看」", HkViewOnly),
                new HotkeySlot("toggleRecording", "开始 / 停止录制", HkRecord),
                new HotkeySlot("screenshot", "截图", HkScreenshot),
                new HotkeySlot("togglePause", "暂停 / 继续录制", HkPause),
                new HotkeySlot("detach", "收起分身桌面", HkDetach),
                new HotkeySlot("pushClipboard", "把剪贴板发送到分身桌面", HkPushClip),
                new HotkeySlot("pullClipboard", "从分身桌面取回剪贴板", HkPullClip),
            };
        }

        private HotkeySlot FindSlot(HotkeyBox box)
        {
            if (box == null || _hotkeySlots == null) return null;
            foreach (var slot in _hotkeySlots) if (ReferenceEquals(slot.Box, box)) return slot;
            return null;
        }

        private string ActionLabel(string action)
        {
            if (_hotkeySlots != null)
            {
                foreach (var slot in _hotkeySlots)
                    if (string.Equals(slot.Action, action, StringComparison.Ordinal)) return L.T(slot.Label);
            }
            return action;
        }

        private static HotkeyBinding FindBinding(AppSettings s, string action)
        {
            if (s.Hotkeys == null) return null;
            foreach (var b in s.Hotkeys)
                if (b != null && string.Equals(b.Action, action, StringComparison.Ordinal)) return b;
            return null;
        }

        private static void ShowSavedHotkey(AppSettings s, HotkeySlot slot)
        {
            var b = FindBinding(s, slot.Action);
            if (b != null && b.Enabled && b.Key != 0) slot.Box.SetHotkey(b.Modifiers, b.Key);
            else slot.Box.SetHotkey(0, 0);
        }

        private void WarnReservedHotkeys()
        {
            if (_hotkeySlots == null) return;
            var s = _app.Settings;
            var list = new List<string>();
            foreach (var slot in _hotkeySlots)
            {
                var b = FindBinding(s, slot.Action);
                if (b == null || !b.Enabled || b.Key == 0) continue;
                string reason = HotkeyBox.ReservedReason(b.Modifiers, b.Key);
                if (reason != null) list.Add(L.T(slot.Label) + " — " + reason);
            }
            if (list.Count == 0) return;

            SetBar(HotkeyBar, global::Wpf.Ui.Controls.InfoBarSeverity.Warning,
                L.T("有热键占用了保留的组合键"),
                string.Format(L.T("以下热键占用了系统或常用程序在用的组合键：{0}"),
                    string.Join(" / ", list.ToArray())));
        }

        private static string FindConflictingAction(AppSettings s, string action, int mods, int vk)
        {
            if (s.Hotkeys == null || vk == 0) return null;
            int m = mods & HotkeyModMask;
            foreach (var b in s.Hotkeys)
            {
                if (b == null || !b.Enabled || b.Key == 0) continue;
                if (string.Equals(b.Action, action, StringComparison.Ordinal)) continue;
                if ((b.Modifiers & HotkeyModMask) == m && b.Key == vk) return b.Action;
            }
            return null;
        }

        private void OnHotkeyChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            var slot = FindSlot(sender as HotkeyBox);
            if (slot == null) return;

            var s = _app.Settings;
            int mods = slot.Box.Modifiers;
            int vk = slot.Box.VirtualKey;

            string other = FindConflictingAction(s, slot.Action, mods, vk);
            if (other != null)
            {
                ShowSavedHotkey(s, slot);
                SetBar(HotkeyBar, global::Wpf.Ui.Controls.InfoBarSeverity.Warning,
                    L.T("热键冲突，未保存"),
                    string.Format(L.T("{0} 与「{1}」冲突：两个动作不能共用一个组合。请换一个，或先清除那一项。"),
                        HotkeyService.Describe(mods, vk), ActionLabel(other)));
                return;
            }

            var binding = FindBinding(s, slot.Action);
            if (binding == null)
            {
                binding = new HotkeyBinding { Action = slot.Action };
                if (s.Hotkeys == null) s.Hotkeys = new List<HotkeyBinding>();
                s.Hotkeys.Add(binding);
            }
            binding.Modifiers = mods;
            binding.Key = vk;
            binding.Enabled = vk != 0;

            SettingsStore.Save(s);
            ViewOnlyQuickHint.Text = DescribeViewOnlyHint(s);
            var failed = _app.ApplyHotkeys();
            if (failed != null && failed.Count > 0)
            {
                var names = new List<string>();
                foreach (string a in failed) names.Add(ActionLabel(a));
                SetBar(HotkeyBar, global::Wpf.Ui.Controls.InfoBarSeverity.Warning,
                    L.T("有热键注册失败"),
                    string.Format(L.T("以下热键没注册上，组合可能已被其它程序占用，请换一个：{0}"),
                        string.Join(" / ", names.ToArray())));
            }
            else
            {
                SetBar(HotkeyBar, global::Wpf.Ui.Controls.InfoBarSeverity.Success,
                    L.T("热键已生效"),
                    L.T("现在可以在任何程序里使用这些组合键。"));
            }
        }

        // ---------------- 配置方案 ----------------

        private sealed class ProfileItem
        {
            public readonly string Name;
            public ProfileItem(string name) { Name = name; }
            public override string ToString() { return Name; }
        }

        private void OnProfileSwitched(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            var item = CbProfile.SelectedItem as ProfileItem;
            if (item == null || string.IsNullOrEmpty(item.Name)) return;
            if (string.Equals(_app.Settings.ActiveProfile, item.Name, StringComparison.Ordinal)) return;

            if (_startupEditProfile != null) SaveStartupCommand();

            _app.SelectProfile(item.Name);
            RefreshAll();
        }

        private void OnProfileAdd(object sender, RoutedEventArgs e)
        {
            if (_startupEditProfile != null) SaveStartupCommand();

            var cur = _app.Settings.GetActiveProfile();
            // 以当前设置为蓝本复制一份，用户改完即得第二套方案
            var copy = new DesktopProfile
            {
                Name = cur.Name,
                MonitorDevice = cur.MonitorDevice,
                WindowMode = cur.WindowMode,
                ResolutionMode = cur.ResolutionMode,
                CustomWidth = cur.CustomWidth,
                CustomHeight = cur.CustomHeight,
                ScalePercent = cur.ScalePercent,
                ViewOnly = cur.ViewOnly,
                AlwaysOnTop = cur.AlwaysOnTop,
                Clipboard = cur.Clipboard,
                DesktopAudio = cur.DesktopAudio,
                StartupCommand = cur.StartupCommand,
                StartupWorkingDir = cur.StartupWorkingDir,
                KeepAwake = cur.KeepAwake,
                WindowX = cur.WindowX,
                WindowY = cur.WindowY,
                WindowWidth = cur.WindowWidth,
                WindowHeight = cur.WindowHeight,
            };
            string name = _app.Settings.AddProfile(copy);
            _app.SelectProfile(name);
            RefreshAll();
            Info(string.Format(L.T("已创建方案「{0}」，可以直接修改它的显示设置。"), name));
        }

        private void OnProfileDelete(object sender, RoutedEventArgs e)
        {
            var s = _app.Settings;
            if (s.Profiles.Count <= 1) return;
            string name = s.GetActiveProfile().Name;

            if (MessageBox.Show(this, string.Format(L.T("确定删除方案「{0}」吗？"), name),
                    AppInfo.ProductName, MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes) return;

            _startupEditProfile = null;
            if (_startupSaveTimer != null) _startupSaveTimer.Stop();

            if (s.RemoveProfile(name))
            {
                SettingsStore.Save(s);
                _app.SelectProfile(s.ActiveProfile);
                _app.ApplyDisplayChanges();
                RefreshAll();
                Log.Info("已删除方案: " + name);
            }
        }

        private void OnLayoutSelectionChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            string device = LayoutView.SelectedDevice;
            if (string.IsNullOrEmpty(device)) return;
            ApplySelectedMonitor(device);
        }

        private void OnMonitorPicked(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            var item = CbMonitor.SelectedItem as MonitorItem;
            if (item == null) return;

            // 已断开的占位项不可选中触发写回：Resolve 的回退本来是临时、可恢复的，
            // 一旦把回退结果写进 profile，屏插回来也回不到原来那块了。
            if (item.Detached) return;

            ApplySelectedMonitor(item.Device);
        }

        /// <summary>
        /// 改目标显示器的唯一写入路径，「桌面」页布局图与「画面」页下拉共用。
        /// device 传 null 表示"自动"（主屏之外的第一块），这个语义必须原样存回去，
        /// 存成具体设备名就等于替用户做了决定。
        /// </summary>
        private void ApplySelectedMonitor(string device)
        {
            var p = _app.Settings.GetActiveProfile();
            if (string.Equals(p.MonitorDevice, device, StringComparison.Ordinal)) return;

            p.MonitorDevice = device;
            SettingsStore.Save(_app.Settings);
            _app.ApplyDisplayChanges();   // 运行中就当场搬家，否则用户以为没生效
            RefreshAll();
        }

        private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            var p = _app.Settings.GetActiveProfile();

            if (CbWindowMode.SelectedIndex >= 0) p.WindowMode = (WindowMode)CbWindowMode.SelectedIndex;
            ApplyResolutionChoice(p, CbResolution.SelectedIndex);
            p.ScalePercent = (CbScale.SelectedIndex >= 0 && CbScale.SelectedIndex < ScaleValues.Length)
                ? ScaleValues[CbScale.SelectedIndex] : 0;

            SettingsStore.Save(_app.Settings);
            _app.ApplyDisplayChanges();
        }

        private void OnViewOnly(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var sw = sender as ToggleButton;
            if (sw == null) return;
            bool on = sw.IsChecked == true;

            bool was = _loading;
            _loading = true;
            try
            {
                SwViewOnly.IsChecked = on;
                SwViewOnlyQuick.IsChecked = on;
            }
            finally { _loading = was; }

            _app.SetViewOnly(on);
        }

        private static string DescribeViewOnlyHint(AppSettings s)
        {
            string text = L.T("你的键鼠不会作用到分身桌面；AI 注入的操作不受影响");
            var b = FindBinding(s, "toggleViewOnly");
            if (b != null && b.Enabled && b.Key != 0)
                text += string.Format(L.T("。热键 {0}"), HotkeyService.Describe(b.Modifiers, b.Key));
            return text;
        }

        private void OnTopMost(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _app.SetAlwaysOnTop(SwTopMost.IsChecked == true);
        }

        private void OnStartup(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var s = _app.Settings;
            s.RunAtStartup = SwStartup.IsChecked == true;
            if (!StartupRegistration.SetEnabled(s.RunAtStartup, s.StartMinimized))
                Warn(L.T("设置开机自启失败，详见日志。"));
            SettingsStore.Save(s);
        }

        private void OnStartMinimized(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var s = _app.Settings;
            s.StartMinimized = SwStartMinimized.IsChecked == true;
            SettingsStore.Save(s);

            if (StartupRegistration.IsEnabled() && !StartupRegistration.SetEnabled(true, s.StartMinimized))
                Warn(L.T("设置开机自启失败，详见日志。"));
        }

        private void OnAppBehaviorChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var s = _app.Settings;
            s.AutoStartDesktop = SwAutoStartDesktop.IsChecked == true;
            s.CheckUpdates = SwCheckUpdates.IsChecked == true;
            s.AutoReattach = SwAutoReattach.IsChecked == true;
            s.ConfirmBeforeClose = SwConfirmClose.IsChecked == true;
            s.AutoLogoffOnShutdown = SwLogoffOnShutdown.IsChecked == true;
            SettingsStore.Save(s);
        }

        private void OnLanguage(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            string lang = CbLanguage.SelectedIndex == 1 ? "zh"
                        : CbLanguage.SelectedIndex == 2 ? "en" : "auto";
            if (_app.Settings.Language == lang) return;

            _app.Settings.Language = lang;
            SettingsStore.Save(_app.Settings);

            // 就地换语言要重走一遍界面树遍历 + RefreshAll（枚举显示器、
            // 逐屏跑 EnumDisplaySettings、读注册表），肉眼看上去就是卡住几秒。
            // 重启一次反而更快、也更彻底——所有窗口、托盘菜单、已缓存的文案一起换。
            _app.RestartForLanguage();
        }

        private void OnTray(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _app.Settings.MinimizeToTray = SwTray.IsChecked == true;
            SettingsStore.Save(_app.Settings);
        }

        private void OnCopyClaudeHooks(object sender, RoutedEventArgs e)
        {
            if (!CopyToClipboard(BuildClaudeHooksJson())) return;
            ShowBar(AiBar, global::Wpf.Ui.Controls.InfoBarSeverity.Success,
                L.T("已复制 Claude Code 通知配置"),
                L.T("把其中的 hooks 合并进 Claude Code 的 settings.json（用户级在 ~/.claude/settings.json，也可以放项目里的 .claude/settings.json）。之后任务完成或需要你确认时，主屏会收到提醒。"));
        }

        private void OnCopyCliUsage(object sender, RoutedEventArgs e)
        {
            if (!CopyToClipboard(BuildCliUsage())) return;
            ShowBar(AiBar, global::Wpf.Ui.Controls.InfoBarSeverity.Success,
                L.T("已复制命令行用法"),
                L.T("可以直接贴给分身桌面里的 agent，或写进项目的说明文件，让它在需要时叫你。"));
        }

        private static string BuildClaudeHooksJson()
        {
            string exe = "\"" + AppInfo.ExecutablePath + "\"";
            string done = exe + " --notify --title \"Claude Code\" --body \"" + L.T("任务完成") + "\" --sound";
            string ask = exe + " --notify --title \"Claude Code\" --body \"" + L.T("需要你确认") + "\" --level warn --sound";

            var sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append("  \"hooks\": {\r\n");
            sb.Append("    \"Stop\": [ { \"hooks\": [ { \"type\": \"command\", \"command\": ")
              .Append(JsonString(done)).Append(" } ] } ],\r\n");
            sb.Append("    \"Notification\": [ { \"hooks\": [ { \"type\": \"command\", \"command\": ")
              .Append(JsonString(ask)).Append(" } ] } ]\r\n");
            sb.Append("  }\r\n");
            sb.Append("}\r\n");
            return sb.ToString();
        }

        private static string BuildCliUsage()
        {
            string exe = "\"" + AppInfo.ExecutablePath + "\"";
            var sb = new StringBuilder();
            sb.Append("# ").Append(L.T("ParaDesk 命令行（分身桌面里也能用）。PowerShell 里要在路径前加 & ，例如 & \"...\\ParaDesk.exe\" --status")).Append("\r\n\r\n");
            sb.Append("# ").Append(L.T("查询状态；加 --json 输出 JSON。退出码 3 表示 ParaDesk 没在运行")).Append("\r\n");
            sb.Append(exe).Append(" --status\r\n\r\n");
            sb.Append("# ").Append(L.T("在主屏提醒你（--level warn / error 用醒目样式，--sound 响一声）")).Append("\r\n");
            sb.Append(exe).Append(" --notify --title \"Claude Code\" --body \"").Append(L.T("任务完成")).Append("\" --sound\r\n\r\n");
            sb.Append("# ").Append(L.T("截图，输出文件路径")).Append("\r\n");
            sb.Append(exe).Append(" --screenshot\r\n\r\n");
            sb.Append("# ").Append(L.T("开始 / 停止录制")).Append("\r\n");
            sb.Append(exe).Append(" --record start\r\n");
            sb.Append(exe).Append(" --record stop\r\n\r\n");
            sb.Append("# ").Append(L.T("列出全部命令")).Append("\r\n");
            sb.Append(exe).Append(" --help\r\n");
            return sb.ToString();
        }

        private static string JsonString(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private bool CopyToClipboard(string text)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("写入剪贴板失败: " + ex.Message);
                ShowBar(AiBar, global::Wpf.Ui.Controls.InfoBarSeverity.Error,
                    L.T("复制失败"), L.T("剪贴板正被其它程序占用，请稍后再试。"));
                return false;
            }
        }

        private bool _updateBusy;
        private string _updateUrl;

        private void OnCheckUpdate(object sender, RoutedEventArgs e)
        {
            if (_updateBusy) return;
            _updateBusy = true;
            BtnCheckUpdate.IsEnabled = false;
            BtnCheckUpdate.Content = L.T("正在检查…");
            BtnOpenDownload.Visibility = Visibility.Collapsed;
            HideBar(UpdateBar);

            try
            {
                UpdateChecker.CheckAsync(delegate(UpdateInfo info, string err)
                {
                    try { Dispatcher.BeginInvoke((Action)delegate { OnUpdateChecked(info, err); }); }
                    catch (Exception ex) { Log.Error("回填更新检查结果失败", ex); }
                });
            }
            catch (Exception ex)
            {
                Log.Error("发起更新检查失败", ex);
                OnUpdateChecked(null, ex.Message);
            }
        }

        private void OnUpdateChecked(UpdateInfo info, string err)
        {
            _updateBusy = false;
            if (_closed) return;
            BtnCheckUpdate.IsEnabled = true;
            BtnCheckUpdate.Content = L.T("检查更新");

            if (info == null)
            {
                ShowBar(UpdateBar, global::Wpf.Ui.Controls.InfoBarSeverity.Warning,
                    L.T("检查更新失败"), err ?? L.T("详见日志。"));
                return;
            }

            if (info.IsNewer)
            {
                _updateUrl = SafeReleaseUrl(info.Url);
                BtnOpenDownload.Visibility = Visibility.Visible;
                ShowBar(UpdateBar, global::Wpf.Ui.Controls.InfoBarSeverity.Success,
                    string.Format(L.T("发现新版本 {0}"), info.LatestVersion),
                    string.Format(L.T("当前版本 {0}。点「打开下载页」前往 GitHub 下载。"), AppInfo.Version));
            }
            else
            {
                ShowBar(UpdateBar, global::Wpf.Ui.Controls.InfoBarSeverity.Success,
                    L.T("已是最新版本"),
                    string.Format(L.T("当前版本 {0}，GitHub 上最新发布为 {1}。"), AppInfo.Version, info.LatestVersion));
            }
        }

        private static string SafeReleaseUrl(string url)
        {
            Uri u;
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out u) &&
                u.Scheme == Uri.UriSchemeHttps &&
                (string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                 u.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)))
                return u.AbsoluteUri;
            return UpdateChecker.ReleasesPage;
        }

        private void OnOpenDownload(object sender, RoutedEventArgs e)
        {
            string url = _updateUrl ?? UpdateChecker.ReleasesPage;
            try { System.Diagnostics.Process.Start(url); }
            catch (Exception ex)
            {
                Log.Error("打开下载页失败", ex);
                Warn(L.T("无法打开浏览器，请手动访问：") + "\r\n" + url);
            }
        }

        private void OnCredentials(object sender, RoutedEventArgs e)
        {
            try
            {
                // 不设 Owner：设了之后窗口创建成功却始终不可见，
                // 与向导（同样是 FluentWindow、不设 Owner）对照可确认是这个差异
                new CredentialDialog().ShowDialog();
            }
            catch (Exception ex)
            {
                Log.Error("打开凭据对话框失败", ex);
                Warn(L.T("打开凭据对话框失败，详见日志。"));
            }
            RefreshAll();
        }

        private void OnViewLogs(object sender, RoutedEventArgs e)
        {
            try
            {
                var v = new LogViewer();
                v.Owner = this;
                v.Show();
            }
            catch (Exception ex)
            {
                Log.Error("打开日志查看器失败", ex);
                Warn(L.T("打开日志查看器失败：") + ex.Message + "\r\n\r\n" +
                     L.T("日志文件在：") + "\r\n" + Log.Path0);
            }
        }

        private void OnShowWizard(object sender, RoutedEventArgs e)
        {
            _app.ShowWizard();
            RefreshAll();
        }

        private void OnOpenLogs(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + Log.Dir + "\""); }
            catch (Exception ex)
            {
                Log.Error("打开日志文件夹失败", ex);
                Warn(L.T("无法打开文件夹：") + ex.Message + "\r\n\r\n" + Log.Dir);
            }
        }

        // ---------------- 关闭 ----------------

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_startupEditProfile != null) SaveStartupCommand();

            // 关窗默认只是收进托盘，进程继续存活
            if (_app.Settings.MinimizeToTray && !_app.IsExiting)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            if (_ticker != null) { _ticker.Stop(); _ticker = null; }
            if (_refreshTimer != null) _refreshTimer.Stop();
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            if (_refreshTimer != null) _refreshTimer.Stop();
            if (_startupSaveTimer != null) _startupSaveTimer.Stop();
            base.OnClosed(e);
        }

        // ---------------- 工具 ----------------

        private void Info(string msg)
        {
            MessageBox.Show(this, msg, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Warn(string msg)
        {
            MessageBox.Show(this, msg, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private static void SetBar(global::Wpf.Ui.Controls.InfoBar bar,
            global::Wpf.Ui.Controls.InfoBarSeverity severity, string title, string message)
        {
            bar.Severity = severity;
            bar.Title = title ?? "";
            bar.Message = message ?? "";
            ReleaseTemplateText(bar, bar);
        }

        private static void ReleaseTemplateText(DependencyObject node, DependencyObject owner)
        {
            int count;
            try { count = VisualTreeHelper.GetChildrenCount(node); }
            catch { return; }
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                var tb = child as TextBlock;
                if (tb != null && ReferenceEquals(tb.TemplatedParent, owner) &&
                    DependencyPropertyHelper.GetValueSource(tb, TextBlock.TextProperty).BaseValueSource
                        == BaseValueSource.Local)
                {
                    tb.ClearValue(TextBlock.TextProperty);
                }
                ReleaseTemplateText(child, owner);
            }
        }

        private static void ShowBar(global::Wpf.Ui.Controls.InfoBar bar,
            global::Wpf.Ui.Controls.InfoBarSeverity severity, string title, string message)
        {
            SetBar(bar, severity, title, message);
            bar.Visibility = Visibility.Visible;
        }

        private static void HideBar(global::Wpf.Ui.Controls.InfoBar bar)
        {
            bar.Visibility = Visibility.Collapsed;
        }

        private sealed class ChoiceItem
        {
            public readonly int Value;
            private readonly string _text;
            public ChoiceItem(int value, string text) { Value = value; _text = text; }
            public override string ToString() { return _text; }
        }

        private static int IndexOfChoice(ComboBox cb, int value)
        {
            for (int i = 0; i < cb.Items.Count; i++)
            {
                var c = cb.Items[i] as ChoiceItem;
                if (c != null && c.Value == value) return i;
            }
            return -1;
        }

        private static void SelectChoice(ComboBox cb, int value)
        {
            cb.SelectedIndex = IndexOfChoice(cb, value);
        }

        /// <summary>
        /// 目标显示器下拉的条目。
        /// 用对象而不是裸 string：RefreshAll 末尾的 Localizer 会把 Content 为 string
        /// 的节点整个换掉，而 MonitorInfo.Display 已经过一次 L.T，二次翻译会出错；
        /// 条目是对象时 Localizer 的 `as string` 返回 null，天然免疫。
        /// </summary>
        private sealed class MonitorItem
        {
            public readonly string Device;      // null = 自动
            public readonly bool Detached;      // profile 指向的屏已不在
            private readonly string _text;
            public MonitorItem(string device, string text, bool detached)
            {
                Device = device; _text = text; Detached = detached;
            }
            public override string ToString() { return _text; }
        }

        /// <summary>填充目标显示器下拉，并回填当前方案的选中项。</summary>
        private void PopulateMonitors(List<MonitorInfo> mons, DesktopProfile p)
        {
            CbMonitor.Items.Clear();

            // 条目只放挑屏需要的信息。设备名（DISPLAY1）对选择毫无帮助，
            // "主屏之外的第一块"这类解释归副标题——都塞进条目会被下拉框宽度截断。
            var fallback = MonitorService.Resolve(null);
            string autoText = fallback != null
                ? string.Format(L.T("自动（现在是 {0}）"), MonitorNaming.NameOf(fallback))
                : L.T("自动");
            CbMonitor.Items.Add(new MonitorItem(null, autoText, false));

            int selected = 0;
            for (int i = 0; i < mons.Count; i++)
            {
                var m = mons[i];
                string text = MonitorNaming.Describe(m);
                CbMonitor.Items.Add(new MonitorItem(m.DeviceName, text, false));
                if (string.Equals(m.DeviceName, p.MonitorDevice, StringComparison.Ordinal))
                    selected = i + 1;
            }

            // 目标屏被拔掉：绝不静默改写 MonitorDevice，只补一条占位项让用户看见。
            // Resolve 遇到这种情况只写日志就默默回退了，界面上一点痕迹都没有。
            if (!string.IsNullOrEmpty(p.MonitorDevice) && selected == 0)
            {
                // "暂用 显示器 N"归副标题，条目里再写一遍会被下拉框宽度截断
                string text = string.Format(L.T("{0}（已断开）"), ShortDevice(p.MonitorDevice));
                CbMonitor.Items.Add(new MonitorItem(p.MonitorDevice, text, true));
                selected = CbMonitor.Items.Count - 1;
            }

            CbMonitor.SelectedIndex = selected;
        }

        private static string ShortDevice(string device)
        {
            return string.IsNullOrEmpty(device) ? "" : device.Replace(@"\\.\", "");
        }

        /// <summary>分辨率卡片副标题：把"目标显示器"换成点名的那块屏。</summary>
        private void UpdateResolutionHint(List<MonitorInfo> mons, DesktopProfile p)
        {
            var m = string.IsNullOrEmpty(p.MonitorDevice)
                ? MonitorService.Resolve(null)
                : MonitorService.Find(mons, p.MonitorDevice);

            ResolutionHint.Text = m != null
                ? string.Format(L.T("连接中即时生效，无需断开重连；选项来自 {0} 实际支持的模式"), MonitorNaming.NameOf(m))
                : L.T("连接中即时生效，无需断开重连；选项来自目标显示器实际支持的模式");
        }

        /// <summary>目标显示器卡片的副标题——把当前处境一句话讲清楚。</summary>
        private void UpdateMonitorHint(List<MonitorInfo> mons, DesktopProfile p, bool attached)
        {
            string activeDev = _app.ActiveMonitorDevice;

            bool detached = !string.IsNullOrEmpty(p.MonitorDevice) &&
                            MonitorService.Find(mons, p.MonitorDevice) == null;

            if (detached)
            {
                var now = MonitorService.Resolve(p.MonitorDevice);
                MonitorHint.Text = now != null
                    ? string.Format(L.T("{0} 已断开，画面暂时放在 {1}；插回后会自动回到原屏。"),
                                    ShortDevice(p.MonitorDevice), MonitorNaming.NameOf(now))
                    : string.Format(L.T("{0} 已断开。"), ShortDevice(p.MonitorDevice));
            }
            else if (mons.Count <= 1)
            {
                MonitorHint.Text = L.T("这台电脑只有一块显示器，分身桌面会盖在主桌面上，用热键或「收起桌面」切回。");
            }
            else if (attached && !string.IsNullOrEmpty(activeDev))
            {
                var m = MonitorService.Find(mons, activeDev);
                MonitorHint.Text = m != null
                    ? string.Format(L.T("分身桌面正显示在 {0} 上；换一块会立刻搬过去。"), MonitorNaming.NameOf(m))
                    : L.T("分身桌面正在运行；换一块屏会立刻搬过去。");
            }
            else if (string.IsNullOrEmpty(p.MonitorDevice))
            {
                var fallback = MonitorService.Resolve(null);
                MonitorHint.Text = fallback != null
                    ? string.Format(L.T("未指定时用主屏之外的第一块，现在是 {0}。"), MonitorNaming.NameOf(fallback))
                    : L.T("下面的分辨率与缩放按这块屏的能力列出。");
            }
            else
            {
                MonitorHint.Text = L.T("下面的分辨率与缩放按这块屏的能力列出。");
            }
        }

        // ---------------- 由显示器能力驱动的选项 ----------------

        /// <summary>下拉项：Tag 挂真实模式，null 表示"跟随显示器"。</summary>
        private sealed class ModeItem
        {
            public readonly DisplayMode Mode;
            private readonly string _text;
            public ModeItem(DisplayMode m, string text) { Mode = m; _text = text; }
            public override string ToString() { return _text; }
        }

        private List<int> _fpsChoices = new List<int>();

        /// <summary>按目标显示器实际支持的模式填充分辨率列表。</summary>
        private void PopulateResolutions(string device, DesktopProfile p)
        {
            CbResolution.Items.Clear();

            var current = DisplayCapabilities.GetCurrent(device);
            string followText = current != null
                ? string.Format(L.T("跟随显示器（{0}）"), current.Resolution)
                : L.T("跟随显示器");
            CbResolution.Items.Add(new ModeItem(null, followText));

            var modes = DisplayCapabilities.GetResolutions(device);
            int selected = 0;
            for (int i = 0; i < modes.Count; i++)
            {
                var m = modes[i];
                string label = m.Resolution;
                string aspect = m.AspectLabel;
                if (!string.IsNullOrEmpty(aspect)) label += "　" + aspect;
                if (current != null && m.Width == current.Width && m.Height == current.Height)
                    label += "　" + L.T("原生");

                CbResolution.Items.Add(new ModeItem(m, label));

                if (p.ResolutionMode == ResolutionMode.Custom &&
                    m.Width == p.CustomWidth && m.Height == p.CustomHeight)
                    selected = i + 1;
            }

            // 已保存的分辨率若不在该屏支持列表里（换了屏的情形），补一条以免静默丢失
            if (p.ResolutionMode == ResolutionMode.Custom && selected == 0)
            {
                var m = new DisplayMode { Width = p.CustomWidth, Height = p.CustomHeight };
                CbResolution.Items.Add(new ModeItem(m, m.Resolution + "　" + L.T("（该屏未报告支持）")));
                selected = CbResolution.Items.Count - 1;
            }

            CbResolution.SelectedIndex = selected;
        }

        /// <summary>
        /// 填充帧率列表。
        ///
        /// 候选取**所有**显示器刷新率的并集，不跟着目标显示器变——因为这个值本来就是整机单值。
        /// 从前按单屏给候选，用户换一块屏就看到列表变了，会理所当然地以为帧率是按屏保存的。
        /// 超出当前目标屏能力的档另行标注，那是选值建议，不是保存范围。
        /// </summary>
        private void PopulateFps(List<MonitorInfo> mons, string device)
        {
            var devices = new List<string>();
            foreach (var m in mons) devices.Add(m.DeviceName);
            _fpsChoices = PerformanceSettings.GetFpsChoicesForAll(devices);

            CbFps.Items.Clear();

            var current = DisplayCapabilities.GetCurrent(device);
            int targetHz = current != null ? current.Frequency : 0;

            foreach (int fps in _fpsChoices)
            {
                string label = fps + " FPS";
                if (fps == 30) label += L.T("（系统默认）");
                else if (targetHz > 0 && fps > targetHz)
                    label += string.Format(L.T("（超出目标屏的 {0}Hz）"), targetHz);
                else if (targetHz > 0 && fps == targetHz) label += "　" + L.T("目标屏刷新率");
                CbFps.Items.Add(new FpsItem(fps, label));
            }

            // 注册表真实值若不在候选里，补一条带真实值的占位项并选中——
            // 从前这里是吸附到最近一档，等于让界面替注册表撒谎。
            int? interval = PerformanceSettings.GetFrameInterval();
            int exact = PerformanceSettings.ExactChoice(interval, _fpsChoices);
            if (exact >= 0)
            {
                CbFps.SelectedIndex = _fpsChoices.IndexOf(exact);
            }
            else
            {
                int real = PerformanceSettings.FrameIntervalToFps(interval);
                CbFps.Items.Add(new FpsItem(real,
                    string.Format(L.T("{0} FPS（当前整机设置）"), real)));
                CbFps.SelectedIndex = CbFps.Items.Count - 1;
            }

            UpdateApplyFpsState();
        }

        /// <summary>
        /// 帧率下拉的条目。必须携带 fps 值：插入"当前整机设置"占位项后，
        /// 索引与 _fpsChoices 就对不上了，再按索引取值会取到错的档。
        /// </summary>
        private sealed class FpsItem
        {
            public readonly int Fps;
            private readonly string _text;
            public FpsItem(int fps, string text) { Fps = fps; _text = text; }
            public override string ToString() { return _text; }
        }

        /// <summary>
        /// 只更新「应用」按钮的可用性，**不写注册表**。
        ///
        /// 这一项是要提权的整机改动。挂在 SelectionChanged 上时，
        /// 用键盘在下拉里划过几档就会连着弹几次 UAC、连着写几次注册表——
        /// 而且中途每一次都真的生效了。改成显式点「应用」，一次意图对应一次提权。
        /// </summary>
        private void OnFpsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            UpdateApplyFpsState();
        }

        private void UpdateApplyFpsState()
        {
            var item = CbFps.SelectedItem as FpsItem;
            if (item == null) { BtnApplyFps.IsEnabled = false; return; }

            // 比较**将要写入的注册表值**而不是帧率本身：
            // FpsToInterval 与 FrameIntervalToFps 不互逆（60 → 17ms → 读回 59），
            // 按帧率比会让"其实没变"显示成"可以应用"。
            int want = PerformanceSettings.FpsToInterval(item.Fps);
            int? have = PerformanceSettings.GetFrameInterval();
            BtnApplyFps.IsEnabled = want != (have.HasValue ? have.Value : 0);
        }

        private void OnApplyFps(object sender, RoutedEventArgs e)
        {
            var item = CbFps.SelectedItem as FpsItem;
            if (item == null) return;
            int fps = item.Fps;

            CbFps.IsEnabled = false;
            BtnApplyFps.IsEnabled = false;
            _app.RunSetupFps(fps, delegate(bool ok)
            {
                CbFps.IsEnabled = true;
                if (ok) Info(string.Format(L.T("帧率上限已设为 {0} FPS。{1}。"), fps, L.T(PerformanceSettings.FrameIntervalNote)));
                else Warn(L.T("设置帧率失败（可能未获得管理员授权）。"));
                RefreshAll();
            });
        }

        private void ApplyResolutionChoice(DesktopProfile p, int index)
        {
            var item = index >= 0 && index < CbResolution.Items.Count
                ? CbResolution.Items[index] as ModeItem : null;

            if (item == null || item.Mode == null)
            {
                p.ResolutionMode = ResolutionMode.FollowMonitor;
                return;
            }
            p.ResolutionMode = ResolutionMode.Custom;
            p.CustomWidth = item.Mode.Width;
            p.CustomHeight = item.Mode.Height;
        }

        private static int ScaleIndex(int pct)
        {
            for (int i = 0; i < ScaleValues.Length; i++) if (ScaleValues[i] == pct) return i;
            return 0;
        }
    }
}
