using System;

namespace ParaDesk.Core
{
    internal static partial class L
    {
        static partial void AddMainWindow(Action<string, string> Add)
        {
            Add("系统 {0} (build {1})　子会话 {2}　监听器 {3}　架构 {4}",
                "{0} (build {1})　Child sessions {2}　Listener {3}　Architecture {4}");
            Add("{0}（在 {1} 上模拟运行，建议改用 arm64 版）", "{0} (emulated on {1}; the arm64 build runs natively)");
            Add("知道了", "Got it");
            Add("来自分身桌面的提醒", "Notice from the parallel desktop");
            Add("。热键 {0}", ". Hotkey: {0}");

            Add("分身桌面里", "Inside the parallel desktop");
            Add("登录后自动运行", "Run after sign-in");
            Add("分身桌面每次登录时执行一次，已登录的桌面需关闭后重新启动才生效",
                "Runs once each time the parallel desktop signs in. If it's already signed in, close it and start it again for changes to apply");
            Add("例如 wt -d C:\\repo 或 powershell -NoExit -Command claude",
                "e.g. wt -d C:\\repo or powershell -NoExit -Command claude");
            Add("工作目录（留空为用户主目录）", "Working folder (blank = your user folder)");
            Add("选择工作目录", "Choose working folder");
            Add("选择登录后自动运行的工作目录", "Choose the working folder for Run after sign-in");
            Add("工作目录不存在", "Working folder not found");
            Add("未能注册分身桌面里的守护程序", "Couldn't register the helper inside the parallel desktop");
            Add("登录后自动运行和保持唤醒都靠它执行，没注册时不会生效。详见日志。",
                "Run after sign-in and Keep awake both rely on it and won't work without it. See the log.");
            Add("保持唤醒", "Keep awake");
            Add("阻止电脑睡眠和分身桌面因空闲而锁屏，适合让 agent 长时间无人值守地运行。对下次启动的分身桌面生效；已在运行的一般 30 秒内也会跟上。",
                "Keeps the PC from sleeping and the parallel desktop from locking when idle, so an agent can run unattended for hours. Applies the next time the parallel desktop starts; one that's already running usually follows within 30 seconds.");
            Add("分身桌面的声音", "Parallel desktop sound");
            Add("下次启动或重新接入时生效", "Takes effect the next time you start or reattach");
            Add("在本机播放", "Play on this PC");
            Add("静音", "Mute");
            Add("留在分身桌面", "Keep in the parallel desktop");

            Add("截取分身桌面窗口；没开分身桌面时截取方案所在的显示器",
                "Captures the parallel desktop window; if it isn't open, captures the profile's monitor");
            Add("越高越清晰，文件也越大；1–5 FPS 适合长时间延时留档",
                "Higher is sharper but makes bigger files; 1–5 FPS suits long time-lapse records");
            Add("{0} FPS（延时）", "{0} FPS (time-lapse)");
            Add("随分身桌面自动录制", "Record along with the parallel desktop");
            Add("分身桌面连上后自动开始录它，收起或关闭时自动结束",
                "Starts recording the parallel desktop when it connects and stops when it's hidden or closed");
            Add("自动分段", "Split into segments");
            Add("长时间录制按时长切成多个文件；程序意外退出时只会损失最后一段",
                "Splits long recordings into several files by length; if the app quits unexpectedly, only the last segment is lost");
            Add("不分段", "Don't split");
            Add("每 {0} 分钟", "Every {0} minutes");
            Add("正在截图…", "Taking screenshot…");
            Add("打开文件", "Open file");
            Add("打开所在文件夹", "Show in folder");
            Add("无法打开文件夹：", "Couldn't open the folder: ");
            Add("文件已不存在：", "The file no longer exists: ");
            Add("无法打开该文件：", "Couldn't open the file: ");

            Add("截取分身桌面窗口（没开时截方案所在的显示器），存到录制的保存位置",
                "Captures the parallel desktop window (or the profile's monitor if it isn't open) and saves it to the recording folder");
            Add("暂停 / 继续录制", "Pause / resume recording");
            Add("录制中才有效", "Only works while recording");
            Add("收起分身桌面", "Hide the parallel desktop");
            Add("只收起画面、不会再打开；里面的程序继续运行",
                "Only hides the view and never reopens it; programs inside keep running");
            Add("把剪贴板发送到分身桌面", "Send clipboard to the parallel desktop");
            Add("从分身桌面取回剪贴板", "Fetch clipboard from the parallel desktop");
            Add("剪贴板设为「手动同步」时使用", "For when the clipboard is set to Manual sync");
            Add("热键冲突，未保存", "Hotkey conflict — not saved");
            Add("{0} 与「{1}」冲突：两个动作不能共用一个组合。请换一个，或先清除那一项。",
                "{0} conflicts with \"{1}\": two actions can't share a combination. Pick another, or clear that one first.");
            Add("以下热键没注册上，组合可能已被其它程序占用，请换一个：{0}",
                "These hotkeys couldn't be registered — another program may be using the combination. Please pick another: {0}");
            Add("有热键占用了保留的组合键", "Some hotkeys use reserved combinations");
            Add("以下热键占用了系统或常用程序在用的组合键：{0}",
                "These hotkeys use combinations that Windows or common programs rely on: {0}");

            Add("程序行为与全局选项，不随方案变化", "App behavior and global options — not tied to any profile");
            Add("Language — 切换后程序会自动重启，分身桌面保持运行",
                "语言 — the app restarts automatically after switching; the parallel desktop keeps running");
            Add("启动", "Startup");
            Add("登录 Windows 后自动运行 ParaDesk", "Run ParaDesk when you sign in to Windows");
            Add("启动时最小化到托盘", "Start minimized to the tray");
            Add("开机自启时不弹出主窗口，只在托盘运行",
                "When started at sign-in, stay in the tray without opening the main window");
            Add("程序启动后自动开启分身桌面", "Open the parallel desktop when the app starts");
            Add("环境就绪时自动启动；分身桌面已在后台运行时自动重新接入。配合开机自启，开机后即可交给 agent 使用",
                "Starts it once the system is ready, or reattaches if it's already running in the background. With Run at startup, it's ready for your agent right after you sign in");
            Add("启动时检查更新", "Check for updates at startup");
            Add("每次启动时访问 GitHub 查询新版本；除此之外本程序不联网",
                "Contacts GitHub at each startup to look for a new version; the app doesn't go online otherwise");
            Add("画面意外断开时自动重新接入", "Reattach automatically if the view drops");
            Add("分身桌面还在运行、只是画面断了（例如远程桌面服务重启）时，自动把画面接回来",
                "If the parallel desktop is still running but its view dropped (for example, Remote Desktop Services restarted), bring the view back automatically");
            Add("关闭桌面前确认", "Confirm before closing the desktop");
            Add("「关闭桌面」会注销分身桌面、结束里面的所有程序，先确认一次",
                "Close desktop signs out the parallel desktop and ends every program in it, so ask first");
            Add("关机时自动注销分身桌面", "Sign out the parallel desktop at shutdown");
            Add("关机或重启前先注销，让里面的程序有机会正常退出、保存",
                "Sign it out before shutdown or restart so programs inside get a chance to exit and save");

            Add("AI 工具", "AI tools");
            Add("AI 工具集成", "AI tool integration");
            Add("分身桌面里的 agent 可以用命令行通知你（--notify）、查询状态（--status）。按钮会把现成的配置或命令复制到剪贴板。",
                "Agents in the parallel desktop can notify you (--notify) and check status (--status) from the command line. These buttons copy ready-made config or commands to the clipboard.");
            Add("复制 Claude Code 通知配置", "Copy Claude Code notification config");
            Add("复制命令行用法", "Copy command-line usage");
            Add("已复制 Claude Code 通知配置", "Claude Code notification config copied");
            Add("把其中的 hooks 合并进 Claude Code 的 settings.json（用户级在 ~/.claude/settings.json，也可以放项目里的 .claude/settings.json）。之后任务完成或需要你确认时，主屏会收到提醒。",
                "Merge its hooks into Claude Code's settings.json (per-user at ~/.claude/settings.json, or .claude/settings.json in a project). You'll then get a notice on your main screen when a task finishes or needs your confirmation.");
            Add("已复制命令行用法", "Command-line usage copied");
            Add("可以直接贴给分身桌面里的 agent，或写进项目的说明文件，让它在需要时叫你。",
                "Paste it to the agent in the parallel desktop, or add it to your project's instructions, so it can call you when needed.");
            Add("任务完成", "Task finished");
            Add("需要你确认", "Needs your confirmation");
            Add("ParaDesk 命令行（分身桌面里也能用）。PowerShell 里要在路径前加 & ，例如 & \"...\\ParaDesk.exe\" --status",
                "ParaDesk command line (works inside the parallel desktop too). In PowerShell, put & before the path, e.g. & \"...\\ParaDesk.exe\" --status");
            Add("查询状态；加 --json 输出 JSON。退出码 3 表示 ParaDesk 没在运行",
                "Show status; add --json for JSON. Exit code 3 means ParaDesk isn't running");
            Add("在主屏提醒你（--level warn / error 用醒目样式，--sound 响一声）",
                "Notify you on the main screen (--level warn / error stands out more, --sound plays a sound)");
            Add("截图，输出文件路径", "Take a screenshot and print the file path");
            Add("列出全部命令", "List all commands");
            Add("复制失败", "Copy failed");
            Add("剪贴板正被其它程序占用，请稍后再试。", "Another program is using the clipboard. Try again in a moment.");

            Add("重新检查", "Check again");
            Add("通常一两秒即可完成。", "This usually takes a second or two.");
            Add("环境检查失败", "Environment check failed");
            Add("详见日志。", "See the log.");
            Add("导出诊断包", "Export diagnostic bundle");
            Add("把日志与环境信息打成一个 zip 放到桌面，报告问题时附上即可",
                "Packs the logs and environment info into a zip on your desktop — attach it when reporting a problem");
            Add("正在生成…", "Creating…");
            Add("正在生成诊断包…", "Creating the diagnostic bundle…");
            Add("正在收集日志与环境信息。", "Collecting logs and environment info.");
            Add("生成诊断包失败", "Couldn't create the diagnostic bundle");
            Add("已生成诊断包", "Diagnostic bundle created");
            Add("系统配置", "System setup");
            Add("撤销系统配置", "Undo system setup");
            Add("不再使用或准备卸载时，撤销 ParaDesk 对系统所做的配置",
                "When you stop using ParaDesk or before uninstalling, undo the changes it made to the system");
            Add("撤销配置…", "Undo setup…");
            Add("将撤销 ParaDesk 对系统所做的配置：", "This undoes the changes ParaDesk made to the system:");
            Add("关闭 Windows 的子会话功能", "Turn off Windows child sessions");
            Add("关闭 Windows 的子会话功能（第一次配置前就已开启的保持不变）",
                "Turn off Windows child sessions (left on if they were already on before the first setup)");
            Add("按第一次配置前记录的原值，恢复远程桌面监听器、防火墙规则、免密登录的凭据委派、「始终提示输入密码」和 TermService 启动类型",
                "Restore the Remote Desktop listener, firewall rules, credential delegation for password-free sign-in, “Always prompt for password” and the TermService startup type to the values recorded before the first setup");
            Add("如果没有第一次配置前的记录（例如由旧版本配置过），远程桌面监听器、防火墙规则、「始终提示输入密码」和 TermService 启动类型的原值无从得知，将保持不变，只删除 ParaDesk 写入的凭据委派条目；不需要远程桌面的话，可以在 Windows「设置 → 系统 → 远程桌面」里关闭。",
                "If there's no record from before the first setup (for example, an older version did the setup), the original Remote Desktop listener, firewall rules, “Always prompt for password” and TermService startup type are unknown and stay as they are; only the credential delegation entries ParaDesk wrote are removed. If you don't need Remote Desktop, turn it off in Windows Settings → System → Remote Desktop.");
            Add("移除分身桌面里的守护程序启动项", "Remove the startup entry of the helper inside the parallel desktop");
            Add("需要一次管理员授权。之后若还要使用分身桌面，需要重新配置并重启电脑。",
                "This needs administrator approval once. To use the parallel desktop again afterwards, you'll have to set it up again and restart the PC.");
            Add("分身桌面正在运行，建议先关闭它，里面的程序会随配置撤销而无法继续使用。",
                "The parallel desktop is running. Close it first — programs inside won't keep working once setup is undone.");
            Add("确定撤销吗？", "Undo now?");
            Add("正在撤销…", "Undoing…");
            Add("撤销配置未完成，详见日志：", "Undo didn't finish. See the log: ");
            Add("撤销配置完成", "Undo complete");
            Add("已撤销 ParaDesk 对系统所做的配置。", "ParaDesk's changes to the system have been undone.");
            Add("已撤销配置，重启电脑后完全生效。", "Setup has been undone; restart the PC for it to take full effect.");
            Add("系统配置或撤销正在进行中，请等它完成后再试。",
                "System setup or undo is already in progress. Wait for it to finish, then try again.");
            Add("打开日志查看器失败：", "Couldn't open the log viewer: ");
            Add("日志文件在：", "The log file is at:");

            Add("检查更新", "Check for updates");
            Add("访问 GitHub 查询是否有新版本", "Contacts GitHub to see whether a new version is out");
            Add("打开下载页", "Open download page");
            Add("正在检查…", "Checking…");
            Add("检查更新失败", "Couldn't check for updates");
            Add("发现新版本 {0}", "Version {0} is available");
            Add("当前版本 {0}。点「打开下载页」前往 GitHub 下载。",
                "You have {0}. Click Open download page to get it from GitHub.");
            Add("已是最新版本", "You're up to date");
            Add("当前版本 {0}，GitHub 上最新发布为 {1}。", "You have {0}; the latest release on GitHub is {1}.");
            Add("无法打开浏览器，请手动访问：", "Couldn't open the browser. Please visit:");
        }
    }
}
