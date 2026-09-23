using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace ParaDesk.Core
{
    internal static class Cli
    {
        public const int ConnectTimeoutMs = 2000;

        public const int ReplyTimeoutMs = 120000;

        private const int QuitWaitTimeoutMs = 60000;

        private static readonly string[] ClientVerbs =
        {
            "--status", "--start", "--detach", "--close", "--view-only", "--topmost",
            "--record", "--screenshot", "--notify", "--show", "--quit",
        };

        private static readonly string[] OnOffToggle = { "on", "off", "toggle" };
        private static readonly string[] RecordActions = { "start", "stop", "pause", "resume", "toggle" };
        private static readonly string[] RecordTargets = { "desktop", "monitor" };
        private static readonly string[] NotifyLevels = { "info", "warn", "error" };

        public static bool IsClientVerb(string verb)
        {
            return IndexOf(ClientVerbs, Lower(verb)) >= 0;
        }

        public static bool IsHelpVerb(string verb)
        {
            switch (Lower(verb))
            {
                case "--help":
                case "-h":
                case "-?":
                case "/?":
                case "/h":
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsAutostartLaunch(string[] args)
        {
            return HasFlag(args, "--autostart") || HasFlag(args, "--minimized");
        }

        public static bool HasFlag(string[] args, string flag)
        {
            if (args == null) return false;
            foreach (string a in args)
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static string FindUnknownUiArgument(string[] args)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i] ?? "";
                string lower = a.ToLowerInvariant();
                if (lower == "--minimized") continue;
                if (lower == "--autostart") continue;
                if (lower == "--restart") { i++; continue; }
                if (lower.StartsWith("--", StringComparison.Ordinal)) return a;
            }
            return null;
        }

        public static int Help()
        {
            CliOutput.Begin();
            try
            {
                string text = HelpText();
                if (CliOutput.HasChannel) CliOutput.Write(text);
                else ShowBox(text, MessageBoxIcon.Information);
                return ExitCodes.Ok;
            }
            finally { CliOutput.End(); }
        }

        public static int Version()
        {
            CliOutput.Begin();
            try
            {
                string text = AppInfo.ProductName + " " + AppInfo.Version;
                if (CliOutput.HasChannel) CliOutput.WriteLine(text);
                else ShowBox(text, MessageBoxIcon.Information);
                return ExitCodes.Ok;
            }
            finally { CliOutput.End(); }
        }

        public static int UnknownArgument(string arg)
        {
            CliOutput.Begin();
            try
            {
                string msg = string.Format(L.T("不认识的参数：{0}"), arg);
                if (CliOutput.HasChannel)
                {
                    CliOutput.WriteLine(msg);
                    CliOutput.WriteLine();
                    CliOutput.Write(HelpText());
                }
                else
                {
                    ShowBox(msg + "\r\n\r\n" + string.Format(L.T("运行 {0} --help 查看全部命令。"), ExeName()),
                        MessageBoxIcon.Warning);
                }
                return ExitCodes.Usage;
            }
            finally { CliOutput.End(); }
        }

        public static int Run(string verb, string[] args)
        {
            CliOutput.Begin();
            bool jsonRequested = false;
            try
            {
                string v = Lower(verb);
                var rest = new List<string>();
                if (args != null)
                    for (int i = 1; i < args.Length; i++) rest.Add(args[i] ?? "");

                foreach (string a in rest)
                {
                    if (a == "--") break;
                    if (string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase))
                    {
                        jsonRequested = true;
                        break;
                    }
                }

                bool wantsHelp = rest.Count == 1 && IsHelpVerb(rest[0]);
                foreach (string a in rest)
                {
                    if (a == "--") break;
                    if (string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase)) wantsHelp = true;
                }
                if (wantsHelp)
                {
                    CliOutput.Write(HelpText());
                    return ExitCodes.Ok;
                }

                string error;
                ControlRequest req = BuildRequest(v, rest, out error);
                if (req == null) return UsageError(v, error, jsonRequested);

                bool json = req.Has("json");
                bool connected;
                ControlResponse resp = SingleInstance.SendCommand(req, ConnectTimeoutMs, ReplyTimeoutMs, out connected);
                if (!connected) return NotRunning(req.Command, json);
                if (resp == null) resp = ControlResponse.Fail(ExitCodes.Failed, L.T("与 ParaDesk 通信失败。"));

                if (req.Command == "quit" && resp.Ok && !SingleInstance.WaitForInstanceExit(QuitWaitTimeoutMs))
                    resp = ControlResponse.Fail(ExitCodes.Failed, L.T("已发出退出请求，但 ParaDesk 在 60 秒内没有退出。"));

                Print(resp, json);
                int code = resp.ExitCode;
                if (!resp.Ok && code == ExitCodes.Ok) code = ExitCodes.Failed;
                return code;
            }
            catch (Exception ex)
            {
                Log.Error("命令行命令执行失败: " + verb, ex);
                string msg = L.T("命令执行出错：") + ex.Message;
                if (jsonRequested) CliOutput.WriteLine(ControlProtocol.ToJson(ControlResponse.Fail(ExitCodes.Failed, msg)));
                else CliOutput.WriteLine(msg);
                return ExitCodes.Failed;
            }
            finally
            {
                CliOutput.End();
            }
        }

        private static void Print(ControlResponse resp, bool json)
        {
            if (json)
            {
                string j = !string.IsNullOrEmpty(resp.Json) ? resp.Json : ControlProtocol.ToJson(resp);
                CliOutput.WriteLine(ControlProtocol.EscapeNonAscii(j));
            }
            else if (!string.IsNullOrEmpty(resp.Message))
            {
                CliOutput.WriteLine(resp.Message);
            }
        }

        private static int NotRunning(string command, bool json)
        {
            string msg = string.Format(L.T("{0} 没有在运行。"), AppInfo.ProductName);

            if (command == "status" && json)
            {
                DesktopStateSnapshot s = StateFile.TryRead() ?? NewEmptySnapshot();
                StateFile.ApplyStopped(s);
                s.Version = StateFile.CurrentVersion;
                s.AppVersion = AppInfo.Version;
                FillLiveChildSession(s);
                CliOutput.WriteLine(ControlProtocol.ToJson(s));
                return ExitCodes.NotRunning;
            }

            if (json) CliOutput.WriteLine(ControlProtocol.ToJson(ControlResponse.Fail(ExitCodes.NotRunning, msg)));
            else CliOutput.WriteLine(msg);
            return ExitCodes.NotRunning;
        }

        private static DesktopStateSnapshot NewEmptySnapshot()
        {
            return new DesktopStateSnapshot
            {
                Version = StateFile.CurrentVersion,
                DesktopState = "idle",
                ChildSessionId = -1,
            };
        }

        private static void FillLiveChildSession(DesktopStateSnapshot s)
        {
            try
            {
                if (SystemStatus.InsideChildSession())
                {
                    s.ChildSessionExists = true;
                    using (var p = System.Diagnostics.Process.GetCurrentProcess()) s.ChildSessionId = p.SessionId;
                    return;
                }
                bool exists = SystemStatus.HasChildSession();
                s.ChildSessionExists = exists;
                s.ChildSessionId = exists ? (long)SystemStatus.ChildSessionId() : -1;
            }
            catch (Exception ex) { Log.Debug("查询子会话状态失败: " + ex.Message); }
        }

        private static ControlRequest BuildRequest(string verb, List<string> rest, out string error)
        {
            error = null;
            string cmd = verb.StartsWith("--", StringComparison.Ordinal) ? verb.Substring(2) : verb;
            var opts = new Dictionary<string, string>(StringComparer.Ordinal);
            var pos = new List<string>();
            var outArgs = new List<string>();

            switch (cmd)
            {
                case "status":
                case "detach":
                case "close":
                case "show":
                case "quit":
                    if (!Parse(rest, new[] { "--json" }, new string[0], opts, pos, out error)) return null;
                    if (pos.Count > 0) { error = Extra(pos[0]); return null; }
                    break;

                case "start":
                    if (!Parse(rest, new[] { "--json", "--wait" }, new[] { "--profile" }, opts, pos, out error)) return null;
                    if (pos.Count > 0) { error = Extra(pos[0]); return null; }
                    if (opts.ContainsKey("--profile"))
                    {
                        string p = opts["--profile"].Trim();
                        if (p.Length == 0) { error = L.T("--profile 后面需要一个方案名。"); return null; }
                        outArgs.Add("--profile");
                        outArgs.Add(p);
                    }
                    if (opts.ContainsKey("--wait")) outArgs.Add("--wait");
                    break;

                case "view-only":
                case "topmost":
                {
                    if (!Parse(rest, new[] { "--json" }, new string[0], opts, pos, out error)) return null;
                    string val;
                    if (!OneChoice(pos, OnOffToggle, out val, out error)) return null;
                    outArgs.Add(val);
                    break;
                }

                case "record":
                {
                    if (!Parse(rest, new[] { "--json" }, new[] { "--target" }, opts, pos, out error)) return null;
                    string action;
                    if (!OneChoice(pos, RecordActions, out action, out error)) return null;
                    outArgs.Add(action);
                    if (opts.ContainsKey("--target"))
                    {
                        string t = opts["--target"].Trim().ToLowerInvariant();
                        if (IndexOf(RecordTargets, t) < 0) { error = BadValue(opts["--target"], RecordTargets); return null; }
                        outArgs.Add("--target");
                        outArgs.Add(t);
                    }
                    break;
                }

                case "screenshot":
                    if (!Parse(rest, new[] { "--json" }, new string[0], opts, pos, out error)) return null;
                    if (pos.Count > 1) { error = Extra(pos[1]); return null; }
                    if (pos.Count == 1)
                    {
                        string full;
                        try { full = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, pos[0])); }
                        catch (Exception) { error = string.Format(L.T("路径无效：{0}"), pos[0]); return null; }
                        outArgs.Add(full);
                    }
                    break;

                case "notify":
                {
                    if (!Parse(rest, new[] { "--json", "--sound" }, new[] { "--title", "--body", "--level" }, opts, pos, out error))
                        return null;
                    string title = opts.ContainsKey("--title") ? opts["--title"] : null;
                    string body = opts.ContainsKey("--body") ? opts["--body"] : null;
                    if (pos.Count > 0)
                    {
                        if (body != null) { error = Extra(pos[0]); return null; }
                        body = string.Join(" ", pos.ToArray());
                    }
                    if (IsBlank(title) && IsBlank(body)) { error = L.T("提醒至少要有标题或正文。"); return null; }

                    string level = null;
                    if (opts.ContainsKey("--level"))
                    {
                        level = opts["--level"].Trim().ToLowerInvariant();
                        if (level == "warning") level = "warn";
                        if (IndexOf(NotifyLevels, level) < 0) { error = BadValue(opts["--level"], NotifyLevels); return null; }
                    }

                    if (!IsBlank(title)) { outArgs.Add("--title"); outArgs.Add(title); }
                    if (!IsBlank(body)) { outArgs.Add("--body"); outArgs.Add(body); }
                    if (level != null) { outArgs.Add("--level"); outArgs.Add(level); }
                    if (opts.ContainsKey("--sound")) outArgs.Add("--sound");
                    break;
                }

                default:
                    error = string.Format(L.T("不认识的参数：{0}"), verb);
                    return null;
            }

            if (opts.ContainsKey("--json")) outArgs.Add("--json");
            return new ControlRequest
            {
                Command = cmd,
                Args = outArgs,
                WorkingDirectory = Environment.CurrentDirectory,
            };
        }

        private static bool Parse(List<string> rest, string[] flags, string[] valueOptions,
            Dictionary<string, string> opts, List<string> pos, out string error)
        {
            error = null;
            bool optionsEnded = false;
            for (int i = 0; i < rest.Count; i++)
            {
                string a = rest[i];
                if (!optionsEnded && a == "--") { optionsEnded = true; continue; }
                if (!optionsEnded && a.StartsWith("--", StringComparison.Ordinal))
                {
                    string name = a.ToLowerInvariant();
                    if (IndexOf(flags, name) >= 0) { opts[name] = ""; continue; }
                    if (IndexOf(valueOptions, name) >= 0)
                    {
                        if (i + 1 >= rest.Count) { error = string.Format(L.T("{0} 后面缺少取值。"), name); return false; }
                        opts[name] = rest[++i];
                        continue;
                    }
                    error = string.Format(L.T("不认识的参数：{0}"), a);
                    return false;
                }
                pos.Add(a);
            }
            return true;
        }

        private static bool OneChoice(List<string> pos, string[] choices, out string value, out string error)
        {
            value = null;
            error = null;
            if (pos.Count == 0)
            {
                error = string.Format(L.T("缺少参数，应为 {0} 之一。"), string.Join("|", choices));
                return false;
            }
            if (pos.Count > 1) { error = Extra(pos[1]); return false; }
            string v = pos[0].Trim().ToLowerInvariant();
            if (IndexOf(choices, v) < 0) { error = BadValue(pos[0], choices); return false; }
            value = v;
            return true;
        }

        private static string BadValue(string value, string[] choices)
        {
            return string.Format(L.T("“{0}”不是有效的取值，应为 {1} 之一。"), value, string.Join("|", choices));
        }

        private static string Extra(string arg)
        {
            return string.Format(L.T("多余的参数：{0}"), arg);
        }

        private static int UsageError(string verb, string error, bool json)
        {
            if (json)
            {
                CliOutput.WriteLine(ControlProtocol.ToJson(ControlResponse.Fail(ExitCodes.Usage, error ?? "")));
                return ExitCodes.Usage;
            }
            if (!string.IsNullOrEmpty(error)) CliOutput.WriteLine(error);
            string u = UsageOf(verb);
            if (u != null) CliOutput.WriteLine(L.T("用法：") + ExeName() + " " + u);
            CliOutput.WriteLine(string.Format(L.T("运行 {0} --help 查看全部命令。"), ExeName()));
            return ExitCodes.Usage;
        }

        private static string UsageOf(string verb)
        {
            switch (verb)
            {
                case "--status": return "--status [--json]";
                case "--start": return L.T("--start [--profile <方案名>] [--wait] [--json]");
                case "--detach": return "--detach [--json]";
                case "--close": return "--close [--json]";
                case "--view-only": return "--view-only on|off|toggle [--json]";
                case "--topmost": return "--topmost on|off|toggle [--json]";
                case "--record": return "--record start|stop|pause|resume|toggle [--target desktop|monitor] [--json]";
                case "--screenshot": return L.T("--screenshot [<路径>] [--json]");
                case "--notify": return L.T("--notify [--title <标题>] [--body <正文>] [--level info|warn|error] [--sound] [--json]");
                case "--show": return "--show [--json]";
                case "--quit": return "--quit [--json]";
                default: return null;
            }
        }

        internal static string HelpText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(AppInfo.DisplayTitle + " " + AppInfo.Version);
            sb.AppendLine();
            sb.AppendLine(string.Format(L.T("用法：{0} [命令] [参数]"), ExeName()));
            sb.AppendLine();

            sb.AppendLine(L.T("控制正在运行的 ParaDesk（转发给主实例，在分身桌面里也能用）："));
            Item(sb, "--status [--json]",
                L.T("查看当前状态。--json 输出机器可读的状态快照；没有在运行时退出码为 3。"));
            Item(sb, L.T("--start [--profile <方案名>] [--wait]"),
                L.T("启动或重新接入分身桌面。--profile 先切换方案；--wait 等到连上或失败（最多 100 秒）。环境未就绪返回 2，不会弹出管理员授权。"));
            Item(sb, "--detach", L.T("收起画面，子会话继续运行。"));
            Item(sb, "--close", L.T("注销子会话（结束里面的所有程序），不弹确认框。"));
            Item(sb, "--view-only on|off|toggle", L.T("仅查看：禁止用键鼠操作分身桌面。"));
            Item(sb, "--topmost on|off|toggle", L.T("分身桌面窗口置顶。"));
            Item(sb, "--record start|stop|pause|resume|toggle [--target desktop|monitor]",
                L.T("录制。start 默认只录分身桌面窗口（画面没打开时返回 1）；要录显示器请加 --target monitor。"));
            Item(sb, L.T("--screenshot [<路径>]"),
                L.T("截取分身桌面画面并输出文件路径（画面没打开时返回 1）。路径可以相对当前目录，省略则存到录制输出目录；给出文件路径时覆盖同名文件，总是保存为 PNG。"));
            Item(sb, L.T("--notify [--title <标题>] [--body <正文>] [--level info|warn|error] [--sound]"),
                L.T("弹出提醒。正文也可以直接写在命令后面。"));
            Item(sb, "--show", L.T("唤起主窗口。"));
            Item(sb, "--quit", L.T("退出程序并等它真正退出（最多 60 秒）：录制先收尾，分身桌面只收起、子会话保留，不弹确认框。"));
            sb.AppendLine(L.T("  以上命令都可以加 --json，输出机器可读的 JSON（纯 ASCII）。"));
            sb.AppendLine();

            sb.AppendLine(L.T("本地执行（不需要主实例）："));
            Item(sb, "--probe [--json]", L.T("只读环境自检。就绪返回 0，未就绪返回 2。"));
            Item(sb, L.T("--diagbundle [<路径>]"), L.T("生成诊断包（zip），报告问题时附上。"));
            Item(sb, "--logictest", L.T("纯逻辑自检，几秒跑完。"));
            Item(sb, "--i18ntest", L.T("本地化字典自检。"));
            Item(sb, "--dlgtest", L.T("子会话对话框守护自检。"));
            Item(sb, L.T("--rectest [秒数] [--monitor N]"),
                L.T("录屏自检：真实录制一块显示器一段时间（默认主显示器、10 秒，可设 3~120 秒）。--monitor N 指定第 N 块显示器（从 1 起，与界面上的「显示器 N」一致）。"));
            Item(sb, "--contenttest", L.T("录制内容自检：录一个已知画面并逐帧核对颜色。"));
            Item(sb, "--spike", L.T("动态分辨率技术验证（开发用，会真实连接一次子会话）。"));
            Item(sb, "--dumpres", L.T("导出 WPF 资源键（开发用）。"));
            Item(sb, "--help, -h, -?", L.T("显示本帮助。"));
            Item(sb, "--version", L.T("显示版本号。"));
            sb.AppendLine();

            sb.AppendLine(L.T("界面启动参数："));
            Item(sb, L.T("（不带参数）"), L.T("打开主窗口；已经在运行则把它唤到前台。"));
            Item(sb, "--minimized", L.T("启动后只留在系统托盘（开机自启用）。"));
            Item(sb, "--autostart", L.T("由开机自启项传入：在分身桌面里、或已经在运行时静默退出。"));
            Item(sb, "--restart <pid>", L.T("先等进程 <pid> 退出再启动（切换语言时内部使用）。"));
            sb.AppendLine();

            sb.AppendLine(L.T("退出码："));
            sb.AppendLine("  0   " + L.T("成功"));
            sb.AppendLine("  1   " + L.T("执行了但失败（原因见输出）"));
            sb.AppendLine("  2   " + L.T("环境未就绪或系统不支持"));
            sb.AppendLine("  3   " + L.T("ParaDesk 没有在运行"));
            sb.AppendLine("  64  " + L.T("参数写错了"));
            sb.AppendLine();

            sb.AppendLine(L.T("参数名不区分大小写。"));
            sb.AppendLine(L.T("这是窗口程序：cmd 里直接运行不会等它结束，要拿退出码请用 start /wait；PowerShell 里把输出重定向或接管道即可。"));
            return sb.ToString();
        }

        private static void Item(StringBuilder sb, string usage, string description)
        {
            sb.Append("  ").AppendLine(usage);
            sb.Append("      ").AppendLine(description);
        }

        private static string ExeName()
        {
            try
            {
                string n = Path.GetFileName(AppInfo.ExecutablePath);
                if (!string.IsNullOrEmpty(n)) return n;
            }
            catch (Exception ex) { Log.Debug("取程序文件名失败: " + ex.Message); }
            return AppInfo.ProductName + ".exe";
        }

        private static void ShowBox(string text, MessageBoxIcon icon)
        {
            try { MessageBox.Show(text, AppInfo.DisplayTitle, MessageBoxButtons.OK, icon); }
            catch (Exception ex) { Log.Debug("显示命令行提示框失败: " + ex.Message); }
        }

        private static bool IsBlank(string s)
        {
            return s == null || s.Trim().Length == 0;
        }

        private static string Lower(string s)
        {
            return (s ?? "").ToLowerInvariant();
        }

        private static int IndexOf(string[] list, string value)
        {
            return Array.IndexOf(list, value);
        }
    }
}
