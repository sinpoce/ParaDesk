using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ParaDesk.Core;

namespace ParaDesk.Rdp
{
    /// <summary>
    /// IMsRdpExtendedSettings —— 属性包接口，ConnectToChildSession 等扩展设置的入口。
    /// 注意：必须从 NotSafeForScripting 版控件 QueryInterface，脚本安全版会屏蔽它。
    /// </summary>
    [ComImport]
    [Guid("302D8188-0052-4807-806A-362B628F9AC5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMsRdpExtendedSettings
    {
        void set_Property([In, MarshalAs(UnmanagedType.BStr)] string propertyName,
                          [In, MarshalAs(UnmanagedType.Struct)] ref object value);

        [return: MarshalAs(UnmanagedType.Struct)]
        object get_Property([In, MarshalAs(UnmanagedType.BStr)] string propertyName);
    }

    /// <summary>
    /// RDP ActiveX 宿主。CLSID = MsRdpClient10NotSafeForScripting。
    /// 除 IMsRdpExtendedSettings 外（它派生自 IUnknown，不是 dual，dynamic 会抛
    /// RuntimeBinderException），其余一律走 IDispatch 后期绑定，避免手写庞大易错的虚表。
    /// </summary>
    internal class RdpHostControl : AxHost
    {
        public const string Clsid = "a0c63c30-f08d-4ab4-907c-34905d770c7d";

        private AxHost.ConnectionPointCookie _cookie;

        /// <summary>事件接收器。必须在控件创建前赋值，CreateSink 时会用到。</summary>
        public RdpEventSink Sink { get; set; }

        public RdpHostControl() : base(Clsid) { }

        public object Ocx { get { return GetOcx(); } }

        /// <summary>
        /// AxHost 在 AttachInterfaces 之后、控件可用之前调用此方法，是订阅事件的正确时机。
        /// ConnectionPointCookie 内部完成 FindConnectionPoint + Advise。
        /// </summary>
        protected override void CreateSink()
        {
            base.CreateSink();
            if (Sink == null) return;
            try
            {
                _cookie = new AxHost.ConnectionPointCookie(GetOcx(), Sink, typeof(IMsTscAxEvents));
                Core.Log.Debug("RDP 事件已订阅");
            }
            catch (Exception ex)
            {
                Core.Log.Error("订阅 RDP 事件失败（将退化为轮询）", ex);
                _cookie = null;
            }
        }

        protected override void DetachSink()
        {
            try
            {
                if (_cookie != null) { _cookie.Disconnect(); _cookie = null; }
            }
            catch (Exception ex) { Core.Log.Error("解除 RDP 事件订阅失败", ex); }
            base.DetachSink();
        }
    }

    /// <summary>
    /// IMsRdpClipboard —— 手动剪贴板同步。
    /// 与 IMsRdpExtendedSettings 一样派生自 IUnknown（非 dual），必须 ComImport。
    /// </summary>
    [ComImport]
    [Guid("2E769EE8-00C7-43DC-AFD9-235D75B72A40")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMsRdpClipboard
    {
        void SendClipboardToServer();
        void SendClipboardToClient();
    }

    /// <summary>连接状态。对应 IMsTscAx.Connected 的 0/1/2。</summary>
    internal enum RdpConnectionState
    {
        Disconnected = 0,
        Connected = 1,
        Connecting = 2,
        Unknown = -1,
    }

    /// <summary>断开原因的人话化。</summary>
    internal static class RdpDisconnectReason
    {
        /// <summary>把 discReason / extendedReason 翻译成用户能据以行动的说明。</summary>
        public static string Describe(int discReason, int extendedReason)
        {
            switch (extendedReason)
            {
                case 5:
                    return L.T("分身桌面被另一个连接接管了。系统同一时间只允许一个子会话。");
                case 2:
                case 12:
                    return L.T("分身桌面已注销。");
            }

            switch (discReason)
            {
                case 0x1: return L.T("本地主动断开。");
                case 0x2: return L.T("已在分身桌面内注销。");
                case 0x3: return L.T("会话被系统结束。");
                case 0x108: return L.T("连接超时。");
                case 0x204:
                    return L.T("无法建立连接：远程桌面监听器未启用。请重启电脑后再试。");
                case 0x406: return L.T("安全数据无效。");
                case 0x706:
                    return L.T("本地回环的证书校验失败，子会话无法建立。");
                case 0x807: return L.T("登录失败：账户凭据被拒绝。");
                case 0x904: return L.T("连接被关闭。");
                case 0x1607:
                case 0x1707:
                    return L.T("凭据委派被组策略禁止，无法自动登录分身桌面。");
            }
            return null;
        }

        /// <summary>
        /// 连接从未建立成功时的通用排查提示。
        /// 用属性而不是 const：const 的初始化在编译期完成，没法过 L.T；
        /// 而且语言可以在运行中切换，每次取用时再拼才是对的。
        /// </summary>
        public static string FirstRunHints
        {
            get
            {
                return L.T("未能连接到分身桌面。常见原因：") + "\r\n" +
                       L.T("1. 系统配置尚未完成，或配置后尚未重启电脑；") + "\r\n" +
                       L.T("2. 账户无密码或仅用 Windows Hello —— 请为账户设置密码；") + "\r\n" +
                       L.T("3. 企业策略禁用了凭据委派；") + "\r\n" +
                       L.T("4. 「手机连接」等组件与子会话冲突。");
            }
        }
    }
}
