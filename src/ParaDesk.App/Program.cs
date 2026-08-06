using System;
using System.Threading;
using System.Windows.Forms;
using ParaDesk.Core;

namespace ParaDesk
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            // 提权子进程分支：只做配置，不建界面
            if (verb == "--setup") return Elevated.SetupHost.Run(args);
            if (verb == "--probe") return Diagnostics.ProbeCommand.Run();
            if (verb == "--spike") return Diagnostics.DisplaySpike.Run();
            if (verb == "--dumpres") return Diagnostics.ResourceDump.Run();
            if (verb == "--rectest") return Diagnostics.RecordTest.Run(args);
            if (verb == "--contenttest") return Diagnostics.ContentTest.Run();
            if (verb == "--i18ntest") return Diagnostics.I18nTest.Run();
            if (verb == "--dlgtest") return Diagnostics.DialogGuardTest.Run();

            // 子会话守护：由 HKCU\Run 拉起，在主会话里会自己判断并立即退出
            if (verb == "--childagent") return Ui.ChildSessionAgent.Run();

            if (SystemStatus.InsideChildSession())
            {
                MessageBox.Show(
                    AppInfo.Title + " 不能在分身桌面内运行，请回到主桌面打开。",
                    AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            // 换语言时的重启：先等上一个实例退干净，否则单实例互斥会把自己挡在门外，
            // 表现成"点了切换语言，程序关了但没再打开"
            WaitForPreviousInstance(args);

            bool createdNew;
            Mutex mutex = CreateSingleInstanceMutex(out createdNew);
            if (!createdNew)
            {
                // 用户再次点击图标，意图是"我要用这个程序"，
                // 正确响应是把已有窗口唤到前台，而不是弹一句"已在运行"就完事。
                if (!SingleInstance.NotifyExisting())
                {
                    MessageBox.Show(AppInfo.Title + " 已经在运行（请查看系统托盘）。",
                        AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                Log.Error("未处理的 UI 线程异常", e.Exception);
                MessageBox.Show("出现未预期的错误：\r\n" + e.Exception.Message +
                                "\r\n\r\n日志：" + Log.Path0,
                    AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log.Error("未处理的异常", e.ExceptionObject as Exception);
            };

            Log.Info("=== " + AppInfo.Title + " v" + AppInfo.Version + " 启动 ===");

            // 语言要在建任何界面之前定好。
            // 先按系统语言初始化一次再读设置：首次运行时 Load() 会就地造出默认方案，
            // 那个名字要跟着系统语言走——否则英文用户一上来就看到一个中文方案名。
            try
            {
                L.Apply("auto");
                L.Apply(SettingsStore.Load().Language);
            }
            catch (Exception ex) { Log.Warn("应用语言设置失败: " + ex.Message); }

            bool startMinimized = Array.IndexOf(args, "--minimized") >= 0;

            try
            {
                using (var app = new Ui.AppContext(startMinimized))
                {
                    SingleInstance.ActivateRequested += app.ActivateFromOtherInstance;
                    SingleInstance.StartListener();
                    Application.Run(app);
                    SingleInstance.Stop();
                }
            }
            finally
            {
                Log.Info("=== 退出 ===");
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        /// <summary>
        /// `--restart &lt;pid&gt;`：等上一个实例退出后再继续启动。
        /// 超时也照常继续——宁可让单实例逻辑去兜底，也不能永远卡在这里不启动。
        /// </summary>
        private static void WaitForPreviousInstance(string[] args)
        {
            int pid = 0;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (!string.Equals(args[i], "--restart", StringComparison.OrdinalIgnoreCase)) continue;
                int.TryParse(args[i + 1], out pid);
                break;
            }
            if (pid <= 0) return;

            try
            {
                using (var prev = System.Diagnostics.Process.GetProcessById(pid))
                {
                    prev.WaitForExit(10000);
                }
            }
            catch { }   // 进程早没了就是最好的情况
        }

        private static Mutex CreateSingleInstanceMutex(out bool createdNew)
        {
            // Global 优先：这样即使有人在子会话里再启动一次也会被拦下。
            try { return new Mutex(true, AppInfo.MutexNameGlobal, out createdNew); }
            catch { return new Mutex(true, AppInfo.MutexNameLocal, out createdNew); }
        }
    }
}
