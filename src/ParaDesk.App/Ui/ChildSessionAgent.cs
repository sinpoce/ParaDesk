using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ParaDesk.Core;

namespace ParaDesk.Ui
{
    /// <summary>
    /// 在分身桌面（子会话）内部运行的小守护，只做一件事：
    /// 关掉 Windows 自己弹出来的那个没法处理的错误框。
    ///
    /// 背景：子会话里 Windows 外壳包 MicrosoftWindows.Client.CBS_* 无法完成 COM 激活，
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
        /// <summary>只认这一个标题。Windows 的消息框把完整路径放在标题栏上。</summary>
        internal static readonly string[] Targets =
        {
            @"C:\WINDOWS\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\CrossDeviceResume.exe",
        };

        private const string DialogClass = "#32770";   // Win32 对话框（含消息框）的窗口类
        private const uint WM_CLOSE = 0x0010;

        public static int Run()
        {
            // 主会话里被 Run 键顺带拉起时直接退出：这个守护只在分身桌面里有意义
            if (!SystemStatus.InsideChildSession())
            {
                return 0;
            }

            Log.Info("[子会话守护] 已启动，监视 Windows 的无效错误框");

            int closed = 0;
            while (true)
            {
                try
                {
                    closed += SweepOnce();
                }
                catch (Exception ex)
                {
                    Log.Debug("[子会话守护] 扫描失败: " + ex.Message);
                }
                Thread.Sleep(1000);
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
    }
}
