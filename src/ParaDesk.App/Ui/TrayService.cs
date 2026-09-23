using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ParaDesk.Core;

namespace ParaDesk.Ui
{
    internal class TrayService : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string lpszFile, int nIconIndex,
            IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

        private enum Glyph { Idle = 0, Running = 1, Recording = 2, Attention = 3, Paused = 4 }

        private const int MaxTipLength = 63;

        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _miNotice;
        private readonly ToolStripMenuItem _miOpen;
        private readonly ToolStripMenuItem _miStart;
        private readonly ToolStripMenuItem _miDetach;
        private readonly ToolStripMenuItem _miClose;
        private readonly ToolStripMenuItem _miViewOnly;
        private readonly ToolStripMenuItem _miTopMost;
        private readonly ToolStripMenuItem _miRecord;
        private readonly ToolStripMenuItem _miPause;
        private readonly ToolStripMenuItem _miScreenshot;
        private readonly ToolStripMenuItem _miIdentify;
        private readonly ToolStripMenuItem _miExit;
        private readonly Font _boldFont;

        private readonly Icon[] _glyphs = new Icon[Enum.GetValues(typeof(Glyph)).Length];

        private bool _attached;
        private bool _sessionExists;
        private bool _recording;
        private bool _paused;
        private bool _attention;
        private bool _disposed;

        private bool _noticeBalloon;

        public event EventHandler OpenRequested;
        public event EventHandler StartRequested;
        public event EventHandler DetachRequested;
        public event EventHandler CloseDesktopRequested;
        public event EventHandler ViewOnlyToggled;
        public event EventHandler TopMostToggled;
        public event EventHandler RecordToggled;
        public event EventHandler IdentifyRequested;
        public event EventHandler ExitRequested;
        public event EventHandler ScreenshotRequested;
        public event EventHandler PauseToggled;
        public event EventHandler AttentionAcknowledged;
        public event EventHandler MenuOpening;

        public TrayService()
        {
            _menu = new ContextMenuStrip();

            _miNotice = new ToolStripMenuItem();
            _miNotice.Available = false;
            _miNotice.Click += delegate { Raise(OpenRequested); };

            _miOpen = new ToolStripMenuItem();
            _boldFont = new Font(_miOpen.Font, FontStyle.Bold);
            _miOpen.Font = _boldFont;
            _miOpen.Click += delegate { Raise(OpenRequested); };

            _miStart = new ToolStripMenuItem();
            _miStart.Click += delegate { Raise(StartRequested); };

            _miDetach = new ToolStripMenuItem();
            _miDetach.Click += delegate { Raise(DetachRequested); };

            _miClose = new ToolStripMenuItem();
            _miClose.Click += delegate { Raise(CloseDesktopRequested); };

            _miViewOnly = new ToolStripMenuItem();
            _miViewOnly.CheckOnClick = true;
            _miViewOnly.Click += delegate { Raise(ViewOnlyToggled); };

            _miTopMost = new ToolStripMenuItem();
            _miTopMost.CheckOnClick = true;
            _miTopMost.Click += delegate { Raise(TopMostToggled); };

            _miRecord = new ToolStripMenuItem();
            _miRecord.Click += delegate { Raise(RecordToggled); };

            _miPause = new ToolStripMenuItem();
            _miPause.Enabled = false;
            _miPause.Click += delegate { Raise(PauseToggled); };

            _miScreenshot = new ToolStripMenuItem();
            _miScreenshot.Click += delegate { Raise(ScreenshotRequested); };

            _miIdentify = new ToolStripMenuItem();
            _miIdentify.Click += delegate { Raise(IdentifyRequested); };

            _miExit = new ToolStripMenuItem();
            _miExit.Click += delegate { Raise(ExitRequested); };

            _menu.Items.AddRange(new ToolStripItem[]
            {
                _miNotice,
                _miOpen,
                new ToolStripSeparator(),
                _miStart, _miDetach, _miClose,
                new ToolStripSeparator(),
                _miViewOnly, _miTopMost,
                new ToolStripSeparator(),
                _miRecord, _miPause, _miScreenshot, _miIdentify,
                new ToolStripSeparator(),
                _miExit,
            });
            _menu.Opening += OnMenuOpening;

            ApplyTexts();
            BuildGlyphs();

            _icon = new NotifyIcon
            {
                Icon = GlyphIcon(Glyph.Idle),
                Text = TipText(),
                ContextMenuStrip = _menu,
                Visible = true,
            };
            _icon.MouseClick += delegate { AcknowledgeAttention(); };
            _icon.DoubleClick += delegate
            {
                AcknowledgeAttention();
                Raise(OpenRequested);
            };
            _icon.BalloonTipClicked += OnBalloonClicked;
        }

