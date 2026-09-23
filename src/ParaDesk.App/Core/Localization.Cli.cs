using System;

namespace ParaDesk.Core
{
    internal static partial class L
    {
        static partial void AddCli(Action<string, string> Add)
        {
            Add("{0} 不能在分身桌面内运行，请回到主桌面打开。",
                "{0} cannot run inside the parallel desktop. Please open it from your main desktop.");
            Add("{0} 已经在运行（请查看系统托盘）。", "{0} is already running (check the system tray).");
            Add("出现未预期的错误：", "An unexpected error occurred:");

            Add("命令格式无法识别（命令行与正在运行的程序版本可能不一致）。",
                "Unrecognized command format (the command line and the running program may be different versions).");
            Add("ParaDesk 正在退出，没有执行这条命令。", "ParaDesk is shutting down; the command was not executed.");
            Add("ParaDesk 还没准备好接收命令，请稍后再试。", "ParaDesk is not ready to accept commands yet. Please try again shortly.");
            Add("ParaDesk 处理这条命令超时。", "ParaDesk timed out handling this command.");
            Add("ParaDesk 没有给出结果。", "ParaDesk returned no result.");
            Add("等待 ParaDesk 应答超时。", "Timed out waiting for a reply from ParaDesk.");
            Add("无法解析 ParaDesk 的应答。", "Could not parse the reply from ParaDesk.");
            Add("与 ParaDesk 通信失败：", "Failed to communicate with ParaDesk: ");
            Add("与 ParaDesk 通信失败。", "Failed to communicate with ParaDesk.");
            Add("正在运行的 ParaDesk 是不支持命令行控制的旧版本，请先退出它（托盘图标 → 退出）再重新打开。",
                "The running ParaDesk is an older version without command-line control. Quit it first (tray icon → Exit), then start it again.");
            Add("ParaDesk 在处理这条命令时断开了连接（可能已退出或崩溃），详见日志。",
                "ParaDesk closed the connection while handling this command (it may have exited or crashed). See the log for details.");
            Add("ParaDesk 正在运行，但暂时接收不了命令（正忙、正在启动或正在退出），请稍后重试。",
                "ParaDesk is running but can't accept commands right now (busy, starting or exiting). Please try again shortly.");
            Add("命令管道被其他账户的程序占用，已拒绝发送命令。",
                "The command pipe is held by another account's program; the command was not sent.");

            Add("不认识的参数：{0}", "Unknown argument: {0}");
            Add("运行 {0} --help 查看全部命令。", "Run {0} --help to see all commands.");
            Add("命令执行出错：", "Command failed: ");
            Add("{0} 没有在运行。", "{0} is not running.");
            Add("--profile 后面需要一个方案名。", "--profile needs a profile name.");
            Add("路径无效：{0}", "Invalid path: {0}");
            Add("提醒至少要有标题或正文。", "A notification needs a title or a body.");
            Add("{0} 后面缺少取值。", "{0} requires a value.");
            Add("缺少参数，应为 {0} 之一。", "Missing argument; expected one of {0}.");
            Add("“{0}”不是有效的取值，应为 {1} 之一。", "\"{0}\" is not a valid value; expected one of {1}.");
            Add("多余的参数：{0}", "Unexpected argument: {0}");
            Add("用法：", "Usage: ");
            Add("--start [--profile <方案名>] [--wait] [--json]", "--start [--profile <name>] [--wait] [--json]");
            Add("--screenshot [<路径>] [--json]", "--screenshot [<path>] [--json]");
            Add("--notify [--title <标题>] [--body <正文>] [--level info|warn|error] [--sound] [--json]",
                "--notify [--title <title>] [--body <text>] [--level info|warn|error] [--sound] [--json]");

            Add("用法：{0} [命令] [参数]", "Usage: {0} [command] [options]");
            Add("控制正在运行的 ParaDesk（转发给主实例，在分身桌面里也能用）：",
                "Control the running ParaDesk (forwarded to the main instance; also works inside the parallel desktop):");
            Add("查看当前状态。--json 输出机器可读的状态快照；没有在运行时退出码为 3。",
                "Show the current state. --json prints a machine-readable snapshot; exit code 3 if ParaDesk is not running.");
            Add("--start [--profile <方案名>] [--wait]", "--start [--profile <name>] [--wait]");
            Add("启动或重新接入分身桌面。--profile 先切换方案；--wait 等到连上或失败（最多 100 秒）。环境未就绪返回 2，不会弹出管理员授权。",
                "Start or reattach the parallel desktop. --profile switches the profile first; --wait waits until connected or failed (up to 100 s). Returns 2 if the system is not ready; never asks for administrator rights.");
            Add("收起画面，子会话继续运行。", "Hide the parallel desktop window; the child session keeps running.");
            Add("注销子会话（结束里面的所有程序），不弹确认框。",
                "Sign out of the child session (ends every program in it), without confirmation.");
            Add("仅查看：禁止用键鼠操作分身桌面。", "View-only: block keyboard and mouse input to the parallel desktop.");
            Add("分身桌面窗口置顶。", "Keep the parallel desktop window on top.");
            Add("录制。start 默认只录分身桌面窗口（画面没打开时返回 1）；要录显示器请加 --target monitor。",
                "Recording. start records only the parallel desktop window by default (exit 1 if the view isn't open); add --target monitor to record a monitor.");
            Add("--screenshot [<路径>]", "--screenshot [<path>]");
            Add("截取分身桌面画面并输出文件路径（画面没打开时返回 1）。路径可以相对当前目录，省略则存到录制输出目录；给出文件路径时覆盖同名文件，总是保存为 PNG。",
                "Capture the parallel desktop view and print the file path (exit 1 if the view isn't open). The path may be relative to the current directory; if omitted, it goes to the recording output folder. An explicit file path overwrites an existing file; the image is always PNG.");
            Add("已发出退出请求，但 ParaDesk 在 60 秒内没有退出。",
                "Asked ParaDesk to quit, but it didn't exit within 60 seconds.");
            Add("--notify [--title <标题>] [--body <正文>] [--level info|warn|error] [--sound]",
                "--notify [--title <title>] [--body <text>] [--level info|warn|error] [--sound]");
            Add("弹出提醒。正文也可以直接写在命令后面。", "Show a notification. The body can also be given right after the command.");
            Add("唤起主窗口。", "Bring up the main window.");
            Add("退出程序并等它真正退出（最多 60 秒）：录制先收尾，分身桌面只收起、子会话保留，不弹确认框。",
                "Quit and wait until ParaDesk has actually exited (up to 60 s): finishes any recording first, hides the parallel desktop but keeps the child session, without confirmation.");
            Add("  以上命令都可以加 --json，输出机器可读的 JSON（纯 ASCII）。",
                "  All of the above accept --json for machine-readable JSON output (pure ASCII).");
            Add("本地执行（不需要主实例）：", "Local commands (no running instance needed):");
            Add("只读环境自检。就绪返回 0，未就绪返回 2。", "Read-only environment check. Returns 0 when ready, 2 otherwise.");
            Add("--diagbundle [<路径>]", "--diagbundle [<path>]");
            Add("生成诊断包（zip），报告问题时附上。", "Create a diagnostics bundle (zip) to attach to bug reports.");
            Add("纯逻辑自检，几秒跑完。", "Pure logic self-test; finishes in seconds.");
            Add("本地化字典自检。", "Localization dictionary self-test.");
            Add("子会话对话框守护自检。", "Child-session dialog guard self-test.");
            Add("--rectest [秒数] [--monitor N]", "--rectest [seconds] [--monitor N]");
            Add("录屏自检：真实录制一块显示器一段时间（默认主显示器、10 秒，可设 3~120 秒）。--monitor N 指定第 N 块显示器（从 1 起，与界面上的「显示器 N」一致）。",
                "Recording self-test: really records one monitor for a while (the primary monitor for 10 s by default; 3-120 s allowed). --monitor N picks monitor N (counting from 1, as in \"Monitor N\" in the app).");
            Add("录制内容自检：录一个已知画面并逐帧核对颜色。",
                "Recording content self-test: records a known pattern and checks the frame colors.");
            Add("动态分辨率技术验证（开发用，会真实连接一次子会话）。",
                "Dynamic resolution spike (for development; really connects to a child session once).");
            Add("导出 WPF 资源键（开发用）。", "Dump WPF resource keys (for development).");
            Add("显示本帮助。", "Show this help.");
            Add("显示版本号。", "Show the version.");
            Add("界面启动参数：", "UI startup options:");
            Add("（不带参数）", "(no arguments)");
            Add("打开主窗口；已经在运行则把它唤到前台。", "Open the main window, or bring it to the front if already running.");
            Add("启动后只留在系统托盘（开机自启用）。", "Start in the system tray only (used for autostart).");
            Add("由开机自启项传入：在分身桌面里、或已经在运行时静默退出。",
                "Passed by the sign-in startup entry: exits silently inside the parallel desktop or when ParaDesk is already running.");
            Add("先等进程 <pid> 退出再启动（切换语言时内部使用）。",
                "Wait for process <pid> to exit before starting (used internally when switching language).");
            Add("退出码：", "Exit codes:");
            Add("执行了但失败（原因见输出）", "Ran but failed (see the output)");
            Add("环境未就绪或系统不支持", "System not ready or not supported");
            Add("ParaDesk 没有在运行", "ParaDesk is not running");
            Add("参数写错了", "Invalid arguments");
            Add("参数名不区分大小写。", "Option names are case-insensitive.");
            Add("这是窗口程序：cmd 里直接运行不会等它结束，要拿退出码请用 start /wait；PowerShell 里把输出重定向或接管道即可。",
                "This is a GUI program: cmd does not wait for it, so use start /wait to get the exit code; in PowerShell, redirect or pipe the output.");

            Add("=== {0} v{1} 环境自检 ===", "=== {0} v{1} environment check ===");
            Add("(无)", "(none)");
        }
    }
}
