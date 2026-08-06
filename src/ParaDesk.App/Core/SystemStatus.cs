using System;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;
using ParaDesk.Native;

namespace ParaDesk.Core
{
    /// <summary>一次性环境体检结果，供首次配置向导与主界面状态区使用。</summary>
    internal class EnvironmentReport
    {
        public string EditionId;
        public string BuildNumber;
        public bool IsHomeEdition;
        public bool ChildSessionsEnabled;
        public bool TermServiceRunning;
        public bool RdpListenerEnabled;
        public bool RdpControlRegistered;
        public bool InsideChildSession;
        public uint ChildSessionId;
        public int TransportStatus;
        public string TransportPipe;

        public bool TransportReady { get { return TransportStatus == 0; } }

        /// <summary>全部就绪即可直接启动桌面。</summary>
        public bool ReadyToStart
        {
            get
            {
                return !IsHomeEdition && ChildSessionsEnabled && TermServiceRunning
                       && RdpControlRegistered && !InsideChildSession && TransportReady;
            }
        }

        /// <summary>
        /// 这台机器根本跑不了，配置也救不回来（版本不支持、缺系统组件、人在分身桌面里）。
        /// 与 ReadyToStart 之间还夹着"能配好、只是还没配"这一档，
        /// 两者不能混为一谈——后者不该把「启动桌面」按钮变灰。
        /// </summary>
        public bool Blocked
        {
            get { return IsHomeEdition || !RdpControlRegistered || InsideChildSession; }
        }

        /// <summary>只差一次管理员授权就能就绪。</summary>
        public bool NeedsSetup
        {
            get { return !Blocked && !ReadyToStart; }
        }

        /// <summary>给用户看的下一步建议；null 表示无需操作。</summary>
        public string NextAction
        {
            get
            {
                if (IsHomeEdition) return L.T("Windows 家庭版不支持子会话，请使用专业版及以上，或改用虚拟机桌面。");
                if (InsideChildSession) return L.T("请在主桌面上运行本程序（当前似乎在分身桌面内）。");
                if (!RdpControlRegistered) return L.T("系统缺少远程桌面客户端控件，无法运行。");
                if (!ChildSessionsEnabled || !RdpListenerEnabled || !TermServiceRunning)
                    return L.T("点「启动桌面」即可自动完成配置（需要一次管理员授权）。");
                if (!TransportReady)
                    return L.T("配置已写入，但子会话监听器需重启电脑后才会启动。请重启电脑。");
                return null;
            }
        }
    }

    internal static class SystemStatus
    {
        public static bool ChildSessionsEnabled()
        {
            bool e;
            return NativeMethods.WTSIsChildSessionsEnabled(out e) && e;
        }

        public static uint ChildSessionId()
        {
            uint id;
            if (!NativeMethods.WTSGetChildSessionId(out id)) return NativeMethods.NoChildSession;
            return id;
        }

        public static bool HasChildSession()
        {
            uint id = ChildSessionId();
            return id != NativeMethods.NoChildSession && id != 0;
        }

        /// <summary>确定性判断自身是否运行在子会话中（SM_REMOTESESSION 在部分配置下会漏报）。</summary>
        public static bool InsideChildSession()
        {
            uint mySession;
            if (!NativeMethods.ProcessIdToSessionId(NativeMethods.GetCurrentProcessId(), out mySession))
                return false;
            uint childId = ChildSessionId();
            return childId != NativeMethods.NoChildSession && childId != 0 && childId == mySession;
        }

        public static bool InRemoteSession()
        {
            return NativeMethods.GetSystemMetrics(NativeMethods.SM_REMOTESESSION) != 0;
        }

