using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ParaDesk.Native
{
    internal static class NativeMethods
    {
        // ---------- 终端服务 / 子会话 ----------

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSEnableChildSessions([MarshalAs(UnmanagedType.Bool)] bool enable);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSIsChildSessionsEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSGetChildSessionId(out uint sessionId);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSLogoffSession(IntPtr hServer, uint sessionId,
            [MarshalAs(UnmanagedType.Bool)] bool wait);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSDisconnectSession(IntPtr hServer, uint sessionId,
            [MarshalAs(UnmanagedType.Bool)] bool wait);

        /// <summary>未公开：返回子会话命名管道路径。用于在不建界面的前提下自检通道可用性。</summary>
        [DllImport("winsta.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int WinStationCreateChildSessionTransport(StringBuilder path, int len);

        public const uint NoChildSession = 0xFFFFFFFF;
        public const int ErrorNotFound = 1168;

        // ---------- 进程 / 会话 ----------

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

        // ---------- 窗口 / 显示 ----------

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        public const int SM_REMOTESESSION = 0x1000;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;

        // ---------- 全局热键 ----------

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        // ---------- 关机拦截 ----------

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string pwszReason);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);

        public const int WM_QUERYENDSESSION = 0x0011;
        public const int WM_ENDSESSION = 0x0016;
        public const int WM_DISPLAYCHANGE = 0x007E;
        public const int WM_WINDOWPOSCHANGING = 0x0046;

        // ---------- View-only（输入拦截） ----------

        /// <summary>
        /// 禁用窗口后其子窗口一并隐式禁用，鼠标消息会被忽略、也无法获得键盘焦点，
        /// 但绘制由 WM_PAINT 驱动不受影响——正是 view-only 需要的语义。
        /// 必须对 HWND 调用本函数，而不是设 AxHost.Enabled：后者会同时翻转 ActiveX
        /// 的 ambient UIDead/Enabled 状态，某些控件会因此画成灰色。
        /// </summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnableWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool bEnable);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const uint LWA_ALPHA = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        public const uint SWP_NOZORDER = 0x0004;
    }
}
