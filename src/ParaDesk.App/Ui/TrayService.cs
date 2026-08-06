using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using ParaDesk.Core;

namespace ParaDesk.Ui
{
    /// <summary>托盘图标与右键菜单。菜单项状态随会话状态实时刷新。</summary>
    internal class TrayService : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _miStart;
        private readonly ToolStripMenuItem _miDetach;
        private readonly ToolStripMenuItem _miClose;
        private readonly ToolStripMenuItem _miViewOnly;
        private readonly ToolStripMenuItem _miTopMost;
        private readonly ToolStripMenuItem _miRecord;
        private readonly ToolStripMenuItem _miOpen;
        private readonly ToolStripMenuItem _miIdentify;
        private readonly ToolStripMenuItem _miExit;
        private Icon _generated;
        private bool _disposed;

        public event EventHandler OpenRequested;
        public event EventHandler StartRequested;
        public event EventHandler DetachRequested;
        public event EventHandler CloseDesktopRequested;
        public event EventHandler ViewOnlyToggled;
        public event EventHandler TopMostToggled;
        public event EventHandler RecordToggled;
        public event EventHandler IdentifyRequested;
        public event EventHandler ExitRequested;

        public TrayService()
        {
            _menu = new ContextMenuStrip();

            _miOpen = new ToolStripMenuItem(L.T("打开主界面(&O)"));
            var miOpen = _miOpen;
            miOpen.Font = new Font(miOpen.Font, FontStyle.Bold);
            miOpen.Click += delegate { Raise(OpenRequested); };

            _miStart = new ToolStripMenuItem(L.T("启动桌面(&S)"));
            _miStart.Click += delegate { Raise(StartRequested); };

            _miDetach = new ToolStripMenuItem(L.T("收起（后台保持运行）(&H)"));
            _miDetach.Click += delegate { Raise(DetachRequested); };

            _miClose = new ToolStripMenuItem(L.T("关闭桌面(&C)"));
            _miClose.Click += delegate { Raise(CloseDesktopRequested); };

            _miViewOnly = new ToolStripMenuItem(L.T("仅查看（不响应我的键鼠）(&V)"));
            _miViewOnly.CheckOnClick = true;
            _miViewOnly.Click += delegate { Raise(ViewOnlyToggled); };

            _miTopMost = new ToolStripMenuItem(L.T("窗口置顶(&T)"));
            _miTopMost.CheckOnClick = true;
            _miTopMost.Click += delegate { Raise(TopMostToggled); };

            _miRecord = new ToolStripMenuItem(L.T("开始录制(&R)"));
            _miRecord.Click += delegate { Raise(RecordToggled); };

            _miIdentify = new ToolStripMenuItem(L.T("识别屏幕(&I)"));
            var miIdentify = _miIdentify;
            miIdentify.Click += delegate { Raise(IdentifyRequested); };

            _miExit = new ToolStripMenuItem(L.T("退出(&X)"));
            var miExit = _miExit;
            miExit.Click += delegate { Raise(ExitRequested); };

            _menu.Items.AddRange(new ToolStripItem[]
            {
                miOpen,
                new ToolStripSeparator(),
                _miStart, _miDetach, _miClose,
                new ToolStripSeparator(),
                _miViewOnly, _miTopMost,
                new ToolStripSeparator(),
                _miRecord, miIdentify,
                new ToolStripSeparator(),
                miExit,
            });

            _generated = BuildIcon();
            _icon = new NotifyIcon
            {
                Icon = _generated,
                Text = AppInfo.Title,
                ContextMenuStrip = _menu,
                Visible = true,
            };
            _icon.DoubleClick += delegate { Raise(OpenRequested); };
        }

        /// <summary>
        /// 重新套用所有菜单文案。语言切换后必须调用，
        /// 否则只有 UpdateState 里那两项会跟着变，菜单会中英混杂。
        /// </summary>
        public void ApplyTexts()
        {
            try
            {
                _miOpen.Text = L.T("打开主界面(&O)");
                _miDetach.Text = L.T("收起（后台保持运行）(&H)");
                _miClose.Text = L.T("关闭桌面(&C)");
                _miViewOnly.Text = L.T("仅查看（不响应我的键鼠）(&V)");
                _miTopMost.Text = L.T("窗口置顶(&T)");
                _miIdentify.Text = L.T("识别屏幕(&I)");
                _miExit.Text = L.T("退出(&X)");
            }
            catch (Exception ex) { Log.Debug("套用托盘文案失败: " + ex.Message); }
        }

        /// <summary>按当前状态启用/禁用菜单项。</summary>
        public void UpdateState(bool desktopAttached, bool sessionExists, bool canStart,
                                bool viewOnly, bool topMost, bool recording = false)
        {
            ApplyTexts();
            _miRecord.Text = recording ? L.T("停止录制(&R)") : L.T("开始录制(&R)");
            _miStart.Enabled = canStart && !desktopAttached;
            _miStart.Text = sessionExists && !desktopAttached ? L.T("重新接入桌面(&S)") : L.T("启动桌面(&S)");
            _miDetach.Enabled = desktopAttached;
            _miClose.Enabled = desktopAttached || sessionExists;
            _miViewOnly.Enabled = desktopAttached;
            _miViewOnly.Checked = viewOnly;
            _miTopMost.Enabled = desktopAttached;
            _miTopMost.Checked = topMost;

            string status = recording ? L.T("录制中")
                : desktopAttached ? L.T("运行中")
                : (sessionExists ? L.T("已收起（后台运行）") : L.T("未启动"));
            // NotifyIcon.Text 上限 63 字符
            string t = AppInfo.Title + " — " + status;
            _icon.Text = t.Length > 62 ? t.Substring(0, 62) : t;
        }

        public void Notify(string title, string body, ToolTipIcon icon)
        {
            try
            {
                _icon.BalloonTipTitle = title;
                _icon.BalloonTipText = body;
                _icon.BalloonTipIcon = icon;
                _icon.ShowBalloonTip(4000);
            }
            catch { }
        }

        private static void Raise(EventHandler h)
        {
            if (h != null) h(null, EventArgs.Empty);
        }

        /// <summary>优先用程序自带图标，取不到再程序化生成一个（两块屏的意象）。</summary>
        private static Icon BuildIcon()
        {
            try
            {
                // 与窗口、任务栏保持同一个图标
                var own = Icon.ExtractAssociatedIcon(AppInfo.ExecutablePath);
                if (own != null) return own;
            }
            catch (Exception ex) { Log.Debug("读取程序图标失败，改用内置绘制: " + ex.Message); }

            try
            {
                using (var bmp = new Bitmap(32, 32))
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
                    return Icon.FromHandle(bmp.GetHicon());
                }
            }
            catch
            {
                return SystemIcons.Application;
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
                _menu.Dispose();
                if (_generated != null) _generated.Dispose();
            }
            catch { }
        }
    }
}