        public static string EditionId() { return ReadHklm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID"); }
        public static string BuildNumber() { return ReadHklm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild"); }

        /// <summary>家庭版 EditionID 以 Core 开头。不可用 ProductName——Win11 上它仍写 "Windows 10"。</summary>
        public static bool IsHomeEdition()
        {
            string e = EditionId();
            return e != null && e.StartsWith("Core", StringComparison.OrdinalIgnoreCase);
        }

        public static bool RdpListenerEnabled()
        {
            try
            {
                object v = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server",
                    "fDenyTSConnections", 1);
                return v != null && Convert.ToInt32(v) == 0;
            }
            catch { return false; }
        }

        public static bool TermServiceRunning()
        {
            try
            {
                using (var sc = new ServiceController("TermService"))
                    return sc.Status == ServiceControllerStatus.Running;
            }
            catch { return false; }
        }

        public static bool RdpControlRegistered()
        {
            try
            {
                using (var k = Registry.ClassesRoot.OpenSubKey(
                    @"CLSID\{A0C63C30-F08D-4AB4-907C-34905D770C7D}"))
                    return k != null;
            }
            catch { return false; }
        }

        /// <summary>自检子会话通道。0=可用。这是启动前最可靠的就绪判据。</summary>
        public static int ProbeTransport(out string pipePath)
        {
            pipePath = "";
            try
            {
                var sb = new StringBuilder(0x80);
                int status = NativeMethods.WinStationCreateChildSessionTransport(sb, 0x80);
                if (status == 0) pipePath = sb.ToString();
                return status;
            }
            catch (Exception ex)
            {
                Log.Error("通道自检异常", ex);
                return -1;
            }
        }

        public static string DescribeTransportError(int status)
        {
            switch (unchecked((uint)status))
            {
                case 0: return L.T("通道可用");
                case 0x800706BA: return L.T("RPC 服务器不可用 —— 子会话监听器未启动（配置后需重启电脑）");
                case 0x80070005: return L.T("拒绝访问");
                default: return L.T("错误码 0x") + status.ToString("X8");
            }
        }

        private static readonly object CacheSync = new object();
        private static EnvironmentReport _cached;
        private static DateTime _cachedAt;

        /// <summary>
        /// 带缓存的环境检查，供界面高频刷新使用。
        ///
        /// 必须有这一层：Check() 内部会调 ProbeTransport，而它**真的会创建一个
        /// 子会话传输通道**——那是有副作用的调用，绝不能由每秒一次的界面刷新触发。
        /// 状态改变时（配置完成、桌面启停）调 Invalidate() 立即失效。
        /// </summary>
        public static EnvironmentReport CheckCached(int ttlSeconds = 10)
        {
            lock (CacheSync)
            {
                if (_cached != null && (DateTime.UtcNow - _cachedAt).TotalSeconds < ttlSeconds)
                    return _cached;
            }

            var fresh = Check();
            lock (CacheSync)
            {
                _cached = fresh;
                _cachedAt = DateTime.UtcNow;
            }
            return fresh;
        }

        /// <summary>状态可能已变，下次读取重新探测。</summary>
        public static void Invalidate()
        {
            lock (CacheSync) { _cached = null; }
        }

        /// <summary>完整检查（含有副作用的通道探测）。高频路径请用 CheckCached。</summary>
        public static EnvironmentReport Check()
        {
            var r = new EnvironmentReport();
            r.EditionId = EditionId();
            r.BuildNumber = BuildNumber();
            r.IsHomeEdition = IsHomeEdition();
            r.ChildSessionsEnabled = ChildSessionsEnabled();
            r.TermServiceRunning = TermServiceRunning();
            r.RdpListenerEnabled = RdpListenerEnabled();
            r.RdpControlRegistered = RdpControlRegistered();
            r.InsideChildSession = InsideChildSession();
            r.ChildSessionId = ChildSessionId();

            string pipe;
            r.TransportStatus = ProbeTransport(out pipe);
            r.TransportPipe = pipe;
            return r;
        }

        private static string ReadHklm(string subKey, string name)
        {
            try
            {
                object v = Registry.GetValue(@"HKEY_LOCAL_MACHINE\" + subKey, name, "");
                return v == null ? "" : v.ToString();
            }
            catch { return ""; }
        }
    }
}
