using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ParaDesk.Core;
using ParaDesk.Elevated;

namespace ParaDesk.Shell
{
    /// <summary>
    /// 首次运行向导。
    /// 新用户一上来面对七页设置无从下手，而这个产品又确实需要一次
    /// 无法回避的管理员配置——把它讲清楚、走一遍，比丢一个"首次配置"按钮好得多。
    /// </summary>
    internal partial class SetupWizard
    {
        private readonly ParaDesk.Ui.AppContext _app;
        private int _step = 1;
        private const int LastStep = 4;

        /// <summary>用户是否完整走完（跳过也算完成，不再重复弹）。</summary>
        public bool Completed { get; private set; }

        public SetupWizard(ParaDesk.Ui.AppContext app)
        {
            _app = app;
            InitializeComponent();
            Refresh();
        }

        private void Refresh()
        {
            Step1.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
            Step2.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
            Step3.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
            Step4.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;

            // 用格式串而不是拼接：英文里语序是 "Step 2 of 4"，拆成前后缀翻不出来
            StepLabel.Text = string.Format(L.T("第 {0} / {1} 步"), _step, LastStep);
            BtnBack.Visibility = _step > 1 ? Visibility.Visible : Visibility.Collapsed;
            BtnSkip.Visibility = _step < LastStep ? Visibility.Visible : Visibility.Collapsed;
            BtnNext.Content = _step == LastStep ? L.T("开始使用") : L.T("下一步");

            if (_step == 2) BuildChecklist();
            if (_step == 3) BuildMonitorStep();

            Localizer.Translate(this);   // 放最后：上面几步会重新赋值文案
        }

        // ---------------- 第 2 步：环境检查 ----------------

        private void BuildChecklist()
        {
            SystemStatus.Invalidate();      // 向导里要看的就是最新状态
            var env = SystemStatus.Check();
            CheckList.Children.Clear();

            AddCheck(L.T("Windows 版本"), !env.IsHomeEdition,
                env.EditionId + " build " + env.BuildNumber,
                L.T("家庭版不支持子会话功能"));
            AddCheck(L.T("远程桌面客户端控件"), env.RdpControlRegistered, L.T("已就绪"), L.T("系统组件缺失，无法运行"));
            AddCheck(L.T("子会话功能"), env.ChildSessionsEnabled, L.T("已启用"), L.T("启动桌面时自动启用"));
            AddCheck(L.T("远程桌面监听器"), env.RdpListenerEnabled, L.T("已启用"), L.T("启动桌面时自动启用"));
            AddCheck(L.T("远程桌面服务"), env.TermServiceRunning, L.T("运行中"), L.T("启动桌面时自动启动"));
            AddCheck(L.T("子会话通道"), env.TransportReady, L.T("可用"),
                L.T("配置完成后需重启电脑一次才会就绪"));

            // 已经就绪的机器不必看"要改什么"——那张卡只在还需要配置时才有意义
            SetupPlanCard.Visibility = env.ReadyToStart || env.Blocked
                ? Visibility.Collapsed : Visibility.Visible;

            if (env.Blocked)
            {
                CheckBar.Severity = global::Wpf.Ui.Controls.InfoBarSeverity.Error;
                CheckBar.Title = L.T("这台电脑无法使用分身桌面");
                CheckBar.Message = env.IsHomeEdition
                    // 别把沙盒也算进来：Windows 沙盒同样是专业版及以上才有，
                    // 家庭版真正还能用的只剩录制和截图
                    ? L.T("Windows 家庭版不含子会话功能，需要专业版及以上。分身桌面与沙盒桌面都用不了，录制和截图仍可正常使用。")
                    : L.T("系统缺少远程桌面客户端控件。");
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

        private void AddCheck(string name, bool ok, string okText, string failText)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new global::Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = ok ? global::Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24
                            : global::Wpf.Ui.Controls.SymbolRegular.Circle24,
                FontSize = 17,
                Foreground = new SolidColorBrush(ok
                    ? Color.FromRgb(0x10, 0x89, 0x3E)
                    : Color.FromRgb(0xB8, 0x86, 0x0B)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);

            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
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

        // ---------------- 第 3 步：选屏 ----------------

        private void BuildMonitorStep()
        {
            var mons = MonitorService.Enumerate();
            var p = _app.Settings.GetActiveProfile();

            string device = p.MonitorDevice;
            if (string.IsNullOrEmpty(device))
            {
                var def = MonitorService.DefaultTarget();
                if (def != null) device = def.DeviceName;
            }

            LayoutView.SetMonitors(mons);
            LayoutView.SelectedDevice = device;
        }

        private void OnLayoutSelectionChanged(object sender, EventArgs e)
        {
            string device = LayoutView.SelectedDevice;
            if (string.IsNullOrEmpty(device)) return;
            _app.Settings.GetActiveProfile().MonitorDevice = device;
            SettingsStore.Save(_app.Settings);
        }

        private void OnIdentify(object sender, RoutedEventArgs e)
        {
            IdentifyOverlay.Show(3);
        }

        // ---------------- 导航 ----------------

        private void OnNext(object sender, RoutedEventArgs e)
        {
            if (_step >= LastStep) { Finish(); return; }
            _step++;
            Refresh();
        }

        private void OnBack(object sender, RoutedEventArgs e)
        {
            if (_step <= 1) return;
            _step--;
            Refresh();
        }

        private void OnSkip(object sender, RoutedEventArgs e) { Finish(); }

        private void Finish()
        {
            Completed = true;
            _app.Settings.WizardShown = true;
            SettingsStore.Save(_app.Settings);
            Close();
        }
    }
}
