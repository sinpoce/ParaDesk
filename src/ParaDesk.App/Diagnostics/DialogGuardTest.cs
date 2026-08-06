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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string cls, string title);

        public static int Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 子会话守护自检 ===");

            int problems = 0;
            problems += Case(sb, Ui.ChildSessionAgent.Targets[0], true);
            problems += Case(sb, "一个正常的确认框，不该被动", false);

            sb.AppendLine(problems == 0 ? "结果            = 通过" : "结果            = 有问题");

            string text = sb.ToString();
            Console.Write(text);
            Log.Info(text.TrimEnd());
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
            for (int i = 0; i < 40 && hwnd == IntPtr.Zero; i++)
            {
                Thread.Sleep(50);
                hwnd = FindWindowW("#32770", caption);
            }
            if (hwnd == IntPtr.Zero)
            {
                sb.AppendLine("  跳过（消息框没弹出来）: " + Trim(caption));
                return 1;
            }

            Ui.ChildSessionAgent.SweepOnce();
            Thread.Sleep(600);

            bool gone = FindWindowW("#32770", caption) == IntPtr.Zero;
            bool ok = gone == shouldClose;

            sb.AppendLine(string.Format("  {0} 预期{1} 实际{2}  {3}",
                ok ? "通过" : "失败",
                shouldClose ? "关闭" : "保留",
                gone ? "关闭" : "保留",
                Trim(caption)));

            if (!gone)
            {
                // 别把自检自己的框留在屏幕上
                try { t.Abort(); } catch { }
            }
            return ok ? 0 : 1;
        }

        private static string Trim(string s)
        {
            return s.Length > 64 ? "…" + s.Substring(s.Length - 60) : s;
        }
    }
}