        private void OnBalloonClicked(object sender, EventArgs e)
        {
            bool notice = _noticeBalloon;
            _noticeBalloon = false;
            AcknowledgeAttention();
            if (notice) Raise(OpenRequested);
        }

        public void ApplyTexts()
        {
            try
            {
                _miNotice.Text = L.T("查看提醒(&N)");
                _miOpen.Text = L.T("打开主界面(&O)");
                _miDetach.Text = L.T("收起（后台保持运行）(&H)");
                _miClose.Text = L.T("关闭桌面(&C)");
                _miViewOnly.Text = L.T("仅查看（不响应我的键鼠）(&V)");
                _miTopMost.Text = L.T("窗口置顶(&T)");
                _miScreenshot.Text = L.T("截图(&P)");
                _miIdentify.Text = L.T("识别屏幕(&I)");
                _miExit.Text = L.T("退出(&X)");
                ApplyStateTexts();
                RefreshIcon();
            }
            catch (Exception ex) { Log.Debug("套用托盘文案失败: " + ex.Message); }
        }

        public void UpdateState(bool desktopAttached, bool sessionExists, bool canStart,
                                bool viewOnly, bool topMost, bool recording = false, bool paused = false)
        {
            if (_disposed) return;

            _attached = desktopAttached;
            _sessionExists = sessionExists;
            _recording = recording;
            _paused = recording && paused;
            ApplyStateTexts();

            _miStart.Enabled = canStart && !desktopAttached;
            _miDetach.Enabled = desktopAttached;
            _miClose.Enabled = desktopAttached || sessionExists;
            _miViewOnly.Enabled = desktopAttached;
            _miViewOnly.Checked = viewOnly;
            _miTopMost.Enabled = desktopAttached;
            _miTopMost.Checked = topMost;
            _miPause.Enabled = recording;
            _miScreenshot.Enabled = desktopAttached || HasAnyScreen();

            RefreshIcon();
        }

        public void SetAttention(bool on)
        {
            if (_disposed) return;
            if (on) _noticeBalloon = true;
            if (_attention == on) return;
            _attention = on;
            RefreshIcon();
        }

        public void Notify(string title, string body, ToolTipIcon icon)
        {
            if (_disposed) return;
            _noticeBalloon = false;
            try
            {
                if (string.IsNullOrEmpty(body)) body = title;
                if (string.IsNullOrEmpty(body)) return;
                _icon.BalloonTipTitle = title ?? "";
                _icon.BalloonTipText = body;
                _icon.BalloonTipIcon = icon;
                _icon.ShowBalloonTip(4000);
            }
            catch (Exception ex) { Log.Debug("显示托盘气泡失败: " + ex.Message); }
        }

        private void Raise(EventHandler h)
        {
            if (h != null) h(this, EventArgs.Empty);
        }

        private void OnMenuOpening(object sender, CancelEventArgs e)
        {
            Raise(MenuOpening);
            _miNotice.Available = _attention;
            AcknowledgeAttention();
        }

        private void AcknowledgeAttention()
        {
            if (_disposed || !_attention) return;
            Raise(AttentionAcknowledged);
        }

        private void ApplyStateTexts()
        {
            _miStart.Text = _sessionExists && !_attached ? L.T("重新接入桌面(&S)") : L.T("启动桌面(&S)");
            _miRecord.Text = _recording ? L.T("停止录制(&R)") : L.T("开始录制(&R)");
            _miPause.Text = _paused ? L.T("继续录制(&U)") : L.T("暂停录制(&U)");
        }

