using System;
using System.Windows.Forms;
using ParaDesk.Native;

namespace ParaDesk.Core
{
    /// <summary>
    /// 关机/注销拦截。子会话存在时 Windows 无法正常重启，必须先把它注销掉。
    /// 用隐藏消息窗口接管 WM_QUERYENDSESSION，并调用 ShutdownBlockReasonCreate
    /// 让用户在关机界面看到是谁在拖延，而不是无提示地卡住。
    /// </summary>
    internal class ShutdownGuard : IDisposable
    {
        private sealed class MessageWindow : NativeWindow
        {
            private readonly ShutdownGuard _owner;
            public MessageWindow(ShutdownGuard owner)
            {
                _owner = owner;
                CreateHandle(new CreateParams());
            }

            protected override void WndProc(ref Message m)
            {
                // 必须立刻答复 TRUE 放行，任何拖延都会被系统判为"无响应"。
                // 真正的清理放到 WM_ENDSESSION——那时系统才真的在等我们。
                if (m.Msg == NativeMethods.WM_QUERYENDSESSION)
                {
                    m.Result = new IntPtr(1);
                    return;
                }
                if (m.Msg == NativeMethods.WM_ENDSESSION)
                {
                    if (m.WParam != IntPtr.Zero) _owner.RunCleanup();
                    m.Result = IntPtr.Zero;
                    return;
                }
                base.WndProc(ref m);
            }
        }

        private MessageWindow _window;
        private bool _disposed;
        private bool _blockReasonSet;

        /// <summary>系统即将关机/注销，需在极短时间内完成清理。</summary>
        public event EventHandler Cleanup;

        public ShutdownGuard()
        {
            _window = new MessageWindow(this);
        }

        /// <summary>
        /// 桌面运行期间登记阻止原因。这不只是给用户看的说明——托盘隐藏的程序
        /// 被系统视作"无可见顶层窗口"，默认只给 5 秒就强杀；登记原因后可提升到 30 秒，
        /// 足够完成子会话注销。
        /// </summary>
        public void SetBlockReason(string reason)
        {
            if (_window == null) return;
            try
            {
                if (_blockReasonSet) NativeMethods.ShutdownBlockReasonDestroy(_window.Handle);
                if (!string.IsNullOrEmpty(reason))
                {
                    _blockReasonSet = NativeMethods.ShutdownBlockReasonCreate(_window.Handle, reason);
                }
                else
                {
                    _blockReasonSet = false;
                }
            }
            catch (Exception ex) { Log.Error("设置关机阻止原因失败", ex); }
        }

        private void RunCleanup()
        {
            Log.Info("收到 WM_QUERYENDSESSION，执行关机前清理");
            var h = Cleanup;
            if (h != null)
            {
                try { h(this, EventArgs.Empty); }
                catch (Exception ex) { Log.Error("关机前清理异常", ex); }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_window != null)
            {
                try
                {
                    if (_blockReasonSet) NativeMethods.ShutdownBlockReasonDestroy(_window.Handle);
                    _window.DestroyHandle();
                }
                catch { }
                _window = null;
            }
        }
    }
}
