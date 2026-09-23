using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ParaDesk.Core;
using ParaDesk.Native;

namespace ParaDesk.Ui
{
    /// <summary>
    ///
    /// 每次登录都会连炸三样——SearchHost 崩溃、CortanaUI 激活失败、
    /// 以及有人去拉 CrossDeviceResume.exe 被拒，弹出
    /// "Windows 无法访问指定设备、路径或文件"。
    ///
    /// 这是微软自己的问题：同一个 exe 在主会话里直接运行完全正常，
    /// 而设置里的开关（EnableCdp / EnableMmx / IsResumeAllowed）实测全是关的也照弹；
    /// 在映像层拦（IFEO）同样无效，说明拒绝发生在 CreateProcess 之前——
    /// 调用方多半是个 AppContainer 进程，它压根没有拉起 Win32 可执行文件的权限。
    /// 修不了根因，那就别让它打扰用户。
    ///
    /// 安全边界：只关标题**完全等于**那个已知路径的对话框。
    /// 绝不做"看到对话框就点确定"这种事——那会替用户误点掉真正重要的确认。
    /// </summary>
    internal static class ChildSessionAgent
    {
        internal static readonly string[] Targets =
        {
            Path.Combine(WindowsDirectory(), @"SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\CrossDeviceResume.exe"),
        };

        private const string DialogClass = "#32770";   // Win32 对话框（含消息框）的窗口类
        private const uint WM_CLOSE = 0x0010;

        private const string AgentMutexName = @"Local\ParaDeskChildAgent";

        private const int FastPhaseMs = 3 * 60 * 1000;
        private const int FastSweepMs = 1000;
        private const int SlowSweepMs = 3000;

        private const int StartupDelayMs = 3000;

        private const int SettingsPollMs = 30 * 1000;

        private const int SettingsRetryMs = 5000;

        private const uint IdleNudgeMs = 20 * 1000;

        private const int MinNudgeIntervalMs = 15 * 1000;

        private sealed class AgentState
        {
            public uint SessionId;
            public bool SettingsLoaded;
            public DateTime SettingsStamp;
            public string LastReadError;
            public bool KeepAwake;
            public bool KeepAwakeApplied;
            public bool NudgeLogged;
            public bool NudgeFailureLogged;
            public bool NudgePending;
            public bool NudgeIneffectiveLogged;
            public bool HasNudged;
            public int LastNudgeTick;
        }

        public static int Run()
        {
            Log.ProcessTag = "agent";

            // 主会话里被 Run 键顺带拉起时直接退出：这个守护只在分身桌面里有意义
            if (!SystemStatus.InsideChildSession())
            {
                return 0;
            }

            Mutex single = null;
            bool createdNew = true;
            try { single = new Mutex(true, AgentMutexName, out createdNew); }
            catch (Exception ex) { Log.Debug("创建守护互斥体失败，照常运行: " + ex.Message); }
            if (!createdNew)
            {
                Log.Info("本会话里已有子会话守护在运行，退出");
                single.Dispose();
                return 0;
            }

            var st = new AgentState { SessionId = CurrentSessionId() };
            Log.Info("子会话守护已启动（会话 " + st.SessionId + "），监视 Windows 的无效错误框");
            try
            {
                RunLoop(st);
            }
            finally
            {
                GC.KeepAlive(single);
            }
            return 0;
        }

        private static void RunLoop(AgentState st)
        {
            var clock = Stopwatch.StartNew();
            long nextSettingsCheck = StartupDelayMs;

            while (true)
            {
                try
                {
                    SweepOnce();
                }
                catch (Exception ex)
                {
                    Log.Debug("扫描错误框失败: " + ex.Message);
                }

                long now = clock.ElapsedMilliseconds;
                if (now >= nextSettingsCheck)
                {
                    try { CheckSettings(st); }
                    catch (Exception ex) { Log.Error("处理设置失败", ex); }
                    nextSettingsCheck = now + (st.SettingsLoaded ? SettingsPollMs : SettingsRetryMs);
                }

                if (st.KeepAwake)
                {
                    try { NudgeIfIdle(st); }
                    catch (Exception ex) { Log.Debug("保持唤醒检查失败: " + ex.Message); }
                }

                Thread.Sleep(now < FastPhaseMs ? FastSweepMs : SlowSweepMs);
            }
        }

