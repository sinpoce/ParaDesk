using System;

namespace ParaDesk.Core
{
    internal static partial class L
    {
        static partial void AddAppContext(Action<string, string> Add)
        {
            Add("分身桌面画面没有打开。命令行默认只录分身桌面；要录显示器请加 --target monitor。",
                "The parallel desktop view isn't open. From the command line, recording captures only the parallel desktop by default; add --target monitor to record a monitor.");
            Add("已切换到方案「{0}」。窗口模式、声音与剪贴板要在下次启动或重新接入后生效。",
                "Switched to profile \u201c{0}\u201d. Window mode, audio and clipboard take effect the next time the desktop starts or reattaches.");
            Add("程序正在退出。", "ParaDesk is quitting.");
            Add("录制目录所在磁盘只剩 {0}，空间不足，无法开始录制。",
                "Only {0} left on the recording drive — not enough space to start recording.");
            Add("录制目标已经不在了。", "The recording target is no longer available.");
            Add("录制提示：", "Recording note: ");
            Add("录制已分段保存，但下一段未能开始：", "The segment was saved, but the next segment couldn't start: ");
            Add("录制中失去声音：", "The recording lost its sound: ");
            Add("磁盘空间不足（剩余 {0}），已停止录制。", "Low disk space ({0} left) — recording stopped.");
            Add("录制目录所在磁盘只剩 {0}；低于 500 MB 时会自动停止录制。",
                "Only {0} left on the recording drive; recording stops automatically below 500 MB.");
            Add("自动录制未能开始：", "Automatic recording couldn't start: ");
            Add("已自动开始录制分身桌面。", "Started recording the parallel desktop automatically.");
            Add("当前没有在录制。", "Not recording right now.");
            Add("{0}（{1}）", "{0} ({1})");

            Add("未自动启动分身桌面：", "The parallel desktop wasn't started automatically: ");
            Add("ParaDesk 有新版本", "A new version of ParaDesk is available");
            Add("新版本 {0} 已发布（当前 {1}）。下载：{2}", "Version {0} is out (you have {1}). Download: {2}");
            Add("请在主窗口点「启动桌面」完成首次配置（需要一次管理员授权）。",
                "Click Start desktop in the main window to finish first-time setup (needs administrator approval once).");
            Add("尚未完成首次配置：请在主窗口点「启动桌面」完成配置（需要一次管理员授权）。",
                "First-time setup isn't done: click Start desktop in the main window to finish it (needs administrator approval once).");
            Add("分身桌面的画面正在关闭，请过几秒再启动。",
                "The parallel desktop view is still closing. Try starting it again in a few seconds.");

            Add("已重新接入分身桌面。", "Reattached to the parallel desktop.");
            Add("连接意外断开。", "The connection dropped unexpectedly.");
            Add("子会话已经结束。", "The child session has ended.");
            Add("画面意外断开，正在重新接入（第 {0} 次）…", "The view dropped unexpectedly — reattaching (attempt {0})…");
            Add("自动重新接入未成功，已放弃：", "Automatic reattach didn't work and was abandoned: ");
            Add("子会话仍在后台运行，可从托盘菜单「重新接入桌面」。",
                "The desktop is still running in the background — use Reattach desktop in the tray menu.");

            Add("已撤销 ParaDesk 对系统所做的配置。", "ParaDesk's changes to the system have been undone.");
            Add("已撤销配置，重启电脑后完全生效。", "Setup has been undone; restart the PC for it to take full effect.");
            Add("撤销未完成，详见日志。", "Undo didn't finish — see the log.");
            Add("系统配置或撤销正在进行中，请等它完成后再试。",
                "System setup or undo is already in progress. Wait for it to finish, then try again.");

            Add("已截图：", "Screenshot saved: ");
            Add("截图失败：", "Screenshot failed: ");
            Add("截图失败，详见日志。", "Screenshot failed — see the log.");
            Add("没有可截图的目标。", "There's nothing available to capture.");
            Add("分身桌面画面没有打开，无法同步剪贴板。", "The parallel desktop view isn't open, so the clipboard can't be synced.");
            Add("推送/取回剪贴板只在「手动同步」剪贴板模式下可用，可在「输入与共享」页切换。",
                "Sending or fetching the clipboard only works in Manual sync clipboard mode; change it on the Input & sharing page.");
            Add("已把剪贴板推送到分身桌面。", "Clipboard sent to the parallel desktop.");
            Add("已从分身桌面取回剪贴板。", "Clipboard fetched from the parallel desktop.");

            Add("正在连接", "Connecting");
            Add("正在重新连接", "Reconnecting");
            Add("正在关闭", "Closing");
            Add("分身桌面：{0}", "Parallel desktop: {0}");
            Add("；方案：{0}", "; profile: {0}");
            Add("；显示器：{0}（{1}×{2}）", "; monitor: {0} ({1}×{2})");
            Add("；显示器：{0}", "; monitor: {0}");
            Add("；仅查看", "; view only");
            Add("；录制已暂停", "; recording paused");
            Add("；录制中", "; recording");

            Add("命令为空。", "Empty command.");
            Add("ParaDesk 正在退出。", "ParaDesk is quitting.");
            Add("ParaDesk 已退出。", "ParaDesk has quit.");
            Add("主实例的界面线程不可用。", "The running instance's UI thread isn't available.");
            Add("不认识的命令：{0}", "Unknown command: {0}");
            Add("--profile 后面要跟方案名。", "--profile needs a profile name.");
            Add("没有名为「{0}」的方案。现有方案：{1}", "There's no profile named “{0}”. Profiles: {1}");
            Add("分身桌面未能启动，详见日志。", "The parallel desktop didn't start — see the log.");
            Add("分身桌面已在运行。", "The parallel desktop is already running.");
            Add("正在重新接入分身桌面。", "Reattaching the parallel desktop.");
            Add("正在启动分身桌面。", "Starting the parallel desktop.");
            Add("分身桌面已连接。", "The parallel desktop is connected.");
            Add("分身桌面在连上之前被关闭了。", "The parallel desktop was closed before it connected.");
            Add("连接失败：", "Connection failed: ");
            Add("等待 100 秒仍未连上，分身桌面仍在连接中。",
                "Still not connected after 100 seconds; the parallel desktop is still connecting.");
            Add("画面已经是收起状态，分身桌面在后台运行。",
                "The view is already hidden; the parallel desktop is running in the background.");
            Add("分身桌面没有在运行。", "The parallel desktop isn't running.");
            Add("已收起画面，分身桌面在后台继续运行。", "View hidden; the parallel desktop keeps running in the background.");
            Add("已发出注销请求，分身桌面仍在关闭中。", "Sign-out requested; the parallel desktop is still closing.");
            Add("已关闭分身桌面。", "The parallel desktop is closed.");
            Add("用法：--view-only on|off|toggle", "Usage: --view-only on|off|toggle");
            Add("用法：--topmost on|off|toggle", "Usage: --topmost on|off|toggle");
            Add("已开启仅查看。", "View only is on.");
            Add("已开启窗口置顶。", "Always on top is on.");
            Add("已关闭窗口置顶。", "Always on top is off.");
            Add("（画面没有打开，下次打开时生效。）", "(The view isn't open; this applies the next time it opens.)");
            Add("--target 只能是 desktop 或 monitor。", "--target must be desktop or monitor.");
            Add("录制已经是暂停状态。", "The recording is already paused.");
            Add("录制没有暂停。", "The recording isn't paused.");
            Add("录制器暂时无法切换暂停状态，请稍后再试。", "The recorder can't pause or resume right now; try again shortly.");
            Add("用法：--record start|stop|pause|resume|toggle [--target desktop|monitor]",
                "Usage: --record start|stop|pause|resume|toggle [--target desktop|monitor]");
            Add("已经在录制中：{0}", "Already recording: {0}");
            Add("分身桌面画面没有打开，无法录制它。", "The parallel desktop view isn't open, so it can't be recorded.");
            Add("录制已保存：", "Recording saved: ");
            Add("已请求结束录制，文件仍在收尾：", "Stop requested; the file is still being finalized: ");
            Add("路径无效：", "Invalid path: ");
            Add("分身桌面画面没有打开，无法截图。",
                "The parallel desktop view isn't open, so there is nothing to screenshot.");
            Add("截图超时，详见日志。", "The screenshot timed out — see the log.");
            Add("--level 只能是 info、warn 或 error。", "--level must be info, warn or error.");
            Add("提醒内容为空：请用 --body 或第一个位置参数给出正文。",
                "The notification is empty: pass the text with --body or as the first argument.");
            Add("已发出提醒。", "Notification shown.");
            Add("已唤起主窗口。", "Main window brought to the front.");
            Add("正在结束录制并退出 ParaDesk（分身桌面保留在后台）。",
                "Finishing the recording and quitting ParaDesk (the parallel desktop stays in the background).");
            Add("ParaDesk 正在退出（分身桌面保留在后台）。",
                "ParaDesk is quitting (the parallel desktop stays in the background).");

            Add("重启失败，程序继续运行。新的语言设置将在下次启动时生效。",
                "Couldn't restart, so ParaDesk keeps running. The new language applies the next time it starts.");
        }
    }
}
