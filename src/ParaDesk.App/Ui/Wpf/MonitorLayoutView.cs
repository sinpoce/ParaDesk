using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ParaDesk.Core;

namespace ParaDesk.Shell
{
    internal class MonitorLayoutView : FrameworkElement
    {
        private sealed class Tile
        {
            public string Device;
            public System.Drawing.Rectangle Bounds;
            public string Number;
            public string CustomName;
            public string Caption;
        }

        private sealed class Palette
        {
            public Brush Fg, FgDim, Accent, CardBg, HoverFill, SelectedFill, ActiveDot;
            public Pen Stroke, SelectedStroke;
        }

        private List<Tile> _tiles = new List<Tile>();
        private readonly Dictionary<string, Rect> _hit = new Dictionary<string, Rect>();
        private string _selected;
        private string _hover;
        private string _active;

        private Palette _palette;
        private FontFamily _fontFamily;
        private Typeface _typeNormal, _typeBold;

        public event EventHandler SelectionChanged;

        public MonitorLayoutView()
        {
            Focusable = false;
            SnapsToDevicePixels = true;

            Loaded += delegate
            {
                WpfHost.ThemeChanged -= OnThemeChanged;
                WpfHost.ThemeChanged += OnThemeChanged;
                _palette = null;
                InvalidateVisual();
            };
            Unloaded += delegate { WpfHost.ThemeChanged -= OnThemeChanged; };
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
            var tiles = new List<Tile>();
            if (monitors != null)
            {
                foreach (var m in monitors)
                {
                    if (m == null) continue;
                    tiles.Add(new Tile
                    {
                        Device = m.DeviceName,
                        Bounds = m.Bounds,
                        Number = IdentifyOverlay.DisplayNumber(m).ToString(CultureInfo.InvariantCulture),
                        CustomName = MonitorNaming.CustomName(m.DeviceName),
                        Caption = m.Bounds.Width + "×" + m.Bounds.Height + (m.IsPrimary ? "  " + L.T("主屏") : ""),
                    });
                }
            }
            _tiles = tiles;
            InvalidateVisual();
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            _palette = null;
            InvalidateVisual();
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == TextElement.FontFamilyProperty)
            {
                _fontFamily = null;
                InvalidateVisual();
            }
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

        private Palette GetPalette()
        {
            if (_palette != null) return _palette;

            var p = new Palette();
            p.Fg = Res("TextFillColorPrimaryBrush", Color.FromRgb(0x20, 0x20, 0x20));
            p.FgDim = Res("TextFillColorSecondaryBrush", Color.FromRgb(0x70, 0x70, 0x70));
            p.Accent = Res("AccentFillColorDefaultBrush", Color.FromRgb(0x00, 0x78, 0xD4));
            p.CardBg = Res("CardBackgroundFillColorDefaultBrush", Color.FromRgb(0xFA, 0xFA, 0xFA));
            p.HoverFill = Res("SubtleFillColorSecondaryBrush", Color.FromRgb(0xF0, 0xF0, 0xF0));
            Brush stroke = Res("CardStrokeColorDefaultBrush", Color.FromRgb(0xC8, 0xC8, 0xC8));

            var accentSolid = p.Accent as SolidColorBrush;
            Color ac = accentSolid != null ? accentSolid.Color : Color.FromRgb(0x00, 0x78, 0xD4);
            p.SelectedFill = Frozen(new SolidColorBrush(Color.FromArgb(38, ac.R, ac.G, ac.B)));
            p.ActiveDot = Frozen(new SolidColorBrush(Color.FromRgb(0x10, 0x89, 0x3E)));

            p.Stroke = FrozenPen(stroke, 1.0);
            p.SelectedStroke = FrozenPen(p.Accent, 2.0);

            _palette = p;
            return p;
        }