        /// <summary>供自检调用：扫一遍并返回关掉的数量。</summary>
        internal static int SweepOnce()
        {
            int n = 0;
            EnumWindows(delegate(IntPtr hwnd, IntPtr lp)
            {
                if (!IsWindowVisible(hwnd)) return true;

                var cls = new StringBuilder(64);
                GetClassName(hwnd, cls, cls.Capacity);
                if (cls.ToString() != DialogClass) return true;

                int len = GetWindowTextLength(hwnd);
                if (len <= 0) return true;
                var title = new StringBuilder(len + 1);
                GetWindowText(hwnd, title, title.Capacity);
                string t = title.ToString();

                foreach (string target in Targets)
                {
                    if (!string.Equals(t, target, StringComparison.OrdinalIgnoreCase)) continue;
                    PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    Log.Info("[子会话守护] 已关闭系统错误框: " + t);
                    n++;
                    break;
                }
                return true;
            }, IntPtr.Zero);
            return n;
        }

        private static void CheckSettings(AgentState st)
        {
            DateTime stamp = SettingsStamp();
            if (st.SettingsLoaded && stamp == st.SettingsStamp) return;

            AppSettings settings;
            string error;
            if (!SettingsStore.TryReadQuietly(out settings, out error))
            {
                if (error != st.LastReadError) Log.Warn("读取设置失败，稍后重试: " + error);
                st.LastReadError = error;
                return;
            }
            st.LastReadError = null;
            st.SettingsStamp = stamp;

            bool first = !st.SettingsLoaded;
            st.SettingsLoaded = true;

            DesktopProfile profile = settings.GetActiveProfile();
            if (first) RunStartupCommand(profile, st.SessionId);
            ApplyKeepAwake(st, profile != null && profile.KeepAwake, profile == null ? "" : profile.Name);
        }

        private static DateTime SettingsStamp()
        {
            try { return File.GetLastWriteTimeUtc(AppInfo.SettingsPath); }
            catch { return DateTime.MinValue; }
        }

        private static void RunStartupCommand(DesktopProfile profile, uint sessionId)
        {
            if (profile == null) return;
            string command = (profile.StartupCommand ?? "").Trim();
            if (command.Length == 0) return;

            try
            {
                string dir = ResolveWorkingDir(profile.StartupWorkingDir);
                bool compound = HasUnquotedShellOperator(command);
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    Arguments = compound ? BuildCompoundArguments(command) : BuildStartArguments(dir, command),
                    WorkingDirectory = dir,
                    UseShellExecute = false,
                    CreateNoWindow = !compound,
                };
                psi.EnvironmentVariables["PARADESK"] = "1";
                psi.EnvironmentVariables["PARADESK_PROFILE"] = profile.Name ?? "";
                psi.EnvironmentVariables["PARADESK_SESSION_ID"] = sessionId.ToString(CultureInfo.InvariantCulture);

                using (Process p = Process.Start(psi))
                {
                    if (compound)
                    {
                        Log.Info("已执行启动命令（含连接符，在可见的 cmd 窗口里整条执行；方案「" + profile.Name + "」，目录 " + dir + "），命令 " + command.Length + " 个字符");
                    }
                    else
                    {
                        Log.Info("已执行启动命令（已交给 cmd 的 start；方案「" + profile.Name + "」，目录 " + dir + "），命令 " + command.Length + " 个字符");
                        ReportStartResult(p);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("执行启动命令失败（命令 " + command.Length + " 个字符）", ex);
            }
        }

        private static void ReportStartResult(Process p)
        {
            if (p == null) return;
            try
            {
                if (!p.WaitForExit(StartResultWaitMs))
                {
                    Log.Debug("启动命令的外层 cmd 在 " + (StartResultWaitMs / 1000) + " 秒内没有退出（start 可能正弹着提示框），不再等待");
                    return;
                }
                int code = p.ExitCode;
                if (code != 0) Log.Warn("启动命令没有成功启动：start 返回错误码 " + code + "（程序名或路径可能写错了；为免泄露密钥，日志里不记命令全文）");
            }
            catch (Exception ex) { Log.Debug("读取启动命令的结果失败: " + ex.Message); }
        }

        private const int StartResultWaitMs = 2000;

        internal static string BuildStartArguments(string dir, string command)
        {
            return "/c start \"" + AppInfo.ProductName + "\" /D " + QuoteForCmd(dir) + " " + command;
        }

        internal static string BuildCompoundArguments(string command)
        {
            return "/s /k \"" + command + "\"";
        }

        internal static bool HasUnquotedShellOperator(string command)
        {
            if (string.IsNullOrEmpty(command)) return false;
            bool inQuote = false;
            for (int i = 0; i < command.Length; i++)
            {
                char c = command[i];
                if (c == '"') { inQuote = !inQuote; continue; }
                if (inQuote) continue;
                if (c == '^') { i++; continue; }
                if (c == '&' || c == '|' || c == '<' || c == '>') return true;
            }
            return false;
        }

        private static string QuoteForCmd(string path)
        {
            string p = (path ?? "").Replace("\"", "");
            if (p.Length > 3) p = p.TrimEnd('\\');
            return "\"" + p + "\"";
        }

        internal static string ResolveWorkingDir(string configured)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) home = Environment.CurrentDirectory;

