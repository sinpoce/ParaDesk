using System;
using System.Windows;
using System.Windows.Media;
using ParaDesk.Core;

namespace ParaDesk.Shell
{
    /// <summary>
    /// 在 WinForms 的消息循环里启用 WPF。
    ///
    /// 为什么是混合模式：托盘（NotifyIcon）、全局热键（NativeWindow）和承载 RDP
    /// 控件的窗口都是 WinForms 资产，消息循环必须留在 ApplicationContext 手里；
    /// 而管理界面要 Fluent 观感和原生 Per-Monitor DPI，用 WPF 最省事。
    /// 做法是构造一个不调用 Run() 的 WPF Application 实例——它只用来提供
    /// Application.Current 供资源查找，窗口靠 WinForms 的循环泵消息。
    /// </summary>
    internal static class WpfHost
    {
        private static bool _ready;

        public static bool Initialize()
        {
            if (_ready) return true;
            try
            {
                if (Application.Current == null)
                {
                    // 关键：ShutdownMode 必须设为显式，否则最后一个 WPF 窗口关闭时
                    // WPF 会尝试结束整个应用，而我们的宿主还要继续在托盘里活着。
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                }

                LoadThemeResources();
                _ready = true;
                Log.Info("WPF 宿主已就绪");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("初始化 WPF 宿主失败", ex);
                return false;
            }
        }

        private static void LoadThemeResources()
        {
            var res = Application.Current.Resources;

            // WPF-UI 需要两个字典，缺一不可：
            //   ThemesDictionary  —— 配色（深/浅），决定所有 *Brush 的实际颜色
            //   ControlsDictionary —— 控件样式模板
            // 之前只合并了打包好的 Wpf.Ui.xaml，控件样式生效但配色没生效，
            // 表现为"控件是 Fluent 的、颜色却是默认浅色"，且 ApplicationThemeManager.Apply
            // 找不到可替换的 ThemesDictionary 因而静默失效。
            bool light = SystemUsesLightTheme();
            try
            {
                res.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary
                {
                    Theme = light ? Wpf.Ui.Appearance.ApplicationTheme.Light
                                  : Wpf.Ui.Appearance.ApplicationTheme.Dark,
                });
                res.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                Log.Debug("已合并 WPF-UI 主题字典，light=" + light);
            }
            catch (Exception ex)
            {
                Log.Error("合并 WPF-UI 字典失败，回退到打包资源", ex);
                AddDictionary(res, "pack://application:,,,/Wpf.Ui;component/Resources/Wpf.Ui.xaml");
            }

            // 自定义样式必须最后合并，才能覆盖库的隐式样式并引用其主题画刷
            AddDictionary(res, "pack://application:,,,/ParaDesk;component/Ui/Wpf/NavStyles.xaml");

            ApplyTheme();
        }

        private static void AddDictionary(ResourceDictionary target, string uri)
        {
            try
            {
                target.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(uri, UriKind.Absolute) });
            }
            catch (Exception ex)
            {
                Log.Warn("加载资源字典失败 " + uri + ": " + ex.Message);
            }
        }

        /// <summary>跟随系统深浅色。</summary>
        public static void ApplyTheme()
        {
            try
            {
                // 命名空间刻意叫 ParaDesk.Shell 而非 ParaDesk.Ui.Wpf：
                // 后者会与库的 Wpf.Ui 命名空间相对解析冲突，且 XAML 生成的代码
                // 无法用 global:: 限定绕开。
                var theme = SystemUsesLightTheme()
                    ? Wpf.Ui.Appearance.ApplicationTheme.Light
                    : Wpf.Ui.Appearance.ApplicationTheme.Dark;
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                    theme, Wpf.Ui.Controls.WindowBackdropType.Mica, true);
                MakeFlyoutsOpaque(theme == Wpf.Ui.Appearance.ApplicationTheme.Light);
                Log.Debug("已应用主题: " + theme);
            }
            catch (Exception ex)
            {
                Log.Warn("应用 Fluent 主题失败（沿用默认外观）: " + ex.Message);
            }
        }

        /// <summary>
        /// 把下拉框/弹出菜单的背景改成不透明。
        /// Fluent 默认给这些浮层用亚克力（半透明）画刷，叠在正文之上时
        /// 会透出下方文字，选项读不清楚。菜单是要看清内容的，不该炫材质。
        /// 主题切换后需要重新调用（画刷会被主题字典重新覆盖）。
        /// </summary>
        private static void MakeFlyoutsOpaque(bool light)
        {
            var res = Application.Current != null ? Application.Current.Resources : null;
            if (res == null) return;

            Color solid = light ? Color.FromRgb(0xF9, 0xF9, 0xF9) : Color.FromRgb(0x2C, 0x2C, 0x2C);

            // 优先沿用主题自己的不透明底色，保证与实际渲染出来的配色一致。
            // 依次尝试几个候选键——不同版本的 WPF-UI 命名不完全一样。
            string[] candidates =
            {
                "SolidBackgroundFillColorSecondary",
                "SolidBackgroundFillColorTertiary",
                "SolidBackgroundFillColorBase",
                "ApplicationBackgroundColor",
            };
            foreach (string key in candidates)
            {
                object v = null;
                try { v = Application.Current.TryFindResource(key); }
                catch { }
                if (v is Color) { solid = (Color)v; break; }
                var b = v as SolidColorBrush;
                if (b != null) { solid = b.Color; break; }
            }

            // 键名不是猜的，是用 --dumpres 从运行时资源字典里 dump 出来的
            string[] flyoutKeys =
            {
                "ComboBoxDropDownBackgroundBrush",   // 下拉列表背景
                "MenuPopupBrush",                    // 弹出菜单背景（默认是渐变，含透明度）
                "MenuFlyoutPresenterBackground",
                "FlyoutBackgroundBrush",
                "AcrylicBackgroundFillColorDefaultBrush",
                "AcrylicBackgroundFillColorBaseBrush",
                "AcrylicInAppFillColorDefaultBrush",
                "AcrylicInAppFillColorBaseBrush",
            };

            var opaque = new SolidColorBrush(solid);
            opaque.Freeze();

            // 自有键：ComboBox 模板用它作弹层底色，完全由我们掌控，不受库的实现变化影响
            res["ParaDeskFlyoutBackground"] = opaque;

            int ok = 0;
            foreach (string key in flyoutKeys)
            {
                try
                {
                    res[key] = opaque;
                    // 回读确认覆盖真的生效——之前就是设了却没生效，白改一轮
                    var check = Application.Current.TryFindResource(key) as SolidColorBrush;
                    if (check != null && check.Color == solid) ok++;
                    else Log.Warn("浮层画刷未生效: " + key);
                }
                catch (Exception ex) { Log.Debug("覆盖浮层画刷 " + key + " 失败: " + ex.Message); }
            }
            Log.Debug("浮层画刷已置为不透明 " + ok + "/" + flyoutKeys.Length + "，色值 " + solid);
        }

        private static bool SystemUsesLightTheme()
        {
            try
            {
                object v = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);
                return v == null || Convert.ToInt32(v) != 0;
            }
            catch { return true; }
        }
    }
}