        private void RefreshIcon()
        {
            if (_disposed || _icon == null) return;

            Glyph g = _attention ? Glyph.Attention
                : _recording ? (_paused ? Glyph.Paused : Glyph.Recording)
                : (_attached || _sessionExists) ? Glyph.Running
                : Glyph.Idle;
            try
            {
                Icon ic = GlyphIcon(g);
                if (ic != null && !ReferenceEquals(_icon.Icon, ic)) _icon.Icon = ic;
                string tip = TipText();
                if (_icon.Text != tip) _icon.Text = tip;
            }
            catch (Exception ex) { Log.Debug("刷新托盘图标失败: " + ex.Message); }
        }

        private string TipText()
        {
            string t = ComposeTip(_attached ? L.T("运行中")
                : (_sessionExists ? L.T("已收起（后台运行）") : L.T("未启动")));
            if (t.Length > MaxTipLength && !_attached && _sessionExists) t = ComposeTip(L.T("后台"));
            if (t.Length > MaxTipLength) t = t.Substring(0, MaxTipLength - 1) + "…";
            return t;
        }

        private string ComposeTip(string desktopStatus)
        {
            string status = desktopStatus;
            if (_recording) status += " · " + (_paused ? L.T("录制已暂停") : L.T("录制中"));
            if (_attention) status = L.T("有新提醒") + " · " + status;
            return L.T(AppInfo.Title) + " — " + status;
        }

        private static bool HasAnyScreen()
        {
            try
            {
                var all = Screen.AllScreens;
                return all != null && all.Length > 0;
            }
            catch (Exception ex)
            {
                Log.Debug("枚举显示器失败: " + ex.Message);
                return true;
            }
        }

        private Icon GlyphIcon(Glyph g)
        {
            Icon ic = _glyphs[(int)g];
            if (ic == null && g == Glyph.Paused) ic = _glyphs[(int)Glyph.Recording];
            return ic ?? _glyphs[(int)Glyph.Idle];
        }

        private void BuildGlyphs()
        {
            Icon baseIcon = BuildIcon();
            _glyphs[(int)Glyph.Idle] = baseIcon;

            int size = SystemInformation.SmallIconSize.Width;
            if (size < 16) size = 16;
            if (size > 64) size = 64;
            _glyphs[(int)Glyph.Running] = BuildVariant(baseIcon, Glyph.Running, size);
            _glyphs[(int)Glyph.Recording] = BuildVariant(baseIcon, Glyph.Recording, size);
            _glyphs[(int)Glyph.Attention] = BuildVariant(baseIcon, Glyph.Attention, size);
            _glyphs[(int)Glyph.Paused] = BuildVariant(baseIcon, Glyph.Paused, size);
        }

        private static Icon BuildIcon()
        {
            try
            {
                var small = new IntPtr[1];
                ExtractIconEx(AppInfo.ExecutablePath, 0, null, small, 1);
                if (small[0] != IntPtr.Zero) return OwnedCopy(small[0]);
            }
            catch (Exception ex) { Log.Debug("读取程序小图标失败: " + ex.Message); }

            try
            {
                var own = Icon.ExtractAssociatedIcon(AppInfo.ExecutablePath);
                if (own != null) return own;
            }
            catch (Exception ex) { Log.Debug("读取程序图标失败，改用内置绘制: " + ex.Message); }

            try
            {
                using (var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        using (var back = new SolidBrush(Color.FromArgb(255, 60, 120, 216)))
                        using (var front = new SolidBrush(Color.FromArgb(255, 245, 247, 250)))
                        {
                            g.FillRectangle(back, 2, 5, 19, 15);
                            g.FillRectangle(front, 11, 12, 19, 15);
                        }
                    }
                    return IconFromBitmap(bmp);
                }
            }
            catch (Exception ex) { Log.Debug("绘制托盘图标失败，改用系统默认图标: " + ex.Message); }

