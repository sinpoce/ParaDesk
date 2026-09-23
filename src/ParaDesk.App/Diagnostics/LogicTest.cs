using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ParaDesk.Core;
using ParaDesk.Recording;

namespace ParaDesk.Diagnostics
{
    internal static class LogicTest
    {
        private static int _pass;
        private static int _fail;
        private static readonly List<string> _failures = new List<string>();

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            _failures.Clear();

            Say("=== 纯逻辑自检 ===");

            Group("ControlRequest 参数解析", TestControlRequest);
            Group("ControlProtocol 编解码", TestControlProtocol);
            Group("SettingsStore 解析与迁移", TestSettings);
            Group("RecordingOptions.Normalize", TestRecordingOptions);
            Group("PerformanceSettings 帧率换算", TestPerformance);
            Group("UpdateChecker 版本比较", TestVersions);
            Group("UpdateChecker 应答解析", TestReleaseParse);
            Group("MonitorNaming 名称查找", TestMonitorNaming);
            Group("DisplayMode.AspectLabel", TestAspect);
            Group("RdpDisconnectReason.Describe", TestDisconnectReason);
            Group("AppSettings 方案挑选（偏好方案回切）", TestProfilePick);
            Group("EnvironmentReport 判据一致", TestEnvironmentReport);
            Group("热键规范化", TestHotkeyNormalize);
            Group("RunKeyStore 启动项解析", TestRunKey);
            Group("启动命令拼接与工作目录", TestStartupCommand);
            Group("诊断包脱敏", TestRedaction);

            Say("");
            Say("通过 " + _pass + " 项，失败 " + _fail + " 项");
            foreach (string f in _failures) Say("  失败: " + f);
            Say(_fail == 0 ? "结果 = 通过" : "结果 = 有问题");
            return _fail == 0 ? ExitCodes.Ok : ExitCodes.Failed;
        }

