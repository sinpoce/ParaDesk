using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace ParaDesk.Core
{
    /// <summary>
    /// 轻量本地化。
    ///
    /// 不用 resx 卫星程序集：那套要为运行时切换额外引框架，而本程序只有两种语言、
    /// 字符串量可控。这里用内嵌字典 + 运行时查表，切换语言无需重启，
    /// 也不给发行包增加任何文件。将来语言变多再迁 resx 不迟。
    /// </summary>
    internal static partial class L
    {
        private static Dictionary<string, string> _map;
        private static string _lang = "zh";

        /// <summary>当前语言：zh 或 en。</summary>
        public static string Current { get { return _lang; } }

        public static event EventHandler Changed;

        private static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;

        /// <summary>lang 传 "auto" 时跟随系统语言。</summary>
        public static void Apply(string lang)
        {
            Apply(lang, true);
        }

        public static void Apply(string lang, bool writeLog)
        {
            string resolved = lang;
            if (string.IsNullOrEmpty(lang) || lang == "auto")
            {
                try
                {
                    var name = SystemUiCulture.TwoLetterISOLanguageName;
                    resolved = name == "zh" ? "zh" : "en";
                }
                catch { resolved = "zh"; }
            }
            if (resolved != "zh" && resolved != "en") resolved = "zh";

            _lang = resolved;
            _map = resolved == "en" ? BuildEnglish() : null;   // 中文即键本身，无需查表

            try
            {
                var ci = new CultureInfo(resolved == "en" ? "en-US" : "zh-CN");
                Thread.CurrentThread.CurrentUICulture = ci;
            }
            catch { }

            if (writeLog) Log.Info("界面语言 => " + resolved);
            var h = Changed;
            if (h != null) h(null, EventArgs.Empty);
        }

        /// <summary>
        /// 取译文。键就是中文原文——这样未翻译的地方会自然回退到中文，
        /// 而不是显示一个开发者才看得懂的键名。
        /// </summary>
        public static string T(string zh)
        {
            if (_map == null || zh == null) return zh;
            string v;
            return _map.TryGetValue(zh, out v) ? v : zh;
        }

        /// <summary>
        /// 同一个中文键被赋了两种不同译文的记录（"键 -> A | B"）。
        /// 后写覆盖先写，界面上就会莫名其妙显示另一处的译法——
        /// 这种冲突只在英文界面下暴露，中文测试永远发现不了，所以要能查。
        /// </summary>
        public static List<string> FindConflicts()
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            var conflicts = new List<string>();
            BuildEnglish(delegate(string zh, string en)
            {
                string prev;
                if (seen.TryGetValue(zh, out prev) && !string.Equals(prev, en, StringComparison.Ordinal))
                    conflicts.Add(zh + " -> " + prev + " | " + en);
                seen[zh] = en;
            });
            return conflicts;
        }

        /// <summary>取字典快照，供占位符一致性等静态校验使用。</summary>
        public static Dictionary<string, string> Snapshot()
        {
            return BuildEnglish();
        }

        internal static void EnumerateEntries(Action<string, string> visitor)
        {
            if (visitor != null) BuildEnglish(visitor);
        }

        private static Dictionary<string, string> BuildEnglish()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            BuildEnglish(delegate(string zh, string en) { d[zh] = en; });
            return d;
        }

        static partial void AddCli(Action<string, string> Add);
        static partial void AddAppContext(Action<string, string> Add);
        static partial void AddRdp(Action<string, string> Add);
        static partial void AddTray(Action<string, string> Add);
        static partial void AddRecording(Action<string, string> Add);
        static partial void AddMainWindow(Action<string, string> Add);
        static partial void AddWpfMisc(Action<string, string> Add);
        static partial void AddDiag(Action<string, string> Add);
        static partial void AddCore(Action<string, string> Add);
        static partial void AddSetup(Action<string, string> Add);

        private static void BuildEnglish(Action<string, string> Add)
        {
            AddCli(Add);
            AddAppContext(Add);
            AddRdp(Add);
            AddTray(Add);
            AddRecording(Add);
            AddMainWindow(Add);
            AddWpfMisc(Add);
            AddDiag(Add);
            AddCore(Add);
            AddSetup(Add);
            BuildEnglishBase(Add);
        }

        private static void BuildEnglishBase(Action<string, string> Add)
        {
                // 导航
                Add("桌面", "Desktop");
                Add("画面", "Display");
                Add("输入与共享", "Input & sharing");
                Add("录制", "Record");
                Add("热键", "Hotkeys");
                Add("设置", "Settings");
                Add("诊断", "Diagnostics");
                Add("关于", "About");

                // 分区标题与页面副标题
                Add("其他桌面", "Other desktops");
                Add("录制设置", "Recording settings");
                Add("环境检查", "Environment check");
                Add("日志与反馈", "Logs & feedback");
                Add("运行日志", "Activity log");
                Add("使用引导", "Guided setup");
                Add("键鼠归谁、以及与主桌面之间怎么协作", "Who owns input, and how the two desktops share");
                Add("在任何程序里都能用，不必先切回本窗口", "Works from any app — no need to come back here");
                Add("出问题时先看这里", "Start here when something goes wrong");
                Add("环境正常", "Environment OK");
                Add("有未通过项", "Some checks failed");

                // 状态
                Add("分身桌面运行中", "Desktop running");
                Add("已收起（后台运行中）", "Hidden (running in background)");
                Add("尚未就绪", "Not ready");
                Add("未启动", "Not started");
                Add("正在连接…", "Connecting…");
                Add("正在重连…", "Reconnecting…");
                Add("运行中", "Running");
                Add("连接中", "Connecting");
                Add("重连中", "Reconnecting");
                Add("后台", "Background");
                Add("需配置", "Setup needed");
                Add("就绪", "Ready");
                Add("运行时长", "Uptime");
                Add("分辨率", "Resolution");
                Add("显示位置", "Location");
                Add("会话 ID", "Session ID");

                // 主操作
                Add("首次配置", "First-time setup");
                Add("启动桌面", "Start desktop");
                Add("收起桌面", "Hide desktop");
                Add("重新接入", "Reattach");
                Add("关闭桌面", "Close desktop");
                Add("识别屏幕", "Identify");
                Add("方案", "Profile");

                // 显示设置
                Add("窗口模式", "Window mode");
                Add("缩放", "Scale");
                Add("仅查看", "View only");
                Add("窗口置顶", "Always on top");
                Add("剪贴板", "Clipboard");
                Add("浏览器隔离", "Browser isolation");
                Add("沙盒桌面", "Sandbox desktop");

                // 录制
                Add("屏幕录制", "Screen recording");
                Add("录制目标", "Capture target");
                Add("音频来源", "Audio source");
                Add("画质", "Quality");
                Add("录制鼠标指针", "Capture cursor");
                Add("保存位置", "Save location");
                Add("开始录制", "Start recording");
                Add("停止录制", "Stop recording");
                Add("正在录制", "Recording");
                Add("未在录制", "Not recording");
                Add("不录音频", "No audio");
                Add("系统声音", "System audio");
                Add("麦克风", "Microphone");
                Add("系统声音 + 麦克风", "System audio + microphone");

                // 性能与设置
                Add("帧率上限", "Frame rate cap");
                Add("全局热键", "Global hotkeys");
                Add("开机自动启动", "Run at startup");
                Add("关闭主窗口时最小化到托盘", "Minimize to tray on close");
                Add("登录凭据", "Credentials");
                Add("打开文件夹", "Open folder");
                Add("更改…", "Change…");
                Add("管理…", "Manage…");
                Add("语言", "Language");

                // 主界面按钮与标签
                Add("正在配置…", "Setting up…");
                Add("重新配置", "Run setup again");
                Add("启动桌面", "Start desktop");
                Add("暂停", "Pause");
                Add("继续", "Resume");
                Add("截图", "Screenshot");
                Add("刷新目标列表", "Refresh target list");
                Add("另存为新方案", "Save as new profile");
                Add("删除当前方案", "Delete this profile");
                Add("查看日志", "View logs");
                Add("打开日志文件夹", "Open log folder");
                Add("重新查看引导", "Show the guide again");
                Add("创建快捷方式…", "Create shortcut…");
                Add("启动沙盒桌面", "Start sandbox desktop");
                Add("沙盒运行中", "Sandbox running");
                Add("发送到分身桌面", "Send to desktop");
                Add("从分身桌面取回", "Get from desktop");
                Add("上一步", "Back");
                Add("下一步", "Next");
                Add("跳过", "Skip");
                Add("开始使用", "Get started");
                Add("确定", "OK");
                Add("取消", "Cancel");
                Add("关闭", "Close");
                Add("复制全部", "Copy all");
                Add("清空日志", "Clear log");
                Add("自动滚动", "Auto-scroll");
                Add("筛选关键字…", "Filter…");
                Add("全部级别", "All levels");
                Add("仅 ERROR", "Errors only");
                Add("共享（默认）", "Shared (default)");
                Add("手动同步", "Manual sync");
                Add("不共享", "Not shared");
                Add("跟随显示器", "Match monitor");
                Add("全屏钉在该显示器", "Full screen on that monitor");
                Add("可缩放窗口", "Resizable window");
                Add("悬浮小窗（画中画）", "Floating window (PiP)");
                Add("自动（跟随显示器）", "Auto (match monitor)");
                Add("跟随系统 / Auto", "System default / Auto");
                Add("简体中文", "简体中文");
                Add("English", "English");

                // 托盘
                Add("打开主界面(&O)", "&Open");
                Add("启动桌面(&S)", "&Start desktop");
                Add("重新接入桌面(&S)", "Re&attach desktop");
                Add("收起（后台保持运行）(&H)", "&Hide (keep running)");
                Add("关闭桌面(&C)", "&Close desktop");
                Add("仅查看（不响应我的键鼠）(&V)", "&View only");
                Add("窗口置顶(&T)", "Always on &top");
                Add("开始录制(&R)", "Start &recording");
                Add("停止录制(&R)", "Stop &recording");
                Add("识别屏幕(&I)", "&Identify screens");
                Add("退出(&X)", "E&xit");

                // ---- 正文与说明文字 ----
                // 只翻标题不翻正文，比不翻还糟：界面会变成中英夹杂。
                // 下面按页面顺序收全部长句，新增文案时也应同步补在这里。

                Add("ParaDesk 分身桌面", "ParaDesk");
                Add("正在检查环境…", "Checking environment…");
                Add("在每块显示器上显示编号", "Show a number on each monitor");

                // 桌面
                Add("系统同时只允许一个分身桌面；需要更多隔离桌面时可用 Windows 沙盒。",
                    "Windows allows only one parallel desktop at a time. For more isolated desktops, use Windows Sandbox.");

                // 画面
                Add("连接中即时生效，无需断开重连；选项来自目标显示器实际支持的模式",
                    "Applies instantly while connected — no reconnect. Options come from the modes the target monitor actually supports");

                // 输入与共享
                Add("你的键鼠不会作用到分身桌面；AI 注入的操作不受影响",
                    "Your keyboard and mouse won't reach the parallel desktop; input injected by AI is unaffected");
                Add("系统默认与主桌面共享；手动模式可避免两边互相覆盖",
                    "Shared with the main desktop by default; manual mode keeps the two sides from overwriting each other");

                // 录制
                Add("单独录某个分身桌面窗口，或整块显示器",
                    "Record a single parallel-desktop window, or a whole monitor");
                Add("选择目标后点「开始录制」。", "Pick a target, then press Start recording.");
                Add("系统声音或麦克风", "System audio or microphone");
                Add("关闭后画面中不出现鼠标", "Turn off to keep the pointer out of the video");
                Add("按窗口录制才能拍到分身桌面",
                    "Only window capture can record the parallel desktop");

                // 热键
                Add("显示 / 收起分身桌面", "Show / hide the parallel desktop");
                Add("切换「仅查看」", "Toggle view only");
                Add("开始 / 停止录制", "Start / stop recording");
                Add("点击输入框后直接按组合键", "Click a field, then press the combination");
                Add("必须搭配 Ctrl / Alt / Shift / Win；按 Backspace 可清除。若提示注册失败，说明该组合已被其它程序占用。",
                    "Must include Ctrl / Alt / Shift / Win; press Backspace to clear. A registration failure means another program already owns that combination.");

                // 设置

                // 诊断
                Add("INFO 以上", "INFO and above");
                Add("WARN 以上", "WARN and above");

                // 关于
                Add("在闲置的显示器上开出第二个桌面，拥有独立的鼠标键盘，与主桌面互不干扰；同时共用同一账户与全部文件，软件、登录状态和工作进度完全通用。",
                    "Open a second desktop on a spare monitor with its own mouse and keyboard, fully out of the way of your main one — while sharing the same account and all your files, so apps, sign-ins and work in progress carry straight over.");

                // 首次运行向导
                Add("欢迎使用 ParaDesk", "Welcome to ParaDesk");
                Add("给你的电脑开一个分身", "Give your PC a second self");
                Add("ParaDesk 会在你选定的显示器上开出第二个 Windows 桌面。它有自己独立的鼠标和键盘——AI 或另一个人在那边操作时，你主屏的键鼠完全不受影响。",
                    "ParaDesk opens a second Windows desktop on the monitor you choose. It has its own mouse and keyboard — while AI or another person works over there, your main screen's input is completely unaffected.");
                Add("它和虚拟机不一样", "It is not a virtual machine");
                Add("分身桌面用的是你同一个 Windows 账户：软件、文件、浏览器登录状态、AI 的记忆和工作进度全部通用，不需要来回传文件。代价是它不做安全隔离——那边能访问你所有文件，和你自己在主桌面操作的权限一样。",
                    "The parallel desktop runs under your same Windows account: apps, files, browser sign-ins, and your AI's memory and work in progress all carry over, with no copying files back and forth. The trade-off is that it provides no security isolation — that side reaches every file you can, with exactly your permissions.");
                Add("需要 Windows 专业版及以上", "Requires Windows Pro or higher");
                Add("选择显示位置", "Choose where it appears");
                Add("点一下要让分身桌面出现的那块屏幕。分不清哪块是哪块时，点“识别屏幕”。",
                    "Click the screen where the parallel desktop should appear. If you can't tell them apart, press Identify.");
                Add("准备就绪", "Ready to go");
                Add("接下来就可以启动分身桌面了。几个值得先知道的点：",
                    "You can start the parallel desktop now. A few things worth knowing first:");
                Add("常用操作", "Everyday use");
                Add("必须知道的几点限制", "Limits worth knowing");
                Add("• 系统同时只允许一个分身桌面，这是 Windows 的限制\n• 剪贴板默认与主桌面共享；同一份浏览器配置不能两边同开，可在“显示”页一键生成独立配置的快捷方式\n• 重启电脑前请先“关闭桌面”",
                    "• Only one parallel desktop can exist at a time — a Windows limitation\n• The clipboard is shared with the main desktop by default; one browser profile can't run on both sides, but Input & sharing can create a shortcut with its own profile\n• Always Close desktop before restarting the PC");

                // ---- 运行中由代码写入的提示（调用处已包 L.T）----

                Add("版本 ", "Version ");
                Add("跟随", "Match");
                Add("所有前置条件均已满足。", "Every prerequisite is met.");
                Add("系统声音录的是本机所有播放的声音（含分身桌面）；麦克风需在隐私设置中允许桌面应用访问。",
                    "System audio captures everything this PC plays, including the parallel desktop. The microphone requires desktop apps to be allowed access in Privacy settings.");
                Add("无法开始录制", "Couldn't start recording");
                Add("录制已开始", "Recording started");
                Add("文件将保存到 ", "The file will be saved to ");
                Add("截图失败", "Screenshot failed");
                Add("已截图", "Screenshot saved");
                Add("录制完成", "Recording finished");
                Add("录制未成功", "Recording did not finish");
                Add("有热键注册失败", "A hotkey failed to register");
                Add("热键已生效", "Hotkeys are active");
                Add("现在可以在任何程序里使用这些组合键。",
                    "You can now use these combinations from any app.");

                // 向导
                Add("第 {0} / {1} 步", "Step {0} of {1}");
                Add("这台电脑无法使用分身桌面", "This PC can't run a parallel desktop");
                Add("全部就绪", "All set");
                Add("无需额外配置，可以直接跳到选择显示位置。",
                    "Nothing to configure — skip straight to choosing where it appears.");
                Add("有几项需要配置", "A few things need configuring");
                Add("配置完成", "Setup complete");
                Add("需要重启电脑", "A restart is required");

                // 日志窗口
                Add("（暂无日志）", "(no log yet)");
                Add("读取日志失败：", "Couldn't read the log: ");
                Add("已复制到剪贴板", "Copied to clipboard");

                // 热键录制框
                Add("未设置", "Not set");
                Add("请至少配合 Ctrl / Alt / Shift / Win",
                    "Add at least Ctrl / Alt / Shift / Win");
                Add("点击后按下组合键；按 Backspace 或 Delete 清除",
                    "Click, then press the combination; Backspace or Delete clears it");

                // 凭据对话框
                Add("账户", "Account");
                Add("密码", "Password");
                Add("清除已保存", "Clear saved");

                // ---- 状态、错误与提示 ----
                // 带 {0} 的是 string.Format 模板；译文里的占位符必须一个不少，
                // 顺序可以调（英文语序常和中文不同），但个数错了会在运行时抛异常。

                // 显示器与浏览器
                Add("显示器", "Monitor");
                Add("窗口", "Window");
                Add("主屏", "primary");
                Add("（主屏）", " (primary)");
                Add("显示器 {0}", "Monitor {0}");
                Add("显示器 {0}: {1}  {2}×{3}{4}", "Monitor {0}: {1}  {2}×{3}{4}");
                Add("未知显示器", "Unknown monitor");
                Add("未检测到显示器", "No monitors detected");
                Add("未检测到可用显示器。", "No usable monitor was found.");
                Add("（分身桌面）", " (parallel desktop)");
                Add("系统不支持创建快捷方式。", "This system can't create shortcuts.");
                Add("在分身桌面里使用的独立浏览器配置，可与主桌面同时运行",
                    "A separate browser profile for the parallel desktop, able to run alongside the main one");

                // 环境状态
                Add("Windows 家庭版不支持子会话，请使用专业版及以上，或改用虚拟机桌面。",
                    "Windows Home doesn't support child sessions. Use Pro or higher, or a virtual machine instead.");
                Add("请在主桌面上运行本程序（当前似乎在分身桌面内）。",
                    "Run this program on the main desktop — it looks like you're inside the parallel desktop.");
                Add("系统缺少远程桌面客户端控件，无法运行。",
                    "The Remote Desktop client control is missing, so this can't run.");
                Add("配置已写入，但子会话监听器需重启电脑后才会启动。请重启电脑。",
                    "The settings are written, but the child-session listener only starts after a restart. Please restart the PC.");
                Add("环境尚未就绪。", "The environment isn't ready yet.");
                Add("环境检查未通过。", "The environment check didn't pass.");
                Add("通道可用", "Channel available");
                Add("拒绝访问", "Access denied");
                Add("错误码 0x", "Error code 0x");
                Add("Windows 版本", "Windows edition");
                Add("家庭版不支持子会话功能", "Home edition has no child sessions");
                Add("远程桌面客户端控件", "Remote Desktop client control");
                Add("已就绪", "Ready");
                Add("系统组件缺失，无法运行", "System component missing — can't run");
                Add("子会话功能", "Child sessions");
                Add("远程桌面监听器", "Remote Desktop listener");
                Add("远程桌面服务", "Remote Desktop service");
                Add("未运行", "Not running");
                Add("子会话通道", "Child-session channel");
                Add("可用", "Available");
                Add("屏幕捕获（录制）", "Screen capture (recording)");
                Add("支持", "Supported");
                Add("需要 Windows 10 1903 或更高版本", "Requires Windows 10 1903 or later");
                Add("详见上方列表。", "See the list above.");
                Add("已启用", "Enabled");
                Add("未启用", "Disabled");
                Add("配置完成后需重启电脑一次才会就绪", "Ready after one restart following setup");
                Add("Windows 家庭版不含子会话功能，需要专业版及以上。分身桌面与沙盒桌面都用不了，录制和截图仍可正常使用。",
                    "Windows Home has no child sessions — Pro or higher is required. Neither the parallel desktop nor the sandbox desktop is available; recording and screenshots still work.");
                Add("系统 {0} (build {1})　子会话 {2}　监听器 {3}",
                    "{0} (build {1})　Child sessions {2}　Listener {3}");

                // 沙盒
                Add("Windows 家庭版不含沙盒功能，需专业版及以上。",
                    "Windows Home has no Sandbox — Pro or higher is required.");
                Add("系统未启用「Windows 沙盒」功能，可在「启用或关闭 Windows 功能」中开启（需重启）。",
                    "Windows Sandbox isn't enabled. Turn it on in “Turn Windows features on or off” (needs a restart).");
                Add("启动沙盒失败：", "Couldn't start the sandbox: ");
                Add("沙盒桌面已在运行。系统同时只允许一个沙盒实例。",
                    "The sandbox desktop is already running — Windows allows only one instance.");
                Add("沙盒桌面是即抛环境：关闭后里面的一切都会丢失，且与主桌面不共享文件。",
                    "The sandbox desktop is disposable: everything inside is lost when it closes, and it shares no files with your main desktop.");
                Add("已为你映射共享文件夹（可读写）：", "A shared folder has been mapped (read/write):");
                Add("若你要的是「AI 接着我的工作继续干」，请用分身桌面而不是沙盒。",
                    "If what you want is “let the AI carry on my work”, use the parallel desktop, not the sandbox.");
                Add("确定启动吗？", "Start it?");
                Add("沙盒桌面正在启动，首次启动需要一点时间。",
                    "The sandbox desktop is starting — the first launch takes a moment.");

                // 连接
                Add("正在连接分身桌面……", "Connecting to the parallel desktop…");
                Add("首次连接会为你的账户建立第二个登录，可能需要一两分钟。",
                    "The first connection creates a second sign-in for your account, which can take a minute or two.");
                Add("启动失败：", "Couldn't start: ");
                Add("连接失败", "Connection failed");
                Add("连接中断，正在自动重连…（第 {0}/{1} 次）",
                    "Connection lost, reconnecting… (attempt {0} of {1})");
                Add("连接中断，正在自动恢复。分身桌面里的程序不受影响。",
                    "The connection dropped and is recovering automatically. Programs inside the parallel desktop are unaffected.");
                Add("在里面打开终端即可开始工作；你的主屏键鼠不受影响。",
                    "Open a terminal inside to get going; your main screen's keyboard and mouse are unaffected.");
                Add("分身桌面里的程序仍在运行。点「重新接入」可再次看到画面。",
                    "Programs inside are still running. Press Reattach to see the screen again.");
                Add("分身桌面被另一个连接接管了。系统同一时间只允许一个子会话。",
                    "Another connection took over the parallel desktop. Windows allows only one child session at a time.");
                Add("分身桌面已注销。", "The parallel desktop was signed out.");
                Add("本地主动断开。", "Disconnected locally.");
                Add("已在分身桌面内注销。", "Signed out from inside the parallel desktop.");
                Add("会话被系统结束。", "The session was ended by the system.");
                Add("连接超时。", "The connection timed out.");
                Add("安全数据无效。", "Invalid security data.");
                Add("本地回环的证书校验失败，子会话无法建立。",
                    "Loopback certificate validation failed, so the child session can't be created.");
                Add("登录失败：账户凭据被拒绝。", "Sign-in failed: the account credentials were rejected.");
                Add("连接被关闭。", "The connection was closed.");
                Add("凭据委派被组策略禁止，无法自动登录分身桌面。",
                    "Group Policy blocks credential delegation, so the parallel desktop can't sign in automatically.");
                Add("未能连接到分身桌面。常见原因：",
                    "Couldn't connect to the parallel desktop. Common causes:");
                Add("2. 账户无密码或仅用 Windows Hello —— 请为账户设置密码；",
                    "2. The account has no password, or uses Windows Hello only — set a password;");
                Add("3. 企业策略禁用了凭据委派；", "3. Enterprise policy disables credential delegation;");
                Add("4. 「手机连接」等组件与子会话冲突。",
                    "4. Components such as Phone Link conflict with child sessions.");
                Add("一切就绪，点「启动桌面」即可在选定显示器上开出分身桌面。",
                    "Everything is ready — press Start desktop to open the parallel desktop on the monitor you picked.");
                Add("分身桌面仍在运行", "The parallel desktop is still running");
                Add("正在关闭分身桌面，请稍候…", "Closing the parallel desktop, please wait…");
                Add("关闭桌面会结束分身桌面里正在运行的所有程序（相当于注销登录）。",
                    "Closing the desktop ends every program running inside it — the same as signing out.");
                Add("若只想收起画面、让里面的程序继续跑，请选择「收起」。",
                    "To just put the view away and let those programs keep running, choose Hide.");
                Add("确定关闭吗？", "Close it?");
                Add("关闭桌面失败，详见日志。", "Couldn't close the desktop — see the log.");
                Add("分身桌面正在运行。退出 {0} 只会收起画面，",
                    "The parallel desktop is running. Quitting {0} only puts the view away —");
                Add("桌面里的程序会继续在后台运行。",
                    "the programs inside keep running in the background.");
                Add("确定退出吗？", "Quit anyway?");
                Add("分身桌面", "Parallel desktop");
                Add("已开启仅查看：你的键鼠不会作用到分身桌面。",
                    "View only is on: your keyboard and mouse won't reach the parallel desktop.");
                Add("已关闭仅查看。", "View only is off.");
                Add("显示器已变化，自动套用方案「{0}」。",
                    "The monitor layout changed — profile “{0}” was applied automatically.");
                Add("分身桌面已移动到 {0}。", "The parallel desktop moved to {0}.");
                Add("界面初始化失败，详见日志：", "The interface failed to start — see the log: ");
                Add("日志：", "Log: ");

                // 录制与截图
                Add("已经在录制中。", "Already recording.");
                Add("未选择录制目标。", "No capture target selected.");
                Add("未选择截图目标。", "No screenshot target selected.");
                Add("当前系统不支持屏幕捕获（需要 Windows 10 1903 或更高版本）。",
                    "This system doesn't support screen capture (Windows 10 1903 or later is required).");
                Add("无法捕获该目标（窗口可能已关闭）。",
                    "Can't capture that target — the window may have closed.");
                Add("显卡设备初始化失败，无法录制。",
                    "The graphics device failed to initialize, so recording can't start.");
                Add("显卡设备初始化失败。", "The graphics device failed to initialize.");
                Add("目标尺寸无效。", "The target size is invalid.");
                Add("等待画面超时，请稍后重试。", "Timed out waiting for a frame — try again.");
                Add("启动录制失败：", "Couldn't start recording: ");
                Add("编码器不接受该配置：", "The encoder rejected this configuration: ");
                Add("录制失败：", "Recording failed: ");
                Add("未命名录制", "Untitled recording");
                Add("未命名截图", "Untitled screenshot");
                Add("录制中", "Recording");
                Add("录制中的目标：", "Recording: ");
                Add("请先选择录制目标。", "Pick a capture target first.");
                Add("选择录制文件的保存位置", "Choose where recordings are saved");
                Add("无法使用该文件夹：", "That folder can't be used: ");
                Add("没有可录制的目标。", "There's nothing available to record.");
                Add("开始录制：", "Recording started: ");
                Add("录制完成（{0}）", "Recording finished ({0})");
                Add("录制未成功：", "Recording didn't finish: ");
                Add("录制已暂停（暂停时长不计入视频）",
                    "Recording paused — paused time isn't included in the video");
                Add("录制已恢复", "Recording resumed");
                Add("正在结束录制…", "Finishing the recording…");
                Add("正在完成录制文件，请稍候…", "Finalizing the video file, please wait…");
                Add("正在录制中。退出前需要先结束录制并完成文件收尾，否则视频将无法播放。",
                    "A recording is in progress. It has to be stopped and finalized before quitting, or the video won't play.");
                Add("确定现在结束录制并退出吗？", "Stop recording and quit now?");
                Add("音频不可用，本次为无声录制：", "Audio is unavailable; recording without sound: ");

                // 音频采集
                Add("找不到默认播放设备。", "No default playback device found.");
                Add("找不到麦克风设备。", "No microphone found.");
                Add("无法打开音频设备（0x{0}）。", "Couldn't open the audio device (0x{0}).");
                Add("系统拒绝访问麦克风，请在隐私设置中允许桌面应用使用麦克风。",
                    "Microphone access was denied. Allow desktop apps to use the microphone in Privacy settings.");
                Add("音频设备不支持所需格式（0x{0}）。",
                    "The audio device doesn't support the required format (0x{0}).");
                Add("无法获取音频采集接口。", "Couldn't obtain the audio capture interface.");
                Add("音频流无法启动（0x{0}）。", "The audio stream couldn't start (0x{0}).");
                Add("启动音频采集失败：", "Couldn't start audio capture: ");
                Add("{0}采集中断：", "{0} capture stopped: ");
                Add("{0}设备已失效（可能被拔出或切换了默认设备）",
                    "The {0} device became invalid (unplugged, or the default device changed)");
                Add("{0}设备未初始化", "The {0} device isn't initialized");
                Add("{0}设备已被独占占用", "The {0} device is held exclusively by another app");
                Add("{0}采集失败（{1} 0x{2}）", "{0} capture failed ({1} 0x{2})");

                // 主界面杂项
                Add("修改后需重新启动分身桌面才生效",
                    "Takes effect after the parallel desktop is restarted");
                Add("已保存凭据（DPAPI 加密，仅本机本账户可解）",
                    "Credentials saved (DPAPI-encrypted; only this account on this PC can decrypt them)");
                Add("系统同时只允许一个分身桌面；需要更多隔离桌面时可用 Windows 沙盒（即抛环境，不共享文件）。",
                    "Windows allows only one parallel desktop at a time. For more isolated desktops use Windows Sandbox — disposable, and sharing no files.");
                Add("跟随显示器（{0}）", "Match monitor ({0})");
                Add("原生", "native");
                Add("（该屏未报告支持）", "(not reported as supported)");
                Add("（系统默认）", " (system default)");
                Add("帧率上限已设为 {0} FPS。{1}。", "Frame rate cap set to {0} FPS. {1}.");
                Add("设置帧率失败（可能未获得管理员授权）。",
                    "Couldn't set the frame rate — administrator approval may not have been granted.");
                Add("修改后需重启电脑生效", "takes effect after a restart");
                Add("设置开机自启失败，详见日志。", "Couldn't change the run-at-startup setting — see the log.");
                Add("已创建方案「{0}」，可以直接修改它的显示设置。",
                    "Profile “{0}” created — its display settings are ready to edit.");
                Add("确定删除方案「{0}」吗？", "Delete profile “{0}”?");
                Add("配置完成，现在可以启动桌面了。", "Setup complete — you can start the desktop now.");
                Add("配置已写入，请重启电脑后再启动桌面。",
                    "The settings are written; restart the PC before starting the desktop.");
                Add("已取消（未获得管理员授权）。", "Cancelled — administrator approval wasn't granted.");
                Add("配置未完成，详见日志。", "Setup didn't finish — see the log.");
                Add("配置未完成，详见日志：", "Setup didn't finish. See the log: ");
                Add("推送剪贴板失败，详见日志。", "Couldn't send the clipboard — see the log.");
                Add("取回剪贴板失败，详见日志。", "Couldn't fetch the clipboard — see the log.");
                Add("未检测到 Edge 或 Chrome。", "Neither Edge nor Chrome was found.");
                Add("未能创建任何快捷方式：", "No shortcuts could be created:");
                Add("已在桌面创建以下快捷方式：", "These shortcuts were created on the desktop:");
                Add("用它启动的浏览器使用独立配置，可与主桌面的浏览器同时运行。",
                    "A browser started from one uses its own profile and can run alongside the main desktop's browser.");
                Add("把它拖进分身桌面里使用即可（两边共用同一个桌面文件夹）。",
                    "Just drag it into the parallel desktop — both sides share the same Desktop folder.");
                Add("以下未能创建：", "These couldn't be created:");
                Add("点击选择分身桌面显示的位置", "Click to choose where the parallel desktop appears");

                // 托盘与日志窗口
                Add("已收起（后台运行）", "Hidden (running in background)");
                Add("（无匹配内容）", "(nothing matches)");
                Add("{0} / {1} 行", "{0} of {1} lines");
                Add("清空失败：", "Couldn't clear it: ");

                // ---- 「画面」页：目标显示器与作用域 ----

                Add("分身桌面开在哪块屏、以及呈现得多清晰",
                    "Which screen it opens on, and how sharp it looks");
                Add("以下设置随方案保存。", "These settings are saved with the profile.");
                Add("以下设置随方案「{0}」保存，换方案会换成另一套。",
                    "These are saved with profile “{0}” — switching profiles swaps in a different set.");

                Add("目标显示器", "Target monitor");
                Add("自动", "Automatic");
                // 显示器重命名
                // 精简后的说明文案
                Add("运行中不可改，收起桌面后再调", "Locked while running — hide the desktop to change");
                Add("运行中不可改", "Locked while running");
                Add("默认 30 FPS。需要管理员授权，重启电脑后生效。",
                    "30 FPS by default. Needs administrator approval and a restart.");
                Add("同一份浏览器配置不能两边同开，用独立配置的快捷方式绕开",
                    "One browser profile can't run on both sides — use a shortcut with its own profile");
                Add("窗口被遮挡也不影响录制内容。", "Covering the window doesn't affect the recording.");
                Add("未选目标时自动录分身桌面", "Records the parallel desktop when no target is picked");
                Add("「启动桌面」会自动配置。仅在系统更新或组策略改回设置时才需要手动修复。",
                    "Start desktop configures things automatically. Only needed to repair after a Windows update or group policy reverts them.");

                Add("修复", "Fix");
                Add("系统组件缺失，无法修复", "System component missing — can't be fixed");
                Add("重命名", "Rename");
                Add("重命名显示器", "Rename monitor");
                Add("给这块屏起个名字", "Give this screen a name");
                Add("例如：左侧竖屏、AI 工作屏", "e.g. Left portrait, AI workspace");
                Add("留空即恢复默认名称。名字按显示器接口保存，换分辨率或重新插拔都不会丢。",
                    "Leave empty to restore the default name. Names are stored per display output, so they survive resolution changes and replugging.");
                Add("{0}　{1}×{2}{3}", "{0}　{1}×{2}{3}");
                Add("　主屏", "　primary");
                Add("这块显示器已断开，插回后才能改名。",
                    "That monitor is disconnected — plug it back in to rename it.");
                Add("自动（现在是 {0}）", "Automatic (currently {0})");
                Add("{0}（{1}×{2}）", "{0} ({1}×{2})");
                Add("{0}（{1}×{2}，主屏）", "{0} ({1}×{2}, primary)");
                Add("连接中即时生效，无需断开重连；选项来自 {0} 实际支持的模式",
                    "Applies instantly while connected — no reconnect. Options come from the modes {0} actually supports");
                Add("分身桌面正显示在 {0} 上；换一块会立刻搬过去。",
                    "The parallel desktop is on {0} right now; picking another moves it immediately.");
                Add("未指定时用主屏之外的第一块，现在是 {0}。",
                    "With none chosen it uses the first screen that isn't primary — currently {0}.");
                Add("{0} 已断开，画面暂时放在 {1}；插回后会自动回到原屏。",
                    "{0} is disconnected, so the view sits on {1} for now; it returns automatically when you plug it back in.");
                Add("下面的分辨率与缩放按这块屏的能力列出。",
                    "The resolution and scale below are listed from this screen's capabilities.");
                Add("分身桌面正在运行；换一块屏会立刻搬过去。",
                    "The parallel desktop is running; picking another screen moves it immediately.");
                Add("这台电脑只有一块显示器，分身桌面会盖在主桌面上，用热键或「收起桌面」切回。",
                    "This PC has only one monitor, so the parallel desktop covers your main one — use the hotkey or Hide desktop to switch back.");

                // 目标屏被拔掉：只提示，绝不改写 MonitorDevice
                Add("{0}（已断开）", "{0} (disconnected)");
                Add("{0} 已断开。", "{0} is disconnected.");

                // ---- 「画面」页：全局设置分节 ----

                Add("全局设置", "System-wide");
                Add("全系统", "system-wide");
                Add("下面这项由 Windows 统一管理，不属于任何方案或显示器，改一次整台电脑生效。",
                    "Windows manages the setting below. It belongs to no profile and no monitor — changing it once affects the whole PC.");
                Add("（超出目标屏的 {0}Hz）", " (beyond the target screen's {0}Hz)");
                Add("目标屏刷新率", "target screen's refresh rate");
                Add("{0} FPS（当前整机设置）", "{0} FPS (current system-wide setting)");
                Add("应用", "Apply");
                Add("需要一次管理员授权", "Requires one administrator approval");

                // ---- 按需配置：不再有独立的「首次配置」步骤 ----

                Add("分身桌面需要先让 Windows 打开「子会话」功能，这要一次管理员授权（只需一次，之后不再需要）。",
                    "The parallel desktop needs Windows child sessions turned on, which takes one administrator approval — once only, never again.");
                Add("接下来会弹出管理员授权窗口，请选择「是」。",
                    "An administrator prompt will appear next — choose Yes.");
                Add("现在配置吗？", "Set it up now?");

                // 首次运行时就地生成的默认数据——必须跟着系统语言走，
                // 否则英文用户一上来就看到一个中文方案名
                Add("默认桌面", "Default desktop");
                Add("新建方案", "New profile");

                // 提权配置进程（独立进程，自己加载语言）
                Add("启用子会话", "Enable child sessions");
                Add("启用远程桌面监听器", "Enable the Remote Desktop listener");
                Add("允许委派默认凭据（免除重复输入密码）",
                    "Allow delegating default credentials (no repeated password prompts)");
                Add("TermService 设为自动启动", "Set TermService to start automatically");
                Add("重启 TermService", "Restart TermService");
                Add("清理无效的映像劫持项", "Remove the ineffective image-hijack entry");
                Add("成功", "OK");
                Add("失败: ", "failed: ");
                Add("异常: ", "exception: ");
                Add("配置失败", "Setup failed");
                Add("设置已全部写入，但子会话监听器需要重启电脑后才会启动。",
                    "Everything was written, but the child-session listener only starts after a restart.");
                Add("请重启电脑，然后直接启动桌面（无需再次配置）。",
                    "Restart the PC, then just start the desktop — no need to run setup again.");
                Add("详细信息：", "Details:");

                // 沙盒功能启用
                Add("启用沙盒功能", "Enable Sandbox");
                Add("正在启用…", "Enabling…");
                Add("需要先启用 Windows 沙盒功能。点右侧即可，需要一次管理员授权并重启电脑。",
                    "Windows Sandbox has to be enabled first. Use the button — it needs one administrator approval and a restart.");
                Add("将启用 Windows 沙盒功能。这是 Windows 的可选组件，需要一次管理员授权，装完要重启电脑才能使用。",
                    "This enables Windows Sandbox, an optional Windows component. It needs one administrator approval, and a restart before it can be used.");
                Add("现在启用吗？", "Enable it now?");
                Add("沙盒功能已启用，重启电脑后即可使用。",
                    "Sandbox is enabled. Restart the PC to start using it.");
                Add("启用沙盒功能失败，详见日志。", "Couldn't enable Sandbox — see the log.");
                Add("同时会关掉跨设备恢复，并挡掉它在分身桌面里弹的那个系统错误框。",
                    "It also turns off Cross Device Resume and suppresses the system error dialog it raises inside the parallel desktop.");
                Add("启动桌面时自动启用", "Enabled when you start the desktop");
                Add("启动桌面时自动启动", "Started when you start the desktop");
                Add("不用在这里操作——第一次点「启动桌面」时会一次性配置好，只需一次管理员授权。",
                    "Nothing to do here — the first time you press Start desktop it configures everything at once, with a single administrator approval.");
                Add("确认这台电脑具备运行条件。有未通过项也不用管——第一次点「启动桌面」时会自动配置好，只需一次管理员授权。",
                    "Confirm this PC meets the requirements. Failed items are fine — the first time you press Start desktop they get configured automatically, with a single administrator approval.");
                Add("那一次授权会改什么", "What that one approval changes");
                Add("重新配置系统", "Reconfigure the system");
                Add("重新走一遍首次运行的引导", "Walk through the first-run guide again");

                // ---- 跨设备功能冲突 ----
                Add("打开凭据对话框失败，详见日志。", "Couldn't open the credentials dialog — see the log.");
                Add("请填写账户名。", "Enter an account name.");
                Add("保存失败，详见日志。", "Save failed — see the log.");
                Add("登录凭据", "Credentials");
                Add("保存登录凭据", "Save sign-in credentials");
                Add("通常不需要保存密码：分身桌面会用你当前的登录身份自动进入。只有账户仅用 PIN 或 Windows Hello 登录时，系统才会强制要求账户密码。",
                    "You usually don't need to save a password — the parallel desktop signs in with your current identity. Windows only demands an account password when the account signs in with a PIN or Windows Hello only.");
                Add("DPAPI 加密，仅本机本账户可解密", "DPAPI-encrypted; only this account on this PC can decrypt it");
                Add("它防的是密码明文落盘与被云同步，不能对抗以你的身份运行的程序。",
                    "That prevents the password hitting disk in plain text or syncing to the cloud; it does not defend against programs running as you.");
                Add("点「启动桌面」即可自动完成配置（需要一次管理员授权）。",
                    "Press Start desktop and it configures itself (one administrator approval).");
                Add("RPC 服务器不可用 —— 子会话监听器未启动（配置后需重启电脑）",
                    "RPC server unavailable — the child-session listener isn't running (a restart is needed after setup)");
                Add("无法建立连接：远程桌面监听器未启用。请重启电脑后再试。",
                    "Can't connect: the Remote Desktop listener isn't enabled. Restart the PC and try again.");
                Add("1. 系统配置尚未完成，或配置后尚未重启电脑；",
                    "1. System setup hasn't finished, or the PC hasn't been restarted since;");
                Add("通常不需要——启动桌面时的那次配置已启用免密登录策略",
                    "Usually unnecessary — the setup done when starting the desktop already enabled passwordless sign-in");
                Add("家庭版不含子会话功能。第一次启动桌面时需要一次管理员授权，之后日常使用无需提权。",
                    "Home edition has no child sessions. Starting the desktop the first time needs one administrator approval; everyday use afterwards does not.");
        }
    }
}
