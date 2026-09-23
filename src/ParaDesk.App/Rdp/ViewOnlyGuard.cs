using System;
using System.Drawing;
using System.Windows.Forms;
using ParaDesk.Core;
using ParaDesk.Native;

namespace ParaDesk.Rdp
{
    /// <summary>
    /// 「仅查看」：挡住人的物理键鼠，但画面继续刷新，程序注入的输入（AI agent）不受影响。
    ///
    /// 两道防线：
    /// 1) 分层覆盖窗口——独立顶层窗口，WS_EX_LAYERED + alpha=1（几乎全透明但仍参与命中测试）。
    ///    关键：alpha 必须 ≥ 1 且**不能**加 WS_EX_TRANSPARENT，否则鼠标消息会被放行到下层。
    ///    用独立顶层窗口而非同级 Panel：WinForms 控件带 WS_CLIPSIBLINGS，Panel 会把
    ///    OCX 的绘制裁成一块实心矩形。
    /// 2) EnableWindow(axHwnd, FALSE)——禁用窗口后子窗口隐式禁用，鼠标消息被忽略、
    ///    无法取得键盘焦点，而绘制不受影响。
    /// </summary>
    internal class ViewOnlyGuard : IDisposable
    {
        /// <summary>覆盖窗口本体。不激活、不进任务栏。</summary>
        private sealed class OverlayWindow : Form
        {
            public OverlayWindow()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                BackColor = Color.Black;
                // 不设 TopMost：这是一层几乎全透明的窗口，若置顶就会浮在
                // 那块屏幕上的所有程序之上，把用户对其他窗口的点击也一并吃掉。
                // 它只需盖住 RDP 画面，靠 Show(owner) 的属主关系跟随即可。
                TopMost = false;
                Cursor = Cursors.No;
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= NativeMethods.WS_EX_LAYERED
                                | NativeMethods.WS_EX_TOOLWINDOW
                                | NativeMethods.WS_EX_NOACTIVATE;
                    // 绝不能加 WS_EX_TRANSPARENT——那会让鼠标穿透，覆盖层就失去意义
                    return cp;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                // alpha=1：肉眼看不出遮挡，但命中测试仍然算它挡住了
                NativeMethods.SetLayeredWindowAttributes(Handle, 0, 1, NativeMethods.LWA_ALPHA);
            }

            // 吞掉一切键鼠，不向下传递
            protected override void OnMouseDown(MouseEventArgs e) { }
            protected override void OnKeyDown(KeyEventArgs e) { }
        }

        private readonly Form _owner;
        private readonly Control _rdpControl;
        private OverlayWindow _overlay;
        private bool _active;
        private bool _disposed;
        private bool _repositionFailLogged;

        public bool IsActive { get { return _active; } }

        public ViewOnlyGuard(Form owner, Control rdpControl)
        {
            _owner = owner;
            _rdpControl = rdpControl;
        }

        public void SetActive(bool active)
        {
            if (_disposed || _active == active) return;
            _active = active;

            try
            {
                if (active) Enable();
                else Disable();
                Log.Info("仅查看模式 => " + (active ? "开" : "关"));
            }
            catch (Exception ex)
            {
                Log.Error("切换仅查看模式失败", ex);
            }
        }

        public void Reposition()
        {
            if (!_active || _overlay == null || _owner == null) return;
            try
            {
                Rectangle r = _owner.RectangleToScreen(_owner.ClientRectangle);
                _overlay.Bounds = r;
                _repositionFailLogged = false;
            }
            catch (Exception ex)
            {
                if (!_repositionFailLogged)
                {
                    _repositionFailLogged = true;
                    Log.Warn("仅查看覆盖层贴合失败，部分画面可能没有被挡住: " + ex.Message);
                }
            }
        }

        private void Enable()
        {
            if (_rdpControl != null && _rdpControl.IsHandleCreated)
                NativeMethods.EnableWindow(_rdpControl.Handle, false);

            if (_overlay == null) _overlay = new OverlayWindow();
            Reposition();
            if (!_overlay.Visible) _overlay.Show(_owner);
            Reposition();
        }

        private void Disable()
        {
            if (_overlay != null)
            {
                _overlay.Hide();
            }
            // 句柄重建前必须恢复，否则 WinForms 与 Win32 的启用状态会长期不一致
            if (_rdpControl != null && _rdpControl.IsHandleCreated)
                NativeMethods.EnableWindow(_rdpControl.Handle, true);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_active) Disable();
                if (_overlay != null) { _overlay.Dispose(); _overlay = null; }
            }
            catch (Exception ex)
            {
                Log.Debug("释放仅查看覆盖层失败: " + ex.Message);
            }
        }
    }
}
