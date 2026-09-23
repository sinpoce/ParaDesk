using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using ParaDesk.Core;

namespace ParaDesk
{
    internal static class Program
    {
        private const int PreviousInstanceWaitMs = 10000;

        private const int ReportDialogIntervalMs = 5000;

        private const int ReportLogWindowMs = 60000;

        private const int ReportSignatureLimit = 32;

        private static readonly object ReportSync = new object();
        private static DateTime _lastReportUtc = DateTime.MinValue;
        private static bool _reportShowing;
        private static readonly Dictionary<string, ReportEntry> _reportSeen =
            new Dictionary<string, ReportEntry>(StringComparer.Ordinal);

        private sealed class ReportEntry
        {
            public DateTime LastFullUtc;
            public int Suppressed;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            if (args == null) args = new string[0];

            string verb = args.Length > 0 ? (args[0] ?? "").ToLowerInvariant() : "";

            if (verb == "--setup") return Elevated.SetupHost.Run(args);

            if (verb == "--childagent") return Ui.ChildSessionAgent.Run();

            if (IsCommandLineVerb(verb)) Log.ProcessTag = "cli";

            ReadSettingsQuietly();

            if (Cli.IsHelpVerb(verb)) return Cli.Help();

            int code;
            if (verb == "--version")
            {
                if (RejectExtraOptions(args, out code)) return code;
                return Cli.Version();
            }

            if (TryRunLocal(verb, args, out code)) return code;

            if (Cli.IsClientVerb(verb)) return Cli.Run(verb, args);

            string unknown = Cli.FindUnknownUiArgument(args);
            if (unknown != null) return Cli.UnknownArgument(unknown);

