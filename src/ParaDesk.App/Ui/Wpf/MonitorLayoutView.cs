using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ParaDesk.Core;

namespace ParaDesk.Shell
{
    /// <summary>
    /// 按真实相对位置与比例绘制显示器，点击即指派分身桌面的位置。
    /// 自绘（OnRender）而非控件拼装：布局是任意坐标的矩形集合，
    /// 用面板容器反而更绕，而且这样能原生适配任意 DPI。
    /// </summary>
    internal class MonitorLayoutView : FrameworkElement
    {
        private List<MonitorInfo> _monitors = new List<MonitorInfo>();
        private readonly Dictionary<string, Rect> _hit = new Dictionary<string, Rect>();
        private string _selected;
        private string _hover;
        private string _active;

        public event EventHandler SelectionChanged;

        public MonitorLayoutView()
        {
            Focusable = false;
            SnapsToDevicePixels = true;
        }

        public string SelectedDevice
        {
            get { return _selected; }
            set { if (_selected != value) { _selected = value; InvalidateVisual(); } }
        }

        /// <summary>桌面当前实际所在的屏，画一个运行中的绿点。</summary>
        public string ActiveDevice
        {
            get { return _active; }
            set { if (_active != value) { _active = value; InvalidateVisual(); } }
        }

        public void SetMonitors(List<MonitorInfo> monitors)
        {
            _monitors = monitors ?? new List<MonitorInfo>();
            InvalidateVisual();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            string h = HitTest(e.GetPosition(this));
            if (h != _hover)
            {
                _hover = h;
                Cursor = h == null ? Cursors.Arrow : Cursors.Hand;
                InvalidateVisual();
            }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != null) { _hover = null; InvalidateVisual(); }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            string h = HitTest(e.GetPosition(this));
            if (h == null || h == _selected) return;
            _selected = h;
            InvalidateVisual();
            var ev = SelectionChanged;
            if (ev != null) ev(this, EventArgs.Empty);
        }

        private string HitTest(Point p)
        {
            foreach (var kv in _hit)
                if (kv.Value.Contains(p)) return kv.Key;
            return null;
        }

        private static Brush Res(string key, Color fallback)
        {
            try
            {
                var b = Application.Current != null ? Application.Current.TryFindResource(key) as Brush : null;
                if (b != null) return b;
            }
            catch { }
            return new SolidColorBrush(fallback);
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            _hit.Clear();

            double W = ActualWidth, H = ActualHeight;
            if (W <= 4 || H <= 4) return;

            Brush fg = Res("TextFillColorPrimaryBrush", Color.FromRgb(0x20, 0x20, 0x20));
            Brush fgDim = Res("TextFillColorSecondaryBrush", Color.FromRgb(0x70, 0x70, 0x70));
            Brush accent = Res("AccentFillColorDefaultBrush", Color.FromRgb(0x00, 0x78, 0xD4));
            Brush cardBg = Res("CardBackgroundFillColorDefaultBrush", Color.FromRgb(0xFA, 0xFA, 0xFA));
            Brush stroke = Res("CardStrokeColorDefaultBrush", Color.FromRgb(0xC8, 0xC8, 0xC8));

            if (_monitors.Count == 0)
            {
                DrawText(dc, L.T("未检测到显示器"), 13, fgDim, new Point(W / 2, H / 2), true);
                return;
            }

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var m in _monitors)
            {
                minX = Math.Min(minX, m.Bounds.Left);
                minY = Math.Min(minY, m.Bounds.Top);
                maxX = Math.Max(maxX, m.Bounds.Right);
                maxY = Math.Max(maxY, m.Bounds.Bottom);
            }
            double vw = Math.Max(1, maxX - minX), vh = Math.Max(1, maxY - minY);

            const double pad = 14, bottomRoom = 22;
            double availW = W - pad * 2, availH = H - pad * 2 - bottomRoom;
            double scale = Math.Min(availW / vw, availH / vh);
            double offX = pad + (availW - vw * scale) / 2.0;
            double offY = pad + (availH - vh * scale) / 2.0;

            foreach (var m in _monitors)
            {
                var r = new Rect(
                    offX + (m.Bounds.Left - minX) * scale,
                    offY + (m.Bounds.Top - minY) * scale,
                    Math.Max(40, m.Bounds.Width * scale - 6),
                    Math.Max(30, m.Bounds.Height * scale - 6));
                _hit[m.DeviceName] = r;

                bool sel = m.DeviceName == _selected;
                bool hov = m.DeviceName == _hover;
                bool act = m.DeviceName == _active;

                Brush fill = cardBg;
                if (sel) fill = new SolidColorBrush(Color.FromArgb(38,
                    ((SolidColorBrush)accent).Color.R, ((SolidColorBrush)accent).Color.G,
                    ((SolidColorBrush)accent).Color.B));
                else if (hov) fill = Res("SubtleFillColorSecondaryBrush", Color.FromRgb(0xF0, 0xF0, 0xF0));

                var pen = new Pen(sel ? accent : stroke, sel ? 2.0 : 1.0);
                dc.DrawRoundedRectangle(fill, pen, r, 6, 6);

                // 屏幕编号：一眼对应现实里的位置
                DrawText(dc, m.Index.ToString(CultureInfo.InvariantCulture),
                    Math.Max(16, Math.Min(34, r.Height / 3)), sel ? accent : fg,
                    new Point(r.Left + r.Width / 2, r.Top + r.Height / 2 - 8), true);

                // 起过名的屏用名字顶掉编号下方那行，没起过还是显示分辨率
                string custom = MonitorNaming.CustomName(m.DeviceName);
                if (!string.IsNullOrEmpty(custom))
                {
                    DrawText(dc, custom, 12, sel ? accent : fg,
                        new Point(r.Left + r.Width / 2, r.Bottom - 30), true);
                }

                string cap = m.Bounds.Width + "×" + m.Bounds.Height + (m.IsPrimary ? "  " + L.T("主屏") : "");
                DrawText(dc, cap, 11, fgDim,
                    new Point(r.Left + r.Width / 2, r.Bottom - 16), true);

                if (act)
                {
                    var dot = new Point(r.Right - 14, r.Top + 14);
                    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x10, 0x89, 0x3E)), null, dot, 5, 5);
                }
            }

            DrawText(dc, L.T("点击选择分身桌面显示的位置"), 11, fgDim, new Point(W / 2, H - 14), true);
        }

        private void DrawText(DrawingContext dc, string text, double size, Brush brush, Point at, bool center)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal,
                    size >= 16 ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
                size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

            var p = center ? new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2) : at;
            dc.DrawText(ft, p);
        }
    }
}
