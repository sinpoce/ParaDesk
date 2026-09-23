using System;
using System.Runtime.InteropServices;

namespace ParaDesk.Core
{
    /// <summary>
    /// 实际运行架构。Arm64 上的 AnyCPU 构建由系统以 x64 模拟运行，
    /// 而 RuntimeInformation 在模拟进程里同样报 X64，所以改看系统原生架构和实际加载的 CLR 目录。
    /// </summary>
    internal static class PlatformInfo
    {
        private const ushort MachineI386 = 0x014c;
        private const ushort MachineAmd64 = 0x8664;
        private const ushort MachineArm64 = 0xAA64;

        private static string _os;
        private static string _process;

        /// <summary>系统原生架构：arm64 / x64 / x86。</summary>
        public static string OsArchitecture
        {
            get
            {
                if (_os == null) _os = DetectOs();
                return _os;
            }
        }

        /// <summary>本进程实际运行的架构：arm64 / x64 / x86。</summary>
        public static string ProcessArchitecture
        {
            get
            {
                if (_process == null) _process = DetectProcess();
                return _process;
            }
        }

        public static bool IsEmulated
        {
            get { return !string.Equals(OsArchitecture, ProcessArchitecture, StringComparison.Ordinal); }
        }

        /// <summary>在 Arm64 设备上跑的是模拟的 x64 版本，换成 arm64 版可以原生运行。</summary>
        public static bool ShouldUseArm64Build
        {
            get { return OsArchitecture == "arm64" && ProcessArchitecture != "arm64"; }
        }

        public static string Describe()
        {
            return IsEmulated
                ? ProcessArchitecture + " (emulated on " + OsArchitecture + ")"
                : ProcessArchitecture + " (native)";
        }

        private static string DetectOs()
        {
            try
            {
                ushort processMachine, nativeMachine;
                if (IsWow64Process2(GetCurrentProcess(), out processMachine, out nativeMachine))
                    return Name(nativeMachine);
            }
            catch (EntryPointNotFoundException) { }
            catch (Exception ex) { Log.Debug("IsWow64Process2 失败: " + ex.Message); }
            return Environment.Is64BitOperatingSystem ? "x64" : "x86";
        }

        private static string DetectProcess()
        {
            if (!Environment.Is64BitProcess) return "x86";
            string dir = "";
            try { dir = RuntimeEnvironment.GetRuntimeDirectory() ?? ""; }
            catch (Exception ex) { Log.Debug("读取 CLR 目录失败: " + ex.Message); }
            return dir.IndexOf("FrameworkArm64", StringComparison.OrdinalIgnoreCase) >= 0 ? "arm64" : "x64";
        }

        private static string Name(ushort machine)
        {
            switch (machine)
            {
                case MachineArm64: return "arm64";
                case MachineAmd64: return "x64";
                case MachineI386: return "x86";
                default: return "0x" + machine.ToString("x4");
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
    }
}
