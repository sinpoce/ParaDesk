using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ParaDesk.Core;
using ParaDesk.Native;

namespace ParaDesk.Shell
{
    /// <summary>
    /// 屏幕识别：在每块显示器中央浮现大号编号，几秒后自动淡出。
    /// 与 Windows 设置里的「识别」是同一个交互习惯——用户对"哪块是显示器 2"
    /// 只有空间记忆，编号必须直接出现在那块屏幕上才有意义。
    /// </summary>
    internal static class IdentifyOverlay
    {
        private static readonly List<Window> Active = new List<Window>();
        private static DispatcherTimer _timer;

        /// <summary>在所有显示器上显示编号。重复调用会重置计时。</summary>
        public static void Show(int seconds = 3)
        {
            try
            {
                if (!WpfHost.Initialize()) return;
                Hide();

                foreach (var m in MonitorService.Enumerate())
                    Active.Add(CreateOverlay(m));

                if (_timer == null)
                {
                    _timer = new DispatcherTimer();
                    _timer.Tick += delegate { FadeOutAndHide(); };
                }
                _timer.Interval = TimeSpan.FromSeconds(Math.Max(1, seconds));
                _timer.Stop();
                _timer.Start();

                Log.Debug("已显示屏幕识别编号，共 " + Active.Count + " 块");
            }
            catch (Exception ex)
            {
                Log.Error("显示屏幕识别失败", ex);
                Hide();
            }
        }

        private static Window CreateOverlay(MonitorInfo m)
        {
            var text = new TextBlock
            {
                Text = m.Index.ToString(),
                FontSize = 132,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // 起过名的屏把名字也打出来——识别屏幕本来就是为了分清哪块是哪块
            string custom = MonitorNaming.CustomName(m.DeviceName);
            var caption = new TextBlock
            {
                Text = (string.IsNullOrEmpty(custom) ? "" : custom + "　")
                       + m.Bounds.Width + " × " + m.Bounds.Height
                       + (m.IsPrimary ? "　" + L.T("主屏") : ""),
                FontSize = 19,
                Foreground = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6),
            };

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(text);
            stack.Children.Add(caption);

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(224, 24, 24, 27)),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(56, 28, 56, 24),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = stack,
            };

            var win = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowActivated = false,
                Content = card,
                Opacity = 0,
            };

            // WPF 的 Left/Top 是设备无关单位，而显示器边界是物理像素。
            // 先按 96 DPI 摆好，再用 Win32 精确定位，避开混合 DPI 下的换算误差。
            win.Left = 0; win.Top = 0; win.Width = 100; win.Height = 100;
            win.Show();

            var hwnd = new WindowInteropHelper(win).Handle;
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                m.Bounds.X, m.Bounds.Y, m.Bounds.Width, m.Bounds.Height,
                NativeMethods.SWP_NOACTIVATE);

            win.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            return win;
        }

        private static void FadeOutAndHide()
        {
            if (_timer != null) _timer.Stop();
            if (Active.Count == 0) return;

            var fading = new List<Window>(Active);
            Active.Clear();

            foreach (var w in fading)
            {
                var win = w;
                var anim = new DoubleAnimation(win.Opacity, 0, TimeSpan.FromMilliseconds(220));
                anim.Completed += delegate
                {
                    try { win.Close(); } catch { }
                };
                win.BeginAnimation(UIElement.OpacityProperty, anim);
            }
        }

        /// <summary>立即收起（不做动画）。</summary>
        public static void Hide()
        {
            if (_timer != null) _timer.Stop();
            foreach (var w in Active)
            {
                try { w.Close(); } catch { }
            }
            Active.Clear();
        }
    }
}
