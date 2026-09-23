using System;

namespace ParaDesk.Core
{
    internal static partial class L
    {
        static partial void AddWpfMisc(Action<string, string> Add)
        {
            Add("• {0} 显示 / 收起分身桌面", "• {0} show / hide the parallel desktop");
            Add("• {0} 切换“仅查看”（锁住你的键鼠，防止误触）",
                "• {0} toggle view only (locks your input so you can't disturb it)");
            Add("• {0} 开始 / 停止录制", "• {0} start / stop recording");
            Add("• {0} 暂停 / 继续录制", "• {0} pause / resume recording");
            Add("• {0} 截图", "• {0} take a screenshot");
            Add("• {0} 收起分身桌面（里面的程序继续运行）",
                "• {0} hide the parallel desktop (its programs keep running)");
            Add("• {0} 把剪贴板发送到分身桌面", "• {0} send the clipboard to the parallel desktop");
            Add("• {0} 从分身桌面取回剪贴板", "• {0} fetch the clipboard from the parallel desktop");
            Add("• 还没有启用任何全局热键，可在「热键」页设置",
                "• No global hotkeys are enabled yet — set them up on the Hotkeys page");
            Add("• 关闭主窗口只是收进托盘，程序继续在后台运行",
                "• Closing the main window only tucks it into the tray; the app keeps running");

            Add("最后几个选择", "A few final choices");
            Add("登录 Windows 后自动在托盘运行", "Run in the tray when I sign in to Windows");
            Add("开机后在托盘待命、热键随时可用，不弹出主窗口",
                "Waits in the tray after you sign in with hotkeys ready; the main window stays closed");
            Add("默认开启仅查看", "Turn on view only by default");
            Add("你的键鼠不会作用到分身桌面，防止误碰 agent 的操作；随时可用热键或托盘菜单切换",
                "Your keyboard and mouse won't reach the parallel desktop, so you can't disturb the agent by accident. Switch it anytime with the hotkey or the tray menu");
            Add("完成后立即启动分身桌面", "Start the parallel desktop when I finish");
            Add("关闭向导后马上在选定的屏幕上开出分身桌面",
                "Opens the parallel desktop on the chosen screen as soon as the wizard closes");
            Add("分身桌面已经在运行。", "The parallel desktop is already running.");
            Add("暂时不能直接启动：{0}", "Can't start it directly yet: {0}");
            Add("启动分身桌面失败，详见日志。", "Couldn't start the parallel desktop — see the log.");

            Add("• 启用 Windows 子会话功能\n• 启用远程桌面监听器（子会话的必要前置条件，走本机回环、不经过网络）\n• 启用远程桌面防火墙规则（仅专用/域网络；多数 Win11 上无需启用）\n• 允许委派默认凭据并关闭「始终提示输入密码」，这样以后不必每次输入密码\n• 将远程桌面服务设为自动启动\n• 关掉跨设备恢复，并挡掉它在分身桌面里弹的系统错误框",
                "• Enable Windows child sessions\n• Enable the Remote Desktop listener (required by child sessions; loopback only, never over the network)\n• Enable the Remote Desktop firewall rules (private/domain networks only; on most Windows 11 PCs none need enabling)\n• Allow delegating default credentials and turn off “Always prompt for password”, so you don't type a password each time\n• Set the Remote Desktop service to start automatically\n• Turn off Cross Device Resume and suppress the system error dialog it raises inside the parallel desktop");

            Add("确定清空日志吗？当前日志和上一份滚动日志（paradesk.log.1）都会被删除。",
                "Clear the log? Both the current log and the previous rolled-over log (paradesk.log.1) will be deleted.");

            Add("已保存过密码。留空表示沿用已保存的密码。",
                "A password is already saved. Leave this blank to keep using it.");
            Add("确定清除已保存的登录凭据吗？清除后立即生效，无法撤销。",
                "Clear the saved sign-in credentials? This takes effect immediately and can't be undone.");
            Add("请填写密码。", "Enter a password.");
            Add("读取已保存的密码失败，请重新输入密码。",
                "Couldn't read the saved password. Please enter the password again.");

            Add("「{0}」已经是另一块显示器的名字，请换一个。",
                "\"{0}\" is already the name of another monitor. Please choose a different name.");

            Add("导出诊断包", "Export diagnostic bundle");
            Add("正在生成诊断包…", "Creating the diagnostic bundle…");
            Add("已生成诊断包", "Diagnostic bundle created");
            Add("生成诊断包失败：{0}", "Couldn't create the diagnostic bundle: {0}");
            Add("重新检查", "Check again");
            Add("环境检查失败", "Environment check failed");
            Add("详见日志。", "See the log.");
        }
    }
}