        private static Brush Res(string key, Color fallback)
        {
            Brush b = null;
            try
            {
                b = Application.Current != null ? Application.Current.TryFindResource(key) as Brush : null;
            }
            catch (Exception ex) { Log.Debug("查找画刷资源 " + key + " 失败: " + ex.Message); }

            if (b == null) return Frozen(new SolidColorBrush(fallback));
            if (b.IsFrozen) return b;

            try
            {
                var copy = b.CloneCurrentValue();
                if (copy.CanFreeze) { copy.Freeze(); return copy; }
            }
            catch (Exception ex) { Log.Debug("复制画刷资源 " + key + " 失败: " + ex.Message); }

            var solid = b as SolidColorBrush;
            if (solid != null) return Frozen(new SolidColorBrush(solid.Color) { Opacity = solid.Opacity });
            return Frozen(new SolidColorBrush(fallback));
        }

        private static Brush Frozen(Brush b)
        {
            if (b.CanFreeze) b.Freeze();
            return b;
        }

        private static Pen FrozenPen(Brush brush, double thickness)
        {
            var pen = new Pen(brush, thickness);
            if (pen.CanFreeze) pen.Freeze();
            return pen;
        }

        private void EnsureTypefaces()
        {
            var family = TextElement.GetFontFamily(this);
            if (_fontFamily != null && ReferenceEquals(family, _fontFamily)) return;
            _fontFamily = family ?? SystemFonts.MessageFontFamily;
            _typeNormal = new Typeface(_fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            _typeBold = new Typeface(_fontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            _hit.Clear();

            double W = ActualWidth, H = ActualHeight;
            if (W <= 4 || H <= 4) return;

            var pal = GetPalette();
            EnsureTypefaces();

            if (_tiles.Count == 0)
            {
                DrawText(dc, L.T("未检测到显示器"), 13, pal.FgDim, new Point(W / 2, H / 2), true);
                return;
            }

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var m in _tiles)
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

            foreach (var m in _tiles)
            {
                var r = new Rect(
                    offX + (m.Bounds.Left - minX) * scale,
                    offY + (m.Bounds.Top - minY) * scale,
                    Math.Max(40, m.Bounds.Width * scale - 6),
                    Math.Max(30, m.Bounds.Height * scale - 6));
                _hit[m.Device] = r;

                bool sel = m.Device == _selected;
                bool hov = m.Device == _hover;
                bool act = m.Device == _active;

                Brush fill = sel ? pal.SelectedFill : (hov ? pal.HoverFill : pal.CardBg);
                dc.DrawRoundedRectangle(fill, sel ? pal.SelectedStroke : pal.Stroke, r, 6, 6);

                DrawText(dc, m.Number,
                    Math.Max(16, Math.Min(34, r.Height / 3)), sel ? pal.Accent : pal.Fg,
                    new Point(r.Left + r.Width / 2, r.Top + r.Height / 2 - 8), true);

                // 起过名的屏用名字顶掉编号下方那行，没起过还是显示分辨率
                if (!string.IsNullOrEmpty(m.CustomName))
                {
                    DrawText(dc, m.CustomName, 12, sel ? pal.Accent : pal.Fg,
                        new Point(r.Left + r.Width / 2, r.Bottom - 30), true);
                }

                DrawText(dc, m.Caption, 11, pal.FgDim,
                    new Point(r.Left + r.Width / 2, r.Bottom - 16), true);

                if (act)
                {
                    var dot = new Point(r.Right - 14, r.Top + 14);
                    dc.DrawEllipse(pal.ActiveDot, null, dot, 5, 5);
                }
            }

            DrawText(dc, L.T("点击选择分身桌面显示的位置"), 11, pal.FgDim, new Point(W / 2, H - 14), true);
        }

        private void DrawText(DrawingContext dc, string text, double size, Brush brush, Point at, bool center)
        {
            if (string.IsNullOrEmpty(text)) return;
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                size >= 16 ? _typeBold : _typeNormal,
                size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

            var p = center ? new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2) : at;
            dc.DrawText(ft, p);
        }
    }
}