        private static void Say(string msg)
        {
            Console.WriteLine(msg);
            Log.Info("[logictest] " + msg);
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) _pass++;
            else
            {
                _fail++;
                _failures.Add(name);
            }
            Say((ok ? "  [ OK ] " : "  [FAIL] ") + name +
                (string.IsNullOrEmpty(detail) ? "" : "  —— " + detail));
        }

        private static void CheckQuiet(string name, bool ok, string detailIfFailed)
        {
            Check(name, ok, ok || detailIfFailed == null ? null
                : detailIfFailed.Replace("\r", "\\r").Replace("\n", "\\n"));
        }

        private static void Eq<T>(string name, T expected, T actual)
        {
            bool ok = EqualityComparer<T>.Default.Equals(expected, actual);
            Check(name, ok, ok ? null : "期望 " + Show(expected) + "，实际 " + Show(actual));
        }

        private static string Show(object o)
        {
            if (o == null) return "null";
            var s = o as string;
            return s != null ? "\"" + s + "\"" : o.ToString();
        }

        private static void Group(string title, Action body)
        {
            Say("");
            Say("-- " + title + " --");
            try { body(); }
            catch (Exception ex)
            {
                Check(title + "：执行中抛出异常", false, ex.GetType().Name + ": " + ex.Message);
                Log.Error("[logictest] " + title + " 异常", ex);
            }
        }

        private static ControlRequest Req(params string[] args)
        {
            return new ControlRequest { Command = "test", Args = new List<string>(args) };
        }

        private static bool IsAscii(string s)
        {
            if (s == null) return false;
            foreach (char c in s) if (c >= 0x80) return false;
            return true;
        }

        private static AppSettings Parse(string json, bool withBom)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            if (!withBom) return SettingsStore.FromJsonBytes(body);
            var bytes = new byte[body.Length + 3];
            bytes[0] = 0xEF; bytes[1] = 0xBB; bytes[2] = 0xBF;
            Array.Copy(body, 0, bytes, 3, body.Length);
            return SettingsStore.FromJsonBytes(bytes);
        }

        private static HotkeyBinding FindHotkey(AppSettings s, string action)
        {
            if (s == null || s.Hotkeys == null) return null;
            foreach (var h in s.Hotkeys)
                if (h != null && string.Equals(h.Action, action, StringComparison.Ordinal)) return h;
            return null;
        }

        private static int Sign(int v) { return v < 0 ? -1 : (v > 0 ? 1 : 0); }

        private static void TestControlRequest()
        {
            Eq("Get 忽略大小写（--Title / title）", "T", Req("--Title", "T", "hello").Get("title"));
            Eq("Get 忽略大小写（--title / TITLE）", "T", Req("--title", "T").Get("TITLE"));
            Eq("Get 没有该参数返回 null", (string)null, Req("--title", "T").Get("body"));
            Eq("Get 名字在最后、没有值时返回 null", (string)null, Req("hello", "--title").Get("title"));

            Check("Has 忽略大小写（--JSON / json）", Req("--JSON").Has("json"), null);
            Check("Has 没有该开关返回 false", !Req("--title", "json").Has("json"), null);

            Eq("布尔开关不吞位置参数：[--json on] 的 Positional(0)", "on", Req("--json", "on").Positional(0));
            Eq("布尔开关大小写不敏感：[--JSON on] 的 Positional(0)", "on", Req("--JSON", "on").Positional(0));
            Eq("带值参数吃掉下一个：[--title T hello] 的 Positional(0)", "hello", Req("--title", "T", "hello").Positional(0));
            Eq("混合：[--sound --title X 正文] 的 Positional(0)", "正文",
               Req("--sound", "--title", "X", "正文").Positional(0));
            Eq("[on] 的 Positional(0)", "on", Req("on").Positional(0));
            Eq("[on] 的 Positional(1) 为 null", (string)null, Req("on").Positional(1));
            Eq("[a --level warn b] 的 Positional(1)", "b", Req("a", "--level", "warn", "b").Positional(1));

            var empty = new ControlRequest { Command = "status", Args = null };
            Check("Args 为 null 时 Get/Has/Positional 不抛异常",
                  empty.Get("x") == null && !empty.Has("x") && empty.Positional(0) == null, null);
        }

        private static void TestControlProtocol()
        {
            string esc = ControlProtocol.EscapeNonAscii("任务完成 ok");
            Check("EscapeNonAscii 输出纯 ASCII", IsAscii(esc), esc);
            Eq("EscapeNonAscii 转成 \\uXXXX", "\\u4efb\\u52a1\\u5b8c\\u6210 ok", esc);
            Eq("EscapeNonAscii(null) 为 null", (string)null, ControlProtocol.EscapeNonAscii(null));

            var req = new ControlRequest
            {
                Command = "notify",
                Args = new List<string> { "--title", "Claude Code", "--body", "任务完成：需要你确认 ✓" },
                WorkingDirectory = @"C:\代码\仓库",
            };
            string reqJson = ControlProtocol.ToJson(req);
            Check("ControlRequest.ToJson 输出纯 ASCII", IsAscii(reqJson), reqJson);
            var req2 = ControlProtocol.FromJson<ControlRequest>(reqJson);
            Check("ControlRequest 往返：Command / WorkingDirectory 一致",
                  req2 != null && req2.Command == req.Command && req2.WorkingDirectory == req.WorkingDirectory,
                  req2 == null ? "解析结果为 null" : req2.Command + " / " + req2.WorkingDirectory);
            bool argsSame = req2 != null && req2.Args != null && req2.Args.Count == req.Args.Count;
            if (argsSame)
                for (int i = 0; i < req.Args.Count; i++)
                    if (req.Args[i] != req2.Args[i]) argsSame = false;
            Check("ControlRequest 往返：Args（含中文）一致", argsSame, null);

            var resp = ControlResponse.Success("已截图：C:\\截图\\a.png", "{\"path\":\"C:\\\\截图\\\\a.png\"}");
            string respJson = ControlProtocol.ToJson(resp);
            Check("ControlResponse.ToJson 输出纯 ASCII", IsAscii(respJson), respJson);
            var resp2 = ControlProtocol.FromJson<ControlResponse>(respJson);
            Check("ControlResponse 往返一致（Ok / ExitCode / Message / Json）",
                  resp2 != null && resp2.Ok == resp.Ok && resp2.ExitCode == resp.ExitCode &&
                  resp2.Message == resp.Message && resp2.Json == resp.Json,
                  resp2 == null ? "解析结果为 null" : resp2.Message);

            var fail = ControlResponse.Fail(ExitCodes.NotReady, "环境未就绪");
            var fail2 = ControlProtocol.FromJson<ControlResponse>(ControlProtocol.ToJson(fail));
            Check("ControlResponse.Fail 往返后 Ok=false、ExitCode=2",
                  fail2 != null && !fail2.Ok && fail2.ExitCode == ExitCodes.NotReady, null);

            Check("FromJson 空串返回 null", ControlProtocol.FromJson<ControlRequest>("") == null, null);

            Eq("ExitCodes.Ok == 0", 0, ExitCodes.Ok);
            Eq("ExitCodes.Failed == 1", 1, ExitCodes.Failed);
            Eq("ExitCodes.NotReady == 2", 2, ExitCodes.NotReady);
            Eq("ExitCodes.NotRunning == 3", 3, ExitCodes.NotRunning);
            Eq("ExitCodes.Usage == 64", 64, ExitCodes.Usage);
        }

        private static void TestSettings()
        {
            AppSettings bom = null;
            string bomErr = null;
            try { bom = Parse("{\"language\":\"en\"}", true); }
            catch (Exception ex) { bomErr = ex.GetType().Name + ": " + ex.Message; }
            Check("带 BOM 的设置文件能读", bom != null && bom.Language == "en", bomErr);

            var empty = Parse("{}", false);
            Check("\"{}\" 解析后 Profiles 非空", empty != null && empty.Profiles != null && empty.Profiles.Count > 0, null);
            Check("\"{}\" 解析后 Hotkeys / Recording / Language 有默认值",
                  empty != null && empty.Hotkeys != null && empty.Recording != null && !string.IsNullOrEmpty(empty.Language),
                  null);
            Check("\"{}\" 解析后 AutoReattach 默认为 true", empty != null && empty.AutoReattach, null);
            Check("\"{}\" 解析后 GetActiveProfile 可用", empty != null && empty.GetActiveProfile() != null, null);
            Check("\"{}\" 解析后 CheckUpdates / AutoStartDesktop 默认为 false",
                  empty != null && !empty.CheckUpdates && !empty.AutoStartDesktop, null);
            var off = Parse("{\"autoReattach\":false}", false);
            Check("用户关掉的 AutoReattach 读回后仍为 false", off != null && !off.AutoReattach, null);
            var fresh = AppSettings.CreateDefault();
            Check("CreateDefault：CheckUpdates / AutoStartDesktop 为 false、AutoReattach 为 true",
                  !fresh.CheckUpdates && !fresh.AutoStartDesktop && fresh.AutoReattach, null);
            Eq("DesktopAudioMode.Local == 0", 0, (int)DesktopAudioMode.Local);
            Eq("DesktopAudioMode.Remote == 1", 1, (int)DesktopAudioMode.Remote);
            Eq("DesktopAudioMode.Mute == 2", 2, (int)DesktopAudioMode.Mute);

            const string v1 =
                "{\"schemaVersion\":1,\"language\":\"zh\",\"minimizeToTray\":true," +
                "\"profiles\":[{\"name\":\"A\",\"windowMode\":0,\"resolutionMode\":0,\"customWidth\":1920,\"customHeight\":1080," +
                "\"scalePercent\":0,\"viewOnly\":false,\"alwaysOnTop\":false,\"clipboard\":0}]," +
                "\"activeProfile\":\"A\"," +
                "\"hotkeys\":[" +
                "{\"action\":\"toggleDesktop\",\"modifiers\":3,\"key\":68,\"enabled\":true}," +
                "{\"action\":\"toggleViewOnly\",\"modifiers\":6,\"key\":87,\"enabled\":false}," +
                "{\"action\":\"toggleRecording\",\"modifiers\":3,\"key\":82,\"enabled\":true}]}";
            var old = Parse(v1, false);
            Check("老设置迁移后 AutoReattach == true", old != null && old.AutoReattach, null);

            string[] actions =
            {
                "toggleDesktop", "toggleViewOnly", "toggleRecording", "detach",
                "screenshot", "togglePause", "pushClipboard", "pullClipboard",
            };
            var missing = new List<string>();
            foreach (string a in actions) if (FindHotkey(old, a) == null) missing.Add(a);
            Check("老设置迁移后热键包含全部 8 个动作", missing.Count == 0,
                  missing.Count == 0 ? null : "缺少 " + string.Join(", ", missing.ToArray()));

            var dupes = new List<string>();
            if (old != null && old.Hotkeys != null)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var h in old.Hotkeys)
                    if (h != null && h.Action != null && !seen.Add(h.Action)) dupes.Add(h.Action);
            }
            Check("老设置迁移后没有重复的热键动作", dupes.Count == 0,
                  dupes.Count == 0 ? null : "重复 " + string.Join(", ", dupes.ToArray()));

            var td = FindHotkey(old, "toggleDesktop");
            var tv = FindHotkey(old, "toggleViewOnly");
            var tr = FindHotkey(old, "toggleRecording");
            Check("老设置迁移后旧绑定不变（toggleDesktop Ctrl+Alt+D）",
                  td != null && td.Modifiers == 3 && td.Key == 68 && td.Enabled, null);
            Check("老设置迁移后旧绑定不变（toggleViewOnly 用户改过的 Ctrl+Shift+W、已停用）",
                  tv != null && tv.Modifiers == 6 && tv.Key == 87 && !tv.Enabled, null);
            Check("老设置迁移后旧绑定不变（toggleRecording Ctrl+Alt+R）",
                  tr != null && tr.Modifiers == 3 && tr.Key == 82 && tr.Enabled, null);

            var sc = FindHotkey(old, "screenshot");
            Check("迁移补上的新动作默认不绑定（screenshot Key=0、Enabled=false）",
                  sc != null && sc.Key == 0 && !sc.Enabled, null);
            Check("老设置迁移后方案保持不变", old != null && old.Profiles != null && old.Profiles.Count == 1 &&
                  old.Profiles[0].Name == "A" && old.ActiveProfile == "A", null);
            foreach (string a in new[] { "detach", "togglePause", "pushClipboard", "pullClipboard" })
            {
                var h = FindHotkey(old, a);
                Check("迁移补上的新动作默认不绑定（" + a + " Key=0、Enabled=false）", h != null && h.Key == 0 && !h.Enabled, null);
            }
            Eq("老设置迁移后 SchemaVersion == CurrentSchema", AppSettings.CurrentSchema, old == null ? -1 : old.SchemaVersion);
            Eq("老设置迁移后 PreferredProfile ← ActiveProfile", "A", old == null ? null : old.PreferredProfile);

            const string bad =
                "{\"profiles\":[{\"name\":\"X\",\"windowMode\":99,\"resolutionMode\":99,\"clipboard\":99," +
                "\"desktopAudio\":99,\"scalePercent\":50,\"customWidth\":5,\"customHeight\":99999}]}";
            var b = Parse(bad, false);
            var p = (b != null && b.Profiles != null && b.Profiles.Count > 0) ? b.Profiles[0] : null;
            Check("越界 windowMode 99 被校正", p != null && Enum.IsDefined(typeof(WindowMode), p.WindowMode),
                  p == null ? "没有方案" : p.WindowMode.ToString());
            Check("越界 resolutionMode 99 被校正", p != null && Enum.IsDefined(typeof(ResolutionMode), p.ResolutionMode),
                  p == null ? "没有方案" : p.ResolutionMode.ToString());
            Check("越界 clipboard 99 被校正", p != null && Enum.IsDefined(typeof(ClipboardMode), p.Clipboard),
                  p == null ? "没有方案" : p.Clipboard.ToString());
            Check("越界 desktopAudio 99 被校正", p != null && Enum.IsDefined(typeof(DesktopAudioMode), p.DesktopAudio),
                  p == null ? "没有方案" : p.DesktopAudio.ToString());
            Eq("scalePercent 50 被校正为 0（自动）", 0, p == null ? -1 : p.ScalePercent);
            Check("越界的自定义宽高被校正到 200..8192",
                  p != null && p.CustomWidth >= 200 && p.CustomWidth <= 8192 && p.CustomHeight >= 200 && p.CustomHeight <= 8192,
                  p == null ? "没有方案" : p.CustomWidth + "x" + p.CustomHeight);

            var good = Parse("{\"profiles\":[{\"name\":\"Y\",\"windowMode\":2,\"clipboard\":1,\"scalePercent\":150}]}", false);
            var gp = (good != null && good.Profiles != null && good.Profiles.Count > 0) ? good.Profiles[0] : null;
            Check("合法的 windowMode/clipboard/scalePercent 保持原值",
                  gp != null && gp.WindowMode == WindowMode.Pip && gp.Clipboard == ClipboardMode.Manual && gp.ScalePercent == 150,
                  gp == null ? "没有方案" : gp.WindowMode + "/" + gp.Clipboard + "/" + gp.ScalePercent);
        }

        private static void TestRecordingOptions()
        {
            Eq("FrameRate 0 → 30", 30, NormalizedFrameRate(0));
            Eq("FrameRate 1 → 1（下限放开到 1）", 1, NormalizedFrameRate(1));
            Eq("FrameRate 60 → 60", 60, NormalizedFrameRate(60));
            Eq("FrameRate 300 → 30（非法值回默认，不钳到边界）", 30, NormalizedFrameRate(300));

            var o = RecordingOptions.CreateDefault();
            o.SegmentMinutes = -5;
            o.Normalize();
            Eq("SegmentMinutes -5 → 0", 0, o.SegmentMinutes);

            var c = RecordingOptions.CreateDefault();
            c.OutputFolder = null;
            c.Normalize();
            Check("OutputFolder 为空时回到默认目录", !string.IsNullOrEmpty(c.OutputFolder), null);
        }

        private static int NormalizedFrameRate(int fps)
        {
            var o = RecordingOptions.CreateDefault();
            o.FrameRate = fps;
            o.Normalize();
            return o.FrameRate;
        }

        private static void TestPerformance()
        {
            Eq("FpsToInterval(30) = 0（恢复默认）", 0, PerformanceSettings.FpsToInterval(30));
            Eq("FpsToInterval(24) = 0", 0, PerformanceSettings.FpsToInterval(24));
            Eq("FpsToInterval(60) = 17", 17, PerformanceSettings.FpsToInterval(60));
            Eq("FpsToInterval(120) = 8", 8, PerformanceSettings.FpsToInterval(120));
            Eq("FpsToInterval(144) = 7", 7, PerformanceSettings.FpsToInterval(144));

            Eq("FrameIntervalToFps(null) = 30", 30, PerformanceSettings.FrameIntervalToFps(null));
            Eq("FrameIntervalToFps(0) = 30", 30, PerformanceSettings.FrameIntervalToFps(0));
            Eq("FrameIntervalToFps(15) = 67", 67, PerformanceSettings.FrameIntervalToFps(15));
            Eq("FrameIntervalToFps(17) = 59", 59, PerformanceSettings.FrameIntervalToFps(17));

            Eq("不互逆：60 → 17ms → 读回 59", 59,
               PerformanceSettings.FrameIntervalToFps(PerformanceSettings.FpsToInterval(60)));

            var choices = new List<int> { 30, 60, 144 };
            Eq("ExactChoice(17, [30,60,144]) = 60（按 interval 命中）", 60, PerformanceSettings.ExactChoice(17, choices));
            Eq("ExactChoice(null, [30,60,144]) = 30（未设置即默认档）", 30, PerformanceSettings.ExactChoice(null, choices));
            Eq("ExactChoice(8, [30,60,144]) = -1（找不到不吸附）", -1, PerformanceSettings.ExactChoice(8, choices));
            Eq("ExactChoice(17, null) = -1", -1, PerformanceSettings.ExactChoice(17, null));
            Eq("ExactChoice(8, [30,119,120,125]) = 125", 125,
               PerformanceSettings.ExactChoice(8, new List<int> { 30, 119, 120, 125 }));
            Eq("ExactChoice(8, [30,119,120]) = 120", 120,
               PerformanceSettings.ExactChoice(8, new List<int> { 30, 119, 120 }));
        }

        private static void TestVersions()
        {
            Eq("1.0.0 < 1.0.1", -1, Sign(UpdateChecker.CompareVersions("1.0.0", "1.0.1")));
            Eq("v1.2 == 1.2.0", 0, Sign(UpdateChecker.CompareVersions("v1.2", "1.2.0")));
            Eq("1.10 > 1.9（按数值不按字符串）", 1, Sign(UpdateChecker.CompareVersions("1.10", "1.9")));
            Eq("1.0.0-beta < 1.0.0（预发布低于同号正式版）", -1, Sign(UpdateChecker.CompareVersions("1.0.0-beta", "1.0.0")));
            Eq("1.0.0-beta < 1.0.0-rc", -1, Sign(UpdateChecker.CompareVersions("1.0.0-beta", "1.0.0-rc")));
            Eq("1.0.0-beta.2 < 1.0.0-beta.10", -1, Sign(UpdateChecker.CompareVersions("1.0.0-beta.2", "1.0.0-beta.10")));
            Eq("1.0.1-beta > 1.0.0", 1, Sign(UpdateChecker.CompareVersions("1.0.1-beta", "1.0.0")));
            Eq("2 > 1.9.9", 1, Sign(UpdateChecker.CompareVersions("2", "1.9.9")));
            Eq("V1.0.0 == 1.0（大写 V、位数不同）", 0, Sign(UpdateChecker.CompareVersions("V1.0.0", "1.0")));
            Eq("1.0.0+build.5 == 1.0.0（构建元数据不参与比较）", 0, Sign(UpdateChecker.CompareVersions("1.0.0+build.5", "1.0.0")));
            Check("反对称：compare(a,b) == -compare(b,a)",
                  Sign(UpdateChecker.CompareVersions("1.2.3", "1.10")) == -Sign(UpdateChecker.CompareVersions("1.10", "1.2.3")),
                  null);
            Check("空串与 null 不抛异常", UpdateChecker.CompareVersions(null, "") == 0, null);
        }

        private static void TestReleaseParse()
        {
            string json =
                "{\"url\":\"https://api.github.com/repos/sinpoce/ParaDesk/releases/1\"," +
                "\"html_url\":\"https://github.com/sinpoce/ParaDesk/releases/tag/v99.0.0\"," +
                "\"tag_name\":\"v99.0.0\",\"name\":\"ParaDesk 99\",\"draft\":false,\"prerelease\":false," +
                "\"assets\":[{\"name\":\"ParaDesk.zip\",\"size\":123}],\"body\":\"说明\"}";
            var info = UpdateChecker.ParseRelease(Encoding.UTF8.GetBytes(json), "1.0.0");
            Check("解析 tag_name 并去掉前缀 v", info != null && info.LatestVersion == "99.0.0",
                  info == null ? "结果为 null" : info.LatestVersion);
            Check("新版本 IsNewer = true", info != null && info.IsNewer, null);
            Eq("html_url 原样保留（GitHub 的 https 地址）",
               "https://github.com/sinpoce/ParaDesk/releases/tag/v99.0.0", info == null ? null : info.Url);

            var same = UpdateChecker.ParseRelease(Encoding.UTF8.GetBytes("{\"tag_name\":\"1.0\",\"html_url\":\"x\"}"), "1.0.0");
            Check("同版本 IsNewer = false", same != null && !same.IsNewer, null);
            Eq("非 GitHub 地址退回固定发布页", UpdateChecker.ReleasesPage, same == null ? null : same.Url);

            var evil = UpdateChecker.ParseRelease(
                Encoding.UTF8.GetBytes("{\"tag_name\":\"v2\",\"html_url\":\"file:///C:/Windows/System32/calc.exe\"}"), "1.0.0");
            Eq("file:// 地址退回固定发布页", UpdateChecker.ReleasesPage, evil == null ? null : evil.Url);

            Check("缺 tag_name 返回 null", UpdateChecker.ParseRelease(Encoding.UTF8.GetBytes("{\"html_url\":\"x\"}"), "1.0.0") == null, null);
            Check("不是 JSON 返回 null", UpdateChecker.ParseRelease(Encoding.UTF8.GetBytes("<html>rate limited</html>"), "1.0.0") == null, null);
        }

        private static void TestMonitorNaming()
        {
            var s = AppSettings.CreateDefault();
            s.MonitorNames = new List<MonitorName>
            {
                new MonitorName { Device = @"\\.\DISPLAY2", Name = "Agent Screen" },
            };
            MonitorNaming.Bind(s);
            try
            {
                Eq("FindDeviceByName 忽略大小写", @"\\.\DISPLAY2", MonitorNaming.FindDeviceByName("agent SCREEN"));
                Eq("FindDeviceByName 原样命中", @"\\.\DISPLAY2", MonitorNaming.FindDeviceByName("Agent Screen"));
                Eq("FindDeviceByName 找不到返回 null", (string)null, MonitorNaming.FindDeviceByName("Other"));
                Eq("CustomName 按设备名取", "Agent Screen", MonitorNaming.CustomName(@"\\.\DISPLAY2"));

                Check("Rename 新名字返回 true", MonitorNaming.Rename(@"\\.\DISPLAY1", "Main"), null);
                Eq("Rename 后 CustomName 生效", "Main", MonitorNaming.CustomName(@"\\.\DISPLAY1"));
                Check("Rename 同名返回 false（无改动不落盘）", !MonitorNaming.Rename(@"\\.\DISPLAY1", "Main"), null);
                Check("Rename 空名清除自定义名", MonitorNaming.Rename(@"\\.\DISPLAY1", "") &&
                      MonitorNaming.CustomName(@"\\.\DISPLAY1") == null, null);

                MonitorNaming.Rename(@"\\.\DISPLAY3", new string('x', MonitorNaming.MaxNameLength + 10));
                string longName = MonitorNaming.CustomName(@"\\.\DISPLAY3");
                Check("Rename 超长名字被截断到 MaxNameLength",
                      longName != null && longName.Length <= MonitorNaming.MaxNameLength,
                      longName == null ? "null" : longName.Length.ToString());
            }
            finally
            {
                MonitorNaming.Bind(null);
            }
        }

        private static void TestAspect()
        {
            Eq("1920x1080 → 16:9", "16:9", Aspect(1920, 1080));
            string wide = Aspect(1920, 1200);
            Check("1920x1200 → 8:5 或 16:10", wide == "8:5" || wide == "16:10", "实际 \"" + wide + "\"");
            Eq("1024x768 → 4:3", "4:3", Aspect(1024, 768));
            Eq("1366x768 → 16:9", "16:9", Aspect(1366, 768));
            Eq("0x0 → 空", "", Aspect(0, 0));

            string uw = Aspect(2560, 1080);
            Check("2560x1080 → 空或 21:9（绝不显示 64:27）", uw == "" || uw == "21:9", "实际 \"" + uw + "\"");
        }

        private static string Aspect(int w, int h)
        {
            return new DisplayMode { Width = w, Height = h, Frequency = 60 }.AspectLabel;
        }

        private static void TestDisconnectReason()
        {
            int[][] known =
            {
                new[] { 0x1, 0 },
                new[] { 0x3, 0 },
                new[] { 0x108, 0 },
                new[] { 0x204, 0 },
                new[] { 0x807, 0 },
                new[] { 0x1607, 0 },
                new[] { 0x3, 5 },
            };
            foreach (var k in known)
            {
                string d = Rdp.RdpDisconnectReason.Describe(k[0], k[1]);
                Check("Describe(0x" + k[0].ToString("X") + ", " + k[1] + ") 非空", !string.IsNullOrEmpty(d), d);
            }

            string takeover = Rdp.RdpDisconnectReason.Describe(0x3, 5);
            Eq("ext=5 优先于 discReason=0x3（与 discReason=0 时同一说明）", Rdp.RdpDisconnectReason.Describe(0, 5), takeover);
            Check("ext=5 的说明不同于 discReason=0x3 自身的说明",
                  takeover != Rdp.RdpDisconnectReason.Describe(0x3, 0), takeover);
            Eq("ext=2 优先于 discReason=0x3", Rdp.RdpDisconnectReason.Describe(0, 2), Rdp.RdpDisconnectReason.Describe(0x3, 2));
            Eq<string>("未知码返回 null", null, Rdp.RdpDisconnectReason.Describe(0x9999, 0));
        }

        private static DesktopProfile Prof(string name, string device)
        {
            var p = DesktopProfile.CreateDefault();
            p.Name = name;
            p.MonitorDevice = device;
            return p;
        }

        private static string PickName(AppSettings s, params string[] devices)
        {
            var p = s.PickForCurrentLayout(new List<string>(devices));
            return p == null ? null : p.Name;
        }

        private static void TestProfilePick()
        {
            const string D1 = @"\\.\DISPLAY1", D2 = @"\\.\DISPLAY2";
            var s = AppSettings.CreateDefault();
            s.Profiles = new List<DesktopProfile> { Prof("A", D2), Prof("B", D1), Prof("C", null) };
            s.ActiveProfile = "A";
            s.PreferredProfile = "A";

            Eq("拔掉扩展坞（D2 不在）：回退到目标屏在的方案", "B", PickName(s, D1));
            s.ActiveProfile = "B";
            Eq("插回扩展坞：切回用户亲手选的方案", "A", PickName(s, D1, D2));
            Eq("偏好方案的屏仍不在：保持当前方案", "B", PickName(s, D1));
            s.PreferredProfile = "C";
            Eq("偏好方案不挑屏：总是选它", "C", PickName(s, D1));
            var none = s.PickForCurrentLayout(null);
            Eq("availableDevices 为 null：返回当前方案", "B", none == null ? null : none.Name);

            s.PreferredProfile = "A";
            Check("删除偏好方案成功", s.RemoveProfile("A"), null);
            Eq("删偏好方案后 Preferred 回到当前方案", "B", s.PreferredProfile);
            Check("删除同时是当前 + 偏好的方案成功", s.RemoveProfile("B"), null);
            Eq("删当前方案后 Active 为剩下的第一个", "C", s.ActiveProfile);
            Eq("删当前方案后 Preferred 跟随 Active", "C", s.PreferredProfile);
            Check("只剩一个方案时不能删", !s.RemoveProfile("C"), null);

            var m = Parse("{\"schemaVersion\":1,\"profiles\":[{\"name\":\"A\"},{\"name\":\"B\"}],\"activeProfile\":\"B\"}", false);
            Eq("老文件没有 preferredProfile：迁移后等于 Active", "B", m == null ? null : m.PreferredProfile);
            var d = Parse("{\"profiles\":[{\"name\":\"A\"},{\"name\":\"B\"}],\"activeProfile\":\"B\",\"preferredProfile\":\"Gone\"}", false);
            Eq("preferredProfile 指向已删方案：迁移后等于 Active", "B", d == null ? null : d.PreferredProfile);
        }

        private static void TestEnvironmentReport()
        {
            int mismatch = 0, blockedReady = 0, ready = 0;
            string firstBad = null;
            for (int m = 0; m < 128; m++)
            {
                var r = new EnvironmentReport
                {
                    IsHomeEdition = (m & 1) != 0,
                    InsideChildSession = (m & 2) != 0,
                    RdpControlRegistered = (m & 4) != 0,
                    ChildSessionsEnabled = (m & 8) != 0,
                    RdpListenerEnabled = (m & 16) != 0,
                    TermServiceRunning = (m & 32) != 0,
                    TransportStatus = (m & 64) != 0 ? 1 : 0,
                };
                if (r.ReadyToStart != (r.NextAction == null))
                {
                    mismatch++;
                    if (firstBad == null)
                        firstBad = "组合 " + m + "：ReadyToStart=" + r.ReadyToStart + "，NextAction=" + Show(r.NextAction);
                }
                if (r.Blocked && r.ReadyToStart) blockedReady++;
                if (r.ReadyToStart) ready++;
            }
            Check("128 种组合下 ReadyToStart ⇔ NextAction == null", mismatch == 0, firstBad);
            Check("Blocked 时绝不 ReadyToStart", blockedReady == 0, null);
            Eq("恰好一种组合就绪", 1, ready);
        }

        private static void TestHotkeyNormalize()
        {
            const string json =
                "{\"hotkeys\":[" +
                "{\"action\":\"toggleDesktop\",\"modifiers\":16387,\"key\":131140,\"enabled\":true}," +
                "{\"action\":\"toggleDesktop\",\"modifiers\":1,\"key\":65,\"enabled\":true}," +
                "{\"action\":\"futureAction\",\"modifiers\":3,\"key\":70,\"enabled\":true}," +
                "{\"action\":\"screenshot\",\"modifiers\":3,\"key\":0,\"enabled\":true}," +
                "{\"action\":\"detach\",\"modifiers\":-1,\"key\":300,\"enabled\":true}]}";
            var s = Parse(json, false);
            var list = s == null ? null : s.Hotkeys;

            var first = (list != null && list.Count > 0) ? list[0] : null;
            Check("修饰键剥掉多余位（0x4003 → 3）、键码取低 16 位（0x20044 → 68），且仍排在第一条",
                  first != null && first.Action == "toggleDesktop" && first.Modifiers == 3 && first.Key == 68 && first.Enabled,
                  first == null ? "没有热键" : first.Action + " " + first.Modifiers + "/" + first.Key + "/" + first.Enabled);

            int count = 0;
            if (list != null)
                foreach (var h in list)
                    if (h != null && h.Action == "toggleDesktop") count++;
            Eq("重复的动作只留第一条", 1, count);

            var fut = FindHotkey(s, "futureAction");
            Check("不认识的动作原样保留（可能来自更新的版本）",
                  fut != null && fut.Modifiers == 3 && fut.Key == 70 && fut.Enabled, null);

            var sc = FindHotkey(s, "screenshot");
            Check("Key=0 的绑定一律 Enabled=false", sc != null && sc.Key == 0 && !sc.Enabled, null);

            var dt = FindHotkey(s, "detach");
            Check("负数修饰键归零、超出 255 的键码按未绑定处理",
                  dt != null && dt.Modifiers == 0 && dt.Key == 0 && !dt.Enabled,
                  dt == null ? "没有 detach" : dt.Modifiers + "/" + dt.Key + "/" + dt.Enabled);

            var missing = new List<string>();
            foreach (string a in new[] { "toggleDesktop", "toggleViewOnly", "toggleRecording", "detach",
                                         "screenshot", "togglePause", "pushClipboard", "pullClipboard" })
                if (FindHotkey(s, a) == null) missing.Add(a);
            Check("缺的动作按默认值补齐", missing.Count == 0,
                  missing.Count == 0 ? null : "缺少 " + string.Join(", ", missing.ToArray()));
        }

        private static void TestRunKey()
        {
            const string exePath = @"C:\a b\ParaDesk.exe";
            Eq("BuildCommand 带参数：路径加引号", @"""C:\a b\ParaDesk.exe"" --childagent",
               RunKeyStore.BuildCommand(exePath, "--childagent"));
            Eq("BuildCommand 无参数", @"""C:\a b\ParaDesk.exe""", RunKeyStore.BuildCommand(exePath, null));

            SplitIs("能拆回 BuildCommand 的结果", RunKeyStore.BuildCommand(exePath, "--childagent"), exePath, "--childagent");
            SplitIs("没加引号、含空格：按 .exe 结尾切", @"C:\a b\ParaDesk.exe --minimized", exePath, "--minimized");
            SplitIs("只有 exe（大写扩展名）", @"C:\a\ParaDesk.EXE", @"C:\a\ParaDesk.EXE", "");
            SplitIs("缺右引号：整段当路径", @"""C:\a b\ParaDesk.exe", exePath, "");
            SplitIs("null：两者都为空", null, "", "");

            Check("SamePath 忽略大小写", RunKeyStore.SamePath(@"C:\A\ParaDesk.exe", @"c:\a\paradesk.EXE"), null);
            Check("SamePath 规范化 ..", RunKeyStore.SamePath(@"C:\a\b\..\ParaDesk.exe", @"C:\a\ParaDesk.exe"), null);
            Check("SamePath 不同目录为 false", !RunKeyStore.SamePath(@"C:\a\ParaDesk.exe", @"C:\b\ParaDesk.exe"), null);
            Check("SamePath 空串 / null 为 false",
                  !RunKeyStore.SamePath("", @"C:\a\ParaDesk.exe") && !RunKeyStore.SamePath(@"C:\a\ParaDesk.exe", null), null);
        }

        private static void SplitIs(string name, string command, string exe, string args)
        {
            string e, a;
            RunKeyStore.SplitCommand(command, out e, out a);
            CheckQuiet("SplitCommand " + name, e == exe && a == args, "exe=" + Show(e) + "，args=" + Show(a));
        }

        private static void TestStartupCommand()
        {
            string title = "/c start \"" + AppInfo.ProductName + "\" /D ";
            Eq("盘符根目录保留反斜杠（C: 与 C:\\ 意思不同）", title + @"""C:\"" code .",
               Ui.ChildSessionAgent.BuildStartArguments(@"C:\", "code ."));
            Eq("结尾的反斜杠去掉（否则按 C 运行库规则成了转义引号）", title + @"""C:\repo"" code .",
               Ui.ChildSessionAgent.BuildStartArguments(@"C:\repo\", "code ."));
            Eq("路径里的引号被剥掉", title + @"""C:\ab"" x", Ui.ChildSessionAgent.BuildStartArguments(@"C:\a""b", "x"));
            Eq("含空格与 & 的目录整体加引号", title + @"""C:\my repo & x"" claude",
               Ui.ChildSessionAgent.BuildStartArguments(@"C:\my repo & x\", "claude"));

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string sys = Environment.SystemDirectory;
            Eq("工作目录 null → 用户主目录", home, Ui.ChildSessionAgent.ResolveWorkingDir(null));
            Eq("工作目录只有空白 → 用户主目录", home, Ui.ChildSessionAgent.ResolveWorkingDir("  "));
            Eq("带引号的目录去掉引号", sys, Ui.ChildSessionAgent.ResolveWorkingDir("\"" + sys + "\""));
            Eq("相对路径按用户主目录解释", Path.GetFullPath(Path.Combine(home, "..")),
               Ui.ChildSessionAgent.ResolveWorkingDir(".."));
            Eq("不存在的目录 → 用户主目录", home, Ui.ChildSessionAgent.ResolveWorkingDir(
               Path.Combine(Path.GetTempPath(), "paradesk-logictest-" + Guid.NewGuid().ToString("N"))));
        }

        private static string RedactSettings(string json, out int count)
        {
            return Encoding.UTF8.GetString(DiagBundle.RedactSettingsBytes(Encoding.UTF8.GetBytes(json), out count));
        }

        private static void TestRedaction()
        {
            int n;
            string s1 = RedactSettings(
                "{\"profiles\":[{\"name\":\"开发\",\"startupCommand\":\"set KEY=sk-FAKE1 && claude\"," +
                "\"startupWorkingDir\":\"C:\\\\代码\"}]}", out n);
            CheckQuiet("settings：startupCommand 的值被替换",
                  !s1.Contains("FAKE1") && s1.Contains("\"startupCommand\":\"<redacted"), s1);
            CheckQuiet("settings：其余字段（含中文）原样保留",
                  s1.Contains("\"name\":\"开发\"") && s1.Contains("\"startupWorkingDir\":\"C:\\\\代码\""), s1);
            Eq("settings：计数 1", 1, n);

            string s2 = RedactSettings(
                "{\"startupCommand\" : \"cmd \\/c \\\"FAKE2\\\" \\\\ x\",\"keepAwake\":true}", out n);
            CheckQuiet("settings：值里有转义也整段替换，后面的字段不受影响",
                  !s2.Contains("FAKE2") && s2.EndsWith(",\"keepAwake\":true}", StringComparison.Ordinal) && n == 1, s2);

            string s3 = RedactSettings("{\"startupCommand\":null,\"x\":\"\"}", out n);
            CheckQuiet("settings：null 值原样保留", s3 == "{\"startupCommand\":null,\"x\":\"\"}" && n == 0, s3);

            string s4 = RedactSettings("{\"profiles\":[{\"startupCommand\":\"set KEY=FAKE4", out n);
            CheckQuiet("settings：截断在值中间（损坏留证）也要抹掉", !s4.Contains("FAKE4") && n == 1, s4);

            RedactSettings("[{\"startupCommand\":\"a\"},{\"StartupCommand\":\"b\"},{\"startupCommand\":\"\"}]", out n);
            Eq("settings：多个方案逐个替换（键名不分大小写，空串不算）", 2, n);

            byte[] body = Encoding.UTF8.GetBytes(
                "{\"profiles\":[{\"name\":\"开发\",\"startupCommand\":\"FAKE5\"}],\"activeProfile\":\"开发\"}");
            var withBom = new byte[body.Length + 3];
            withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
            Array.Copy(body, 0, withBom, 3, body.Length);
            byte[] red = DiagBundle.RedactSettingsBytes(withBom, out n);
            Check("settings：BOM 原样保留", red.Length > 3 && red[0] == 0xEF && red[1] == 0xBB && red[2] == 0xBF, null);
            var back = SettingsStore.FromJsonBytes(red);
            var bp = back == null ? null : back.FindProfile("开发");
            CheckQuiet("settings：脱敏后仍能解析，中文方案名不变、命令已替换",
                  bp != null && bp.StartupCommand != null && bp.StartupCommand.StartsWith("<redacted", StringComparison.Ordinal),
                  bp == null ? "解析失败或找不到方案" : bp.StartupCommand);

            const string log =
                "2026-01-01 00:00:00.000  [INFO]  [agent] 已执行启动命令（方案「A」，目录 C:\\x）: set KEY=FAKE6 && claude\r\n" +
                "2026-01-01 00:00:01.000  [ERROR]  [agent] 执行启动命令失败: FAKE7 :: " +
                "System.ComponentModel.Win32Exception (0x80004005): 系统找不到指定的文件。\r\n" +
                "   在 System.Diagnostics.Process.Start()\r\n" +
                "2026-01-01 00:00:02.000  [INFO]  方案「B」的登录后自动运行已改为: FAKE8（工作目录 D:\\y）\r\n" +
                "2026-01-01 00:00:03.000  [INFO]  普通的一行\r\n";
            string outLog = Encoding.UTF8.GetString(DiagBundle.RedactLogBytes(Encoding.UTF8.GetBytes(log), out n));
            CheckQuiet("日志：三种记下命令全文的行都被抹掉",
                  !outLog.Contains("FAKE6") && !outLog.Contains("FAKE7") && !outLog.Contains("FAKE8"), outLog);
            Eq("日志：计数 3", 3, n);
            CheckQuiet("日志：失败行保留异常说明",
                  outLog.Contains(" :: System.ComponentModel.Win32Exception (0x80004005): 系统找不到指定的文件。\r\n"), outLog);
            Check("日志：其它行原样、行数不变",
                  outLog.Contains("  [INFO]  普通的一行\r\n") && outLog.Contains("   在 System.Diagnostics.Process.Start()\r\n") &&
                  outLog.Split('\n').Length == log.Split('\n').Length, null);
        }
    }
}
