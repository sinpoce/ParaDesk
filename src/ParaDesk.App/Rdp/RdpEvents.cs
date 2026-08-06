using System;
using System.Runtime.InteropServices;
using ParaDesk.Core;

namespace ParaDesk.Rdp
{
    /// <summary>
    /// RDP ActiveX 的事件源接口（唯一一个）。dispinterface，共 32 个成员，
    /// 这里只声明用得到的：未声明的 DISPID 会返回 DISP_E_MEMBERNOTFOUND，
    /// mstscax 能容忍并继续派发后续事件（已实测）。
    /// 派发严格按 DISPID 数字，与声明顺序无关。
    /// </summary>
    [ComImport]
    [Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    internal interface IMsTscAxEvents
    {
        [DispId(1)] void OnConnecting();
        [DispId(2)] void OnConnected();
        [DispId(3)] void OnLoginComplete();
        [DispId(4)] void OnDisconnected(int discReason);
        [DispId(10)] void OnFatalError(int errorCode);
        [DispId(11)] void OnWarning(int warningCode);
        [DispId(12)] void OnRemoteDesktopSizeChange(int width, int height);
        [DispId(15)] bool OnConfirmClose();
        [DispId(33)] void OnAutoReconnected();
        [DispId(34)] void OnAutoReconnecting2(int disconnectReason, bool networkAvailable,
                                              int attemptCount, int maxAttemptCount);
    }

    /// <summary>事件回调载体。事件在 OCX 的单元线程（即 WinForms UI 线程）上触发，无需 marshal。</summary>
    internal class RdpEventSink : IMsTscAxEvents
    {
        public event EventHandler Connecting;
        public event EventHandler Connected;
        public event EventHandler LoginComplete;
        public event EventHandler<RdpDisconnectedEventArgs> Disconnected;
        public event EventHandler<RdpSizeChangedEventArgs> RemoteSizeChanged;
        public event EventHandler<RdpReconnectingEventArgs> Reconnecting;
        public event EventHandler Reconnected;

        /// <summary>返回 false 可阻止控件自行关闭；由宿主决定关闭时机。</summary>
        public Func<bool> ConfirmClose;

        public void OnConnecting() { Safe("OnConnecting", delegate { Fire(Connecting); }); }
        public void OnConnected() { Safe("OnConnected", delegate { Fire(Connected); }); }
        public void OnLoginComplete() { Safe("OnLoginComplete", delegate { Fire(LoginComplete); }); }

        public void OnDisconnected(int discReason)
        {
            Safe("OnDisconnected", delegate
            {
                var h = Disconnected;
                if (h != null) h(this, new RdpDisconnectedEventArgs(discReason));
            });
        }

        public void OnFatalError(int errorCode)
        {
            Safe("OnFatalError", delegate { Log.Error("RDP 致命错误 code=" + errorCode); });
        }

        public void OnWarning(int warningCode)
        {
            Safe("OnWarning", delegate { Log.Warn("RDP 警告 code=" + warningCode); });
        }

        public void OnRemoteDesktopSizeChange(int width, int height)
        {
            Safe("OnRemoteDesktopSizeChange", delegate
            {
                var h = RemoteSizeChanged;
                if (h != null) h(this, new RdpSizeChangedEventArgs(width, height));
            });
        }

        public bool OnConfirmClose()
        {
            var f = ConfirmClose;
            if (f == null) return true;
            try { return f(); }
            catch (Exception ex) { Log.Error("OnConfirmClose 异常", ex); return true; }
        }

        public void OnAutoReconnected() { Safe("OnAutoReconnected", delegate { Fire(Reconnected); }); }

        public void OnAutoReconnecting2(int disconnectReason, bool networkAvailable,
                                        int attemptCount, int maxAttemptCount)
        {
            Safe("OnAutoReconnecting2", delegate
            {
                var h = Reconnecting;
                if (h != null)
                    h(this, new RdpReconnectingEventArgs(disconnectReason, attemptCount, maxAttemptCount));
            });
        }

        private void Fire(EventHandler h)
        {
            if (h != null) h(this, EventArgs.Empty);
        }

        /// <summary>
        /// 处理器里抛出的异常会被 CCW 转成 DISP_E_EXCEPTION 并被 mstscax 静默吞掉，
        /// 所以必须自己捕获记录，否则故障无声无息。
        /// </summary>
        private static void Safe(string name, Action body)
        {
            try { body(); }
            catch (Exception ex) { Log.Error("RDP 事件处理异常: " + name, ex); }
        }
    }

    internal class RdpDisconnectedEventArgs : EventArgs
    {
        public int DiscReason { get; private set; }
        public RdpDisconnectedEventArgs(int r) { DiscReason = r; }
    }

    internal class RdpSizeChangedEventArgs : EventArgs
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public RdpSizeChangedEventArgs(int w, int h) { Width = w; Height = h; }
    }

    internal class RdpReconnectingEventArgs : EventArgs
    {
        public int DisconnectReason { get; private set; }
        public int AttemptCount { get; private set; }
        public int MaxAttemptCount { get; private set; }
        public RdpReconnectingEventArgs(int r, int a, int m)
        {
            DisconnectReason = r; AttemptCount = a; MaxAttemptCount = m;
        }
    }
}
