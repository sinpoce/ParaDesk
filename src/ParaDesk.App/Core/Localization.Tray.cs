using System;

namespace ParaDesk.Core
{
    internal static partial class L
    {
        static partial void AddTray(Action<string, string> Add)
        {
            Add("查看提醒(&N)", "View &notification");
            Add("截图(&P)", "Ca&pture screenshot");
            Add("暂停录制(&U)", "Pa&use recording");
            Add("继续录制(&U)", "Res&ume recording");

            Add("录制已暂停", "Recording paused");
            Add("有新提醒", "New notification");

            Add("Ctrl+C 留给复制，请换一个", "Ctrl+C is reserved for Copy; try another");
            Add("Ctrl+V 留给粘贴，请换一个", "Ctrl+V is reserved for Paste; try another");
            Add("Ctrl+X 留给剪切，请换一个", "Ctrl+X is reserved for Cut; try another");
            Add("Ctrl+Z 留给撤销，请换一个", "Ctrl+Z is reserved for Undo; try another");
            Add("Ctrl+Y 留给重做，请换一个", "Ctrl+Y is reserved for Redo; try another");
            Add("Ctrl+A 留给全选，请换一个", "Ctrl+A is reserved for Select all; try another");
            Add("Ctrl+S 留给保存，请换一个", "Ctrl+S is reserved for Save; try another");
            Add("Ctrl+Space 留给输入法中英文切换，请换一个",
                "Ctrl+Space is reserved for toggling the input method; try another");
            Add("Ctrl+Esc 留给开始菜单，请换一个", "Ctrl+Esc is reserved for the Start menu; try another");
            Add("Ctrl+Shift+Esc 留给任务管理器，请换一个", "Ctrl+Shift+Esc is reserved for Task Manager; try another");
            Add("Ctrl+Alt+Del 留给系统安全选项，请换一个",
                "Ctrl+Alt+Del is reserved for the Windows security screen; try another");
            Add("Alt+F4 留给关闭窗口，请换一个", "Alt+F4 is reserved for closing windows; try another");
            Add("Alt+Tab 留给切换窗口，请换一个", "Alt+Tab is reserved for switching windows; try another");
            Add("Alt+Esc 留给切换窗口，请换一个", "Alt+Esc is reserved for switching windows; try another");
            Add("Alt+Space 留给窗口菜单，请换一个", "Alt+Space is reserved for the window menu; try another");
            Add("Win+L 留给锁屏，请换一个", "Win+L is reserved for locking the PC; try another");
            Add("Win+D 留给显示桌面，请换一个", "Win+D is reserved for showing the desktop; try another");
            Add("Win+E 留给文件资源管理器，请换一个", "Win+E is reserved for File Explorer; try another");
            Add("Win+R 留给“运行”对话框，请换一个", "Win+R is reserved for the Run dialog; try another");
            Add("Win+S 留给搜索，请换一个", "Win+S is reserved for Search; try another");
            Add("Win+V 留给剪贴板历史，请换一个", "Win+V is reserved for clipboard history; try another");
            Add("Win+Tab 留给任务视图，请换一个", "Win+Tab is reserved for Task View; try another");
            Add("Win+Space 留给切换输入法，请换一个", "Win+Space is reserved for switching input languages; try another");
            Add("Win+Shift+S 留给截图工具，请换一个", "Win+Shift+S is reserved for the Snipping Tool; try another");
            Add("{0} 会影响正常打字，请再配合 Ctrl / Alt / Win",
                "{0} would get in the way of typing; add Ctrl, Alt or Win");
        }
    }
}
