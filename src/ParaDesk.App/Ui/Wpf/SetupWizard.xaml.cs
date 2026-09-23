using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ParaDesk.Core;
using ParaDesk.Elevated;

namespace ParaDesk.Shell
{
    internal partial class SetupWizard
    {
        private static readonly Brush OkBrush = FrozenBrush(Color.FromRgb(0x10, 0x89, 0x3E));
        private static readonly Brush PendingBrush = FrozenBrush(Color.FromRgb(0xB8, 0x86, 0x0B));
        private static readonly Brush BlockedBrush = FrozenBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));

        private static readonly string[][] HotkeyLines =
        {
            new[] { "toggleDesktop", "• {0} 显示 / 收起分身桌面" },
            new[] { "toggleViewOnly", "• {0} 切换“仅查看”（锁住你的键鼠，防止误触）" },
            new[] { "toggleRecording", "• {0} 开始 / 停止录制" },
            new[] { "togglePause", "• {0} 暂停 / 继续录制" },
            new[] { "screenshot", "• {0} 截图" },
            new[] { "detach", "• {0} 收起分身桌面（里面的程序继续运行）" },
            new[] { "pushClipboard", "• {0} 把剪贴板发送到分身桌面" },
            new[] { "pullClipboard", "• {0} 从分身桌面取回剪贴板" },
        };

        private readonly ParaDesk.Ui.AppContext _app;

        private readonly FrameworkElement[] _steps;
        private int _index;
        private bool _closed;

        private EnvironmentReport _env;
        private bool _checking;

        private string _pendingDevice;

        private bool _finishInitialized;
        private bool _startupInitial;
        private bool _viewOnlyInitial;

        /// <summary>用户是否完整走完（跳过也算完成，不再重复弹）。</summary>
        public bool Completed { get; private set; }

        public SetupWizard(ParaDesk.Ui.AppContext app)
        {
            _app = app;
            InitializeComponent();
            _steps = new FrameworkElement[] { Step1, Step2, Step3, Step4 };
            Closed += delegate { _closed = true; };
            Refresh();
        }

        private int LastIndex { get { return _steps.Length - 1; } }

        private FrameworkElement CurrentStep { get { return _steps[_index]; } }

        private void Refresh()
        {
            for (int i = 0; i < _steps.Length; i++)
                _steps[i].Visibility = i == _index ? Visibility.Visible : Visibility.Collapsed;
            StepScroller.ScrollToTop();

            // 用格式串而不是拼接：英文里语序是 "Step 2 of 4"，拆成前后缀翻不出来
            StepLabel.Text = string.Format(L.T("第 {0} / {1} 步"), _index + 1, _steps.Length);
            BtnBack.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnSkip.Visibility = _index < LastIndex ? Visibility.Visible : Visibility.Collapsed;
            BtnNext.Content = _index == LastIndex ? L.T("开始使用") : L.T("下一步");

            var step = CurrentStep;
            if (step == Step2) ShowChecklist();
            else if (step == Step3) BuildMonitorStep();
            else if (step == Step4) BuildFinishStep();

            Localizer.Translate(this);   // 放最后：上面几步会重新赋值文案
        }

        // ---------------- 第 2 步：环境检查 ----------------

        private void ShowChecklist()
        {
            if (_env != null) RenderChecklist(_env);
            else if (_checking) RenderChecking();
            else StartCheck();
        }

        private void StartCheck()
        {
            _checking = true;
            _env = null;
            RenderChecking();

            var dispatcher = Dispatcher;
            ThreadPool.QueueUserWorkItem(delegate
            {
                EnvironmentReport env = null;
                try
                {
                    SystemStatus.Invalidate();
                    env = SystemStatus.CheckCached();
                }
                catch (Exception ex)
                {
                    Log.Error("向导环境检查失败", ex);
                }

                try
                {
                    dispatcher.BeginInvoke(new Action(delegate { OnCheckDone(env); }));
                }
                catch (Exception ex) { Log.Debug("环境检查结果回到界面线程失败: " + ex.Message); }
            });
        }

        private void OnCheckDone(EnvironmentReport env)
        {
            _checking = false;
            if (_closed) return;
            _env = env;

            var step = CurrentStep;
            if (step == Step2) RenderChecklist(env);
            else if (step == Step4) UpdateStartNowAvailability();
        }

        private void OnRecheck(object sender, RoutedEventArgs e)
        {
            if (_checking) return;
            StartCheck();
        }

        private void RenderChecking()
        {
            CheckList.Children.Clear();
            CheckList.Children.Add(new TextBlock
            {
                Text = L.T("正在检查环境…"),
                Opacity = 0.7,
                Margin = new Thickness(0, 0, 0, 8),
            });
            CheckBar.Visibility = Visibility.Collapsed;
            SetupPlanCard.Visibility = Visibility.Collapsed;
            BtnRecheck.IsEnabled = false;
        }

        private void RenderChecklist(EnvironmentReport env)
        {
            CheckList.Children.Clear();
            BtnRecheck.IsEnabled = true;
            CheckBar.Visibility = Visibility.Visible;

            if (env == null)
            {
                CheckBar.Severity = global::Wpf.Ui.Controls.InfoBarSeverity.Error;
                CheckBar.Title = L.T("环境检查失败");
                CheckBar.Message = L.T("详见日志。");
                SetupPlanCard.Visibility = Visibility.Collapsed;
                return;
            }

            AddCheck(L.T("Windows 版本"), !env.IsHomeEdition, true,
                env.EditionId + " build " + env.BuildNumber,
                L.T("家庭版不支持子会话功能"));
            AddCheck(L.T("远程桌面客户端控件"), env.RdpControlRegistered, true, L.T("已就绪"), L.T("系统组件缺失，无法运行"));
            AddCheck(L.T("子会话功能"), env.ChildSessionsEnabled, false, L.T("已启用"), L.T("启动桌面时自动启用"));
            AddCheck(L.T("远程桌面监听器"), env.RdpListenerEnabled, false, L.T("已启用"), L.T("启动桌面时自动启用"));
            AddCheck(L.T("远程桌面服务"), env.TermServiceRunning, false, L.T("运行中"), L.T("启动桌面时自动启动"));
            AddCheck(L.T("子会话通道"), env.TransportReady, false, L.T("可用"),
                L.T("配置完成后需重启电脑一次才会就绪"));

            // 已经就绪的机器不必看"要改什么"——那张卡只在还需要配置时才有意义
            SetupPlanCard.Visibility = env.ReadyToStart || env.Blocked
                ? Visibility.Collapsed : Visibility.Visible;

            if (env.Blocked)
            {
                CheckBar.Severity = global::Wpf.Ui.Controls.InfoBarSeverity.Error;
                CheckBar.Title = L.T("这台电脑无法使用分身桌面");
                CheckBar.Message = env.IsHomeEdition
                    ? L.T("Windows 家庭版不含子会话功能，需要专业版及以上。分身桌面与沙盒桌面都用不了，录制和截图仍可正常使用。")
                    : (env.NextAction ?? L.T("环境尚未就绪。"));
            }
            else if (env.ReadyToStart)
            {
                CheckBar.Severity = global::Wpf.Ui.Controls.InfoBarSeverity.Success;
                CheckBar.Title = L.T("全部就绪");
                CheckBar.Message = L.T("无需额外配置，可以直接跳到选择显示位置。");
            }
            else
            {
                CheckBar.Severity = global::Wpf.Ui.Controls.InfoBarSeverity.Informational;
                CheckBar.Title = L.T("有几项需要配置");
                CheckBar.Message = L.T("不用在这里操作——第一次点「启动桌面」时会一次性配置好，只需一次管理员授权。");
            }
        }

        private void AddCheck(string name, bool ok, bool blocking, string okText, string failText)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new global::Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = ok ? global::Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24
                     : (blocking ? global::Wpf.Ui.Controls.SymbolRegular.ErrorCircle24
                                 : global::Wpf.Ui.Controls.SymbolRegular.Circle24),
                FontSize = 17,
                Foreground = ok ? OkBrush : (blocking ? BlockedBrush : PendingBrush),
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
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(detail, 2);
            row.Children.Add(detail);

            CheckList.Children.Add(row);
        }

        private static Brush FrozenBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // ---------------- 第 3 步：选屏 ----------------

        private void BuildMonitorStep()
        {
            List<MonitorInfo> mons;
            try { mons = MonitorService.Enumerate(); }
            catch (Exception ex)
            {
                Log.Error("向导枚举显示器失败", ex);
                mons = new List<MonitorInfo>();
            }

            string device = _pendingDevice;
            if (string.IsNullOrEmpty(device)) device = _app.Settings.GetActiveProfile().MonitorDevice;
            if (string.IsNullOrEmpty(device))
            {
                var def = MonitorService.DefaultTarget();
                if (def != null) device = def.DeviceName;
            }

            LayoutView.SetMonitors(mons);
            LayoutView.SelectedDevice = device;
            LayoutView.ActiveDevice = _app.ActiveMonitorDevice;
        }

        private void OnLayoutSelectionChanged(object sender, EventArgs e)
        {
            string device = LayoutView.SelectedDevice;
            if (string.IsNullOrEmpty(device)) return;
            _pendingDevice = device;
        }

        private void OnIdentify(object sender, RoutedEventArgs e)
        {
            IdentifyOverlay.Show(3);
        }

        private void BuildFinishStep()
        {
            HotkeyText.Text = DescribeHotkeys();

            if (!_finishInitialized)
            {
                _finishInitialized = true;
                _startupInitial = StartupRegistration.IsEnabled();
                _viewOnlyInitial = _app.Settings.GetActiveProfile().ViewOnly;
                SwRunAtStartup.IsChecked = _startupInitial;
                SwViewOnly.IsChecked = _viewOnlyInitial;
                SwStartNow.IsChecked = false;
            }
            UpdateStartNowAvailability();
        }

        private string DescribeHotkeys()
        {
            var sb = new StringBuilder();
            var bindings = _app.Settings.Hotkeys;
            foreach (var line in HotkeyLines)
            {
                var b = FindBinding(bindings, line[0]);
                if (b == null || !b.Enabled || b.Key == 0) continue;
                sb.Append(string.Format(L.T(line[1]), HotkeyService.Describe(b))).Append('\n');
            }
            if (sb.Length == 0) sb.Append(L.T("• 还没有启用任何全局热键，可在「热键」页设置")).Append('\n');
            sb.Append(L.T("• 关闭主窗口只是收进托盘，程序继续在后台运行"));
            return sb.ToString();
        }

        private static HotkeyBinding FindBinding(List<HotkeyBinding> bindings, string action)
        {
            if (bindings == null) return null;
            foreach (var b in bindings)
                if (b != null && string.Equals(b.Action, action, StringComparison.Ordinal)) return b;
            return null;
        }

        private void UpdateStartNowAvailability()
        {
            if (_app.DesktopAttached)
            {
                SwStartNow.IsChecked = false;
                SwStartNow.IsEnabled = false;
                LblStartNowHint.Text = L.T("分身桌面已经在运行。");
            }
            else if (_env != null && !_env.ReadyToStart)
            {
                SwStartNow.IsChecked = false;
                SwStartNow.IsEnabled = false;
                LblStartNowHint.Text = string.Format(L.T("暂时不能直接启动：{0}"),
                    _env.NextAction ?? L.T("环境尚未就绪。"));
            }
            else
            {
                SwStartNow.IsEnabled = true;
                LblStartNowHint.Text = L.T("关闭向导后马上在选定的屏幕上开出分身桌面");
            }
        }

        // ---------------- 导航 ----------------

        private void OnNext(object sender, RoutedEventArgs e)
        {
            if (_index >= LastIndex) { Finish(true); return; }
            _index++;
            Refresh();
        }

        private void OnBack(object sender, RoutedEventArgs e)
        {
            if (_index <= 0) return;
            _index--;
            Refresh();
        }

        private void OnSkip(object sender, RoutedEventArgs e) { Finish(false); }

        private void Finish(bool apply)
        {
            var s = _app.Settings;
            bool displayChanged = false;
            bool startNow = false;

            if (apply)
            {
                var p = s.GetActiveProfile();
                if (!string.IsNullOrEmpty(_pendingDevice) &&
                    !string.Equals(_pendingDevice, p.MonitorDevice, StringComparison.Ordinal))
                {
                    p.MonitorDevice = _pendingDevice;
                    displayChanged = true;
                    Log.Info("向导：分身桌面显示位置 => " + _pendingDevice);
                }

                if (_finishInitialized)
                {
                    bool startup = SwRunAtStartup.IsChecked == true;
                    if (startup != _startupInitial) ApplyStartup(s, startup);

                    bool viewOnly = SwViewOnly.IsChecked == true;
                    if (viewOnly != _viewOnlyInitial) _app.SetViewOnly(viewOnly);

                    startNow = SwStartNow.IsEnabled && SwStartNow.IsChecked == true;
                }
            }

            Completed = true;
            s.WizardShown = true;
            SettingsStore.Save(s);

            if (displayChanged) _app.ApplyDisplayChanges();

            Close();

            if (startNow)
            {
                var app = _app;
                Dispatcher.BeginInvoke(new Action(delegate { StartDesktopAfterWizard(app); }));
            }
        }

        private void ApplyStartup(AppSettings s, bool on)
        {
            if (on) s.StartMinimized = true;
            s.RunAtStartup = on;
            if (!StartupRegistration.SetEnabled(on, s.StartMinimized))
            {
                MessageBox.Show(this, L.T("设置开机自启失败，详见日志。"), AppInfo.ProductName,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static void StartDesktopAfterWizard(ParaDesk.Ui.AppContext app)
        {
            try
            {
                string err = app.StartDesktop();
                if (err != null)
                    MessageBox.Show(err, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                Log.Error("向导结束后启动分身桌面失败", ex);
                MessageBox.Show(L.T("启动分身桌面失败，详见日志。"), AppInfo.ProductName,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