            if (SystemStatus.InsideChildSession())
            {
                if (Cli.IsAutostartLaunch(args))
                {
                    Log.Info("分身桌面内由开机自启项拉起，静默退出");
                    return 0;
                }
                MessageBox.Show(
                    string.Format(L.T("{0} 不能在分身桌面内运行，请回到主桌面打开。"), AppInfo.DisplayTitle),
                    AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            // 换语言时的重启：先等上一个实例退干净，否则单实例互斥会把自己挡在门外，
            // 表现成"点了切换语言，程序关了但没再打开"
            WaitForPreviousInstance(args);

            bool startMinimized = Cli.HasFlag(args, "--minimized");

            bool createdNew;
            Mutex mutex = CreateSingleInstanceMutex(out createdNew);
            if (!createdNew)
            {
                if (Cli.IsAutostartLaunch(args))
                {
                    Log.Info("已有实例在运行，开机自启的启动静默退出");
                    return 0;
                }

                // 用户再次点击图标，意图是"我要用这个程序"，
                // 正确响应是把已有窗口唤到前台，而不是弹一句"已在运行"就完事。
                if (!SingleInstance.NotifyExisting())
                {
                    MessageBox.Show(string.Format(L.T("{0} 已经在运行（请查看系统托盘）。"), AppInfo.DisplayTitle),
                        AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                ReportUnhandled(e.Exception, "WinForms");
            };
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

            Log.Info("=== " + AppInfo.Title + " v" + AppInfo.Version + " 启动 ===");

            AppSettings settings = LoadSettingsForUi();

            try
            {
                SingleInstance.StartListener();

                using (var app = new Ui.AppContext(startMinimized, settings))
                {
                    SingleInstance.CommandReceived += app.HandleControlRequest;
                    SingleInstance.AttachActivate(app.ActivateFromOtherInstance);
                    try
                    {
                        Application.Run(app);
                    }
                    finally
                    {
                        SingleInstance.Stop();
                        SingleInstance.ActivateRequested -= app.ActivateFromOtherInstance;
                        SingleInstance.CommandReceived -= app.HandleControlRequest;
                    }
                }
            }
            finally
            {
                SingleInstance.Stop();
                StateFile.MarkStopped();
                Log.Info("=== 退出 ===");
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        private static AppSettings LoadSettingsForUi()
        {
            AppSettings settings = null;
            try
            {
                settings = SettingsStore.Load();
                ApplySettingsLanguage(settings);
                Log.Info("界面语言 => " + L.Current);
            }
            catch (Exception ex) { Log.Warn("应用语言设置失败: " + ex.Message); }

            if (settings != null) MonitorNaming.Bind(settings);
            return settings;
        }

        private static AppSettings ReadSettingsQuietly()
        {
            AppSettings settings = null;
            try
            {
                // 先按系统语言初始化一次再读设置：首次运行时 Load() 会就地造出默认方案，
                // 那个名字要跟着系统语言走——否则英文用户一上来就看到一个中文方案名。
                L.Apply("auto", false);
                _systemLanguage = L.Current;
                string path = AppInfo.SettingsPath;
                if (System.IO.File.Exists(path))
                {
                    byte[] bytes;
                    using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                        System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
                    {
                        bytes = new byte[(int)fs.Length];
                        int read = 0;
                        while (read < bytes.Length)
                        {
                            int n = fs.Read(bytes, read, bytes.Length - read);
                            if (n <= 0) break;
                            read += n;
                        }
                    }
                    settings = SettingsStore.FromJsonBytes(bytes);
                    ApplySettingsLanguage(settings);
                }
            }
            catch (Exception ex) { Log.Debug("命令行读取设置失败，按系统语言: " + ex.Message); }

            if (settings != null) MonitorNaming.Bind(settings);
            return settings;
        }

        private static string _systemLanguage;

        private static void ApplySettingsLanguage(AppSettings settings)
        {
            if (settings == null) return;
            string lang = settings.Language;
            string target = (string.IsNullOrEmpty(lang) || lang == "auto") ? (_systemLanguage ?? "auto") : lang;
            if (target != L.Current) L.Apply(target, false);
        }

        private static bool IsCommandLineVerb(string verb)
        {
            return Cli.IsHelpVerb(verb) || verb == "--version" || IsLocalVerb(verb) || Cli.IsClientVerb(verb);
        }

        private static bool IsLocalVerb(string verb)
        {
            switch (verb)
            {
                case "--probe":
                case "--diagbundle":
                case "--logictest":
                case "--spike":
                case "--dumpres":
                case "--rectest":
                case "--contenttest":
                case "--i18ntest":
                case "--dlgtest":
                    return true;
                default:
                    return false;
            }
        }

        private static bool TakesNoOptions(string verb)
        {
            switch (verb)
            {
                case "--logictest":
                case "--spike":
                case "--dumpres":
                case "--contenttest":
                case "--i18ntest":
                case "--dlgtest":
                    return true;
                default:
                    return false;
            }
        }

        private static bool RejectExtraOptions(string[] args, out int code)
        {
            code = ExitCodes.Ok;
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i] ?? "";
                if (!a.StartsWith("--", StringComparison.Ordinal)) continue;
                code = Cli.IsHelpVerb(a) ? Cli.Help() : Cli.UnknownArgument(a);
                return true;
            }
            return false;
        }

        private static bool TryRunLocal(string verb, string[] args, out int code)
        {
            code = ExitCodes.Ok;
            if (!IsLocalVerb(verb)) return false;

            CliOutput.Begin();
            try
            {
                if (TakesNoOptions(verb) && RejectExtraOptions(args, out code)) return true;

                switch (verb)
                {
                    case "--probe": code = Diagnostics.ProbeCommand.Run(args); break;
                    case "--diagbundle": code = Diagnostics.DiagBundle.Run(args); break;
                    case "--logictest": code = Diagnostics.LogicTest.Run(); break;
                    case "--spike": code = Diagnostics.DisplaySpike.Run(); break;
                    case "--dumpres": code = Diagnostics.ResourceDump.Run(); break;
                    case "--rectest": code = Diagnostics.RecordTest.Run(args); break;
                    case "--contenttest": code = Diagnostics.ContentTest.Run(); break;
                    case "--i18ntest": code = Diagnostics.I18nTest.Run(); break;
                    case "--dlgtest": code = Diagnostics.DialogGuardTest.Run(); break;
                }
            }
            catch (Exception ex)
            {
                Log.Error("自检 " + verb + " 出现异常", ex);
                string msg = verb + ": " + ex.Message;
                if (verb == "--probe" && Cli.HasFlag(args, "--json"))
                    CliOutput.WriteLine(ControlProtocol.ToJson(ControlResponse.Fail(ExitCodes.Failed, msg)));
                else
                    CliOutput.WriteLine(msg);
                code = ExitCodes.Failed;
            }
            finally
            {
                CliOutput.End();
            }
            return true;
        }

        public static void ReportUnhandled(Exception ex, string source)
        {
            try
            {
                LogUnhandled(ex, source);

                bool show;
                lock (ReportSync)
                {
                    DateTime now = DateTime.UtcNow;
                    show = !_reportShowing && (now - _lastReportUtc).TotalMilliseconds >= ReportDialogIntervalMs;
                    _lastReportUtc = now;
                    if (show) _reportShowing = true;
                }
                if (!show) return;

                try
                {
                    string msg = L.T("出现未预期的错误：") + "\r\n" + (ex == null ? "" : ex.Message) +
                                 "\r\n\r\n" + L.T("日志：") + Log.Path0;
                    MessageBox.Show(msg, AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    lock (ReportSync)
                    {
                        _reportShowing = false;
                        _lastReportUtc = DateTime.UtcNow;
                    }
                }
            }
            catch
            {
            }
        }

        private static void LogUnhandled(Exception ex, string source)
        {
            string src = source ?? "?";
            string sig = ExceptionSignature(ex);
            bool logFull = false;
            int repeats;
            lock (ReportSync)
            {
                DateTime now = DateTime.UtcNow;
                ReportEntry en;
                if (!_reportSeen.TryGetValue(sig, out en))
                {
                    if (_reportSeen.Count >= ReportSignatureLimit) _reportSeen.Clear();
                    en = new ReportEntry();
                    _reportSeen[sig] = en;
                    logFull = true;
                }
                else if ((now - en.LastFullUtc).TotalMilliseconds >= ReportLogWindowMs)
                {
                    logFull = true;
                }

                if (logFull)
                {
                    repeats = en.Suppressed;
                    en.Suppressed = 0;
                    en.LastFullUtc = now;
                }
                else
                {
                    en.Suppressed++;
                    repeats = en.Suppressed;
                }
            }

            if (logFull)
            {
                Log.Error("未处理的异常（" + src + "）" +
                          (repeats > 0 ? "，上次完整记录之后同一异常又发生了 " + repeats + " 次" : ""), ex);
            }
            else if (repeats == 10 || repeats == 100 || repeats == 1000 || repeats == 10000)
            {
                Log.Warn("同一未处理异常（" + src + "）在上次完整记录之后已重复 " + repeats + " 次，只计数不再记堆栈: " +
                         (ex == null ? "" : ex.GetType().Name + ": " + ex.Message));
            }
        }

        internal static string ExceptionSignature(Exception ex)
        {
            if (ex == null) return "(null)";
            var sb = new StringBuilder();
            int depth = 0;
            for (Exception e = ex; e != null && depth < 8; e = e.InnerException, depth++)
            {
                sb.Append(e.GetType().FullName).Append('|');
                string st = e.StackTrace ?? "";
                int pos = -1;
                for (int i = 0; i < 3; i++)
                {
                    pos = st.IndexOf('\n', pos + 1);
                    if (pos < 0) break;
                }
                sb.Append(pos >= 0 ? st.Substring(0, pos) : st).Append("||");
            }
            return sb.ToString();
        }

        private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                Log.Error("未处理的异常（AppDomain" + (e.IsTerminating ? "，进程即将终止" : "") + "）",
                    e.ExceptionObject as Exception);
                if (e.IsTerminating) StateFile.MarkStopped();
            }
            catch
            {
            }
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
                    if (!prev.WaitForExit(PreviousInstanceWaitMs))
                        Log.Warn("上一个实例（pid " + pid + "）" + (PreviousInstanceWaitMs / 1000) + " 秒内没有退出，继续启动");
                }
            }
            catch (ArgumentException)
            {
                // 进程早没了就是最好的情况
            }
            catch (Exception ex)
            {
                Log.Debug("等待上一个实例退出失败: " + ex.Message);
            }
        }

        private static Mutex CreateSingleInstanceMutex(out bool createdNew)
        {
            try { return new Mutex(true, AppInfo.MutexNameGlobal, out createdNew); }
            catch (Exception ex)
            {
                Log.Warn("创建 Global 单实例互斥量失败，改用 Local（子会话内重复启动的拦截失效）: " + ex.Message);
            }

            try { return new Mutex(true, AppInfo.MutexNameLocal, out createdNew); }
            catch (Exception ex)
            {
                Log.Error("创建单实例互斥量失败，本次不做单实例保护", ex);
                createdNew = true;
                return null;
            }
        }
    }
}