            string dir = (configured ?? "").Trim().Trim('"');
            if (dir.Length == 0) return home;
            try
            {
                dir = Environment.ExpandEnvironmentVariables(dir);
                if (!Path.IsPathRooted(dir)) dir = Path.Combine(home, dir);
                if (Directory.Exists(dir)) return Path.GetFullPath(dir);
                Log.Warn("启动命令的工作目录不存在，改用用户主目录: " + dir);
            }
            catch (Exception ex)
            {
                Log.Warn("启动命令的工作目录无效（" + ex.Message + "），改用用户主目录: " + dir);
            }
            return home;
        }

        private static void ApplyKeepAwake(AgentState st, bool want, string profileName)
        {
            if (st.KeepAwakeApplied && want == st.KeepAwake) return;

            uint flags = want ? (ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED) : ES_CONTINUOUS;
            if (SetThreadExecutionState(flags) == 0)
                Log.Warn("SetThreadExecutionState 调用失败，保持唤醒可能不生效");

            bool wasApplied = st.KeepAwakeApplied;
            st.KeepAwake = want;
            st.KeepAwakeApplied = true;
            st.NudgePending = false;

            if (want) Log.Info("保持唤醒已开启（方案「" + profileName + "」）");
            else if (wasApplied) Log.Info("保持唤醒已关闭");
        }

        private static void NudgeIfIdle(AgentState st)
        {
            uint idle = IdleMilliseconds();

            if (st.NudgePending)
            {
                st.NudgePending = false;
                if (idle >= IdleNudgeMs && !st.NudgeIneffectiveLogged)
                {
                    st.NudgeIneffectiveLogged = true;
                    Log.Warn("注入零位移鼠标输入后空闲计时没有复位，保持唤醒可能拦不住空闲锁屏");
                }
            }

            if (idle < IdleNudgeMs) return;
            int nowTick = Environment.TickCount;
            if (st.HasNudged && unchecked(nowTick - st.LastNudgeTick) < MinNudgeIntervalMs) return;

            var inputs = new INPUT[1];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].mi.dx = 0;
            inputs[0].mi.dy = 0;
            inputs[0].mi.dwFlags = MOUSEEVENTF_MOVE;

            st.HasNudged = true;
            st.LastNudgeTick = nowTick;
            uint sent = SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
            if (sent == 1)
            {
                st.NudgePending = true;
                st.NudgeFailureLogged = false;
                if (!st.NudgeLogged)
                {
                    st.NudgeLogged = true;
                    Log.Info("会话空闲超过 " + (IdleNudgeMs / 1000) + " 秒，已注入零位移鼠标输入以免锁屏（之后不再逐次记录）");
                }
            }
            else if (!st.NudgeFailureLogged)
            {
                st.NudgeFailureLogged = true;
                Log.Debug("注入零位移鼠标输入失败（会话可能已锁定或断开），错误码 " + Marshal.GetLastWin32Error());
            }
        }

        private static uint IdleMilliseconds()
        {
            var info = new LASTINPUTINFO();
            info.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!GetLastInputInfo(ref info)) return 0;
            return unchecked((uint)Environment.TickCount - info.dwTime);
        }

        private static uint CurrentSessionId()
        {
            uint id;
            return NativeMethods.ProcessIdToSessionId(NativeMethods.GetCurrentProcessId(), out id) ? id : 0;
        }

        private static string WindowsDirectory()
        {
            string dir = null;
            try { dir = Environment.GetFolderPath(Environment.SpecialFolder.Windows); }
            catch { }
            if (string.IsNullOrEmpty(dir)) dir = Environment.GetEnvironmentVariable("SystemRoot");
            if (string.IsNullOrEmpty(dir)) dir = @"C:\Windows";
            return dir;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder buf, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder buf, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        private const uint INPUT_MOUSE = 0;
        private const uint MOUSEEVENTF_MOVE = 0x0001;

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);
    }
}
