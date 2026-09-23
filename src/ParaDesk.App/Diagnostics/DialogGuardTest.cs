using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ParaDesk.Core;

namespace ParaDesk.Diagnostics
{
    /// <summary>
    /// 子会话守护自检（--dlgtest）。
    ///
    /// 造一个标题和真实错误框完全一致的消息框，看守护能不能把它关掉；
    /// 再造一个别的标题，确认守护**不会**去动它。
    /// 后者和前者一样重要：一个见框就点确定的东西比那个错误框危险得多。
    ///
    /// 真实的框只在子会话里出现，没法在自检里复现，所以这里验的是
    /// "标题匹配 + 关窗"这段逻辑本身——它才是会写错的部分。
    /// </summary>
    internal static class DialogGuardTest
    {
        private const uint MB_OK = 0x0;
        private const uint WM_CLOSE = 0x0010;

        private const int WindowWaitAttempts = 40;
        private const int PollIntervalMs = 50;

        private const int SweepSettleMs = 600;

        private const int ThreadJoinMs = 2000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string cls, string title);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public static int Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 子会话守护自检 ===");

            int problems = 0;
            string[] targets = Ui.ChildSessionAgent.Targets;
            if (targets == null || targets.Length == 0)
            {
                sb.AppendLine("  失败: 守护的目标标题列表为空");
                problems++;
            }
            else
            {
                foreach (string target in targets)
                {
                    if (string.IsNullOrEmpty(target)) { sb.AppendLine("  失败: 目标列表里有空标题"); problems++; continue; }
                    problems += Case(sb, target, true);
                }

                problems += Case(sb, targets[0].ToLowerInvariant(), true);

                problems += Case(sb, targets[0] + ".bak", false);
            }
            problems += Case(sb, "一个正常的确认框，不该被动", false);

            sb.AppendLine(problems == 0 ? "结果            = 通过" : "结果            = 有问题");

            string text = sb.ToString();
            Console.Write(text);
            Log.Info("[dlgtest] " + text.TrimEnd());
            return problems == 0 ? 0 : 1;
        }

        /// <summary>弹一个指定标题的框，扫一遍，检查它是否按预期被关掉。</summary>
        private static int Case(StringBuilder sb, string caption, bool shouldClose)
        {
            var t = new Thread(delegate()
            {
                MessageBoxW(IntPtr.Zero, "ParaDesk self-test", caption, MB_OK);
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();

            // 等消息框真正建好
            IntPtr hwnd = IntPtr.Zero;
            for (int i = 0; i < WindowWaitAttempts && hwnd == IntPtr.Zero; i++)
            {
                Thread.Sleep(PollIntervalMs);
                hwnd = FindWindowW("#32770", caption);
            }
            if (hwnd == IntPtr.Zero)
            {
                sb.AppendLine("  跳过（消息框没弹出来）: " + Trim(caption));
                CloseOwnBox(t, FindWindowW("#32770", caption));
                return 1;
            }

            Ui.ChildSessionAgent.SweepOnce();
            Thread.Sleep(SweepSettleMs);

            bool gone = FindWindowW("#32770", caption) == IntPtr.Zero;
            bool ok = gone == shouldClose;

            sb.AppendLine(string.Format("  {0} 预期{1} 实际{2}  {3}",
                ok ? "通过" : "失败",
                shouldClose ? "关闭" : "保留",
                gone ? "关闭" : "保留",
                Trim(caption)));

            CloseOwnBox(t, gone ? IntPtr.Zero : hwnd);
            return ok ? 0 : 1;
        }

        private static void CloseOwnBox(Thread t, IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero && !PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
                Log.Debug("[dlgtest] 关闭自检消息框失败，错误码 " + Marshal.GetLastWin32Error());

            if (!t.Join(ThreadJoinMs))
                Log.Debug("[dlgtest] 弹框线程未在 " + ThreadJoinMs + " 毫秒内退出（后台线程，随进程结束）");
        }

        private static string Trim(string s)
        {
            return s.Length > 64 ? "…" + s.Substring(s.Length - 60) : s;
        }
    }
}