            return (Icon)SystemIcons.Application.Clone();
        }

        private static Icon BuildVariant(Icon baseIcon, Glyph glyph, int size)
        {
            if (baseIcon == null) return null;
            try
            {
                using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.SmoothingMode = SmoothingMode.AntiAlias;

                        using (var src = baseIcon.ToBitmap())
                        using (var attrs = new ImageAttributes())
                        {
                            attrs.SetWrapMode(WrapMode.TileFlipXY);
                            g.DrawImage(src, new Rectangle(0, 0, size, size),
                                0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
                        }

                        switch (glyph)
                        {
                            case Glyph.Running:
                                DrawCornerDot(g, size, 0.40f, Color.FromArgb(255, 22, 198, 12));
                                break;
                            case Glyph.Recording:
                                DrawCornerDot(g, size, 0.46f, Color.FromArgb(255, 232, 17, 35));
                                break;
                            case Glyph.Attention:
                                DrawAttentionBadge(g, size);
                                break;
                            case Glyph.Paused:
                                DrawPauseBadge(g, size);
                                break;
                        }
                    }
                    return IconFromBitmap(bmp);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("生成托盘状态图标失败（" + glyph + "）: " + ex.Message);
                return null;
            }
        }

        private static void DrawCornerDot(Graphics g, int size, float ratio, Color color)
        {
            float d = Math.Max(5f, (float)Math.Round(size * ratio));
            float x = size - d;
            float y = size - d;
            CutOut(g, x, y, d, size);
            using (var b = new SolidBrush(color)) g.FillEllipse(b, x, y, d, d);
        }

        private static void DrawPauseBadge(Graphics g, int size)
        {
            float d = Math.Max(5f, (float)Math.Round(size * 0.46f));
            float x = size - d;
            float y = size - d;
            CutOut(g, x, y, d, size);
            using (var b = new SolidBrush(Color.FromArgb(255, 128, 128, 128))) g.FillEllipse(b, x, y, d, d);

            float w = Math.Max(1f, d * 0.18f);
            float h = d * 0.5f;
            float gap = w;
            float left = x + (d - (2 * w + gap)) / 2f;
            float top = y + (d - h) / 2f;
            using (var white = new SolidBrush(Color.White))
            {
                g.FillRectangle(white, left, top, w, h);
                g.FillRectangle(white, left + w + gap, top, w, h);
            }
        }

        private static void DrawAttentionBadge(Graphics g, int size)
        {
            float d = Math.Max(8f, (float)Math.Round(size * 0.62));
            float x = size - d;
            float y = 0f;
            CutOut(g, x, y, d, size);
            using (var b = new SolidBrush(Color.FromArgb(255, 247, 99, 12))) g.FillEllipse(b, x, y, d, d);

            float w = Math.Max(1.5f, d * 0.18f);
            float cx = x + d / 2f;
            using (var white = new SolidBrush(Color.White))
            {
                g.FillRectangle(white, cx - w / 2f, y + d * 0.18f, w, d * 0.40f);
                g.FillEllipse(white, cx - w / 2f, y + d * 0.66f, w, w);
            }
        }

        private static void CutOut(Graphics g, float x, float y, float d, int size)
        {
            float gap = Math.Max(1f, size / 16f);
            var oldMode = g.CompositingMode;
            g.CompositingMode = CompositingMode.SourceCopy;
            using (var clear = new SolidBrush(Color.Transparent))
                g.FillEllipse(clear, x - gap, y - gap, d + 2 * gap, d + 2 * gap);
            g.CompositingMode = oldMode;
        }

        private static Icon IconFromBitmap(Bitmap bmp)
        {
            return OwnedCopy(bmp.GetHicon());
        }

        private static Icon OwnedCopy(IntPtr hIcon)
        {
            try
            {
                using (var borrowed = Icon.FromHandle(hIcon))
                    return (Icon)borrowed.Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _icon.Visible = false;
                _icon.Dispose();
            }
            catch (Exception ex) { Log.Debug("释放托盘图标失败: " + ex.Message); }

            try { _menu.Dispose(); }
            catch (Exception ex) { Log.Debug("释放托盘菜单失败: " + ex.Message); }

            for (int i = 0; i < _glyphs.Length; i++)
            {
                if (_glyphs[i] == null) continue;
                try { _glyphs[i].Dispose(); }
                catch (Exception ex) { Log.Debug("释放托盘状态图标失败: " + ex.Message); }
                _glyphs[i] = null;
            }

            try { _boldFont.Dispose(); }
            catch (Exception ex) { Log.Debug("释放托盘菜单字体失败: " + ex.Message); }
        }
    }
}
