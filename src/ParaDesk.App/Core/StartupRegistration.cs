using System;
using System.IO;
using Microsoft.Win32;

namespace ParaDesk.Core
{
    internal static class RunKeyStore
    {
        public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool Exists(string valueName)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                return k != null && k.GetValue(valueName) != null;
        }

        public static string Get(string valueName)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                return k == null ? null : k.GetValue(valueName) as string;
        }

        public static void Set(string valueName, string command)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                if (k == null) throw new IOException("无法打开或创建 HKCU\\" + KeyPath);
                k.SetValue(valueName, command, RegistryValueKind.String);
            }
        }

        public static void Remove(string valueName)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(KeyPath, true))
            {
                if (k != null && k.GetValue(valueName) != null) k.DeleteValue(valueName, false);
            }
        }

        public static string BuildCommand(string exePath, string args)
        {
            string cmd = "\"" + exePath + "\"";
            if (!string.IsNullOrEmpty(args)) cmd += " " + args;
            return cmd;
        }

        internal static void SplitCommand(string command, out string exe, out string args)
        {
            exe = "";
            args = "";
            if (command == null) return;
            string s = command.Trim();
            if (s.Length == 0) return;

            if (s[0] == '"')
            {
                int close = s.IndexOf('"', 1);
                if (close < 0) { exe = s.Substring(1).Trim(); return; }
                exe = s.Substring(1, close - 1).Trim();
                args = s.Substring(close + 1).Trim();
                return;
            }

            int ext = -1;
            for (int from = 0; ; )
            {
                int at = s.IndexOf(".exe", from, StringComparison.OrdinalIgnoreCase);
                if (at < 0) break;
                if (at + 4 == s.Length || char.IsWhiteSpace(s[at + 4])) { ext = at; break; }
                from = at + 4;
            }
            int cut = ext >= 0 ? ext + 4 : s.IndexOf(' ');
            if (cut < 0 || cut >= s.Length) { exe = s; return; }
            exe = s.Substring(0, cut).Trim();
            args = s.Substring(cut).Trim();
        }

        internal static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                a = Path.GetFullPath(a);
                b = Path.GetFullPath(b);
            }
            catch { }
            return string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        public static bool RepairIfStale(string valueName, string what)
        {
            string cmd = Get(valueName);
            if (string.IsNullOrEmpty(cmd)) return false;

            string exe, args;
            SplitCommand(cmd, out exe, out args);
            string current = AppInfo.ExecutablePath;
            if (string.IsNullOrEmpty(current) || SamePath(exe, current)) return false;

            if (IsUnderTempDir(current) && TargetExists(exe))
            {
                Log.Info(what + "启动项指向 " + exe + "，但本程序正从临时目录运行（" + current + "），不改写");
                return false;
            }

            string fixedCmd = BuildCommand(current, args);
            Set(valueName, fixedCmd);
            Log.Info(what + "启动项指向的程序不是当前程序（" + exe + "），已改写为: " + fixedCmd);
            return true;
        }

        internal static bool IsUnderTempDir(string path)
        {
            try
            {
                string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
                return Path.GetFullPath(path).StartsWith(temp, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool TargetExists(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return false;
            try { return File.Exists(Environment.ExpandEnvironmentVariables(exe)); }
            catch { return false; }
        }

        public const string ApprovedKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        public static bool IsApproved(string valueName)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, false))
                {
                    if (k == null) return true;   // 没有该项即视为启用
                    return !IsDisabledBlob(k.GetValue(valueName) as byte[]);
                }
            }
            catch { return true; }
        }

        public static bool ClearApprovalBlock(string valueName)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, true))
            {
                if (k == null) return false;
                object v = k.GetValue(valueName);
                if (v == null) return false;
                bool wasDisabled = IsDisabledBlob(v as byte[]);
                k.DeleteValue(valueName, false);
                return wasDisabled;
            }
        }

        private static bool IsDisabledBlob(byte[] blob)
        {
            return blob != null && blob.Length > 0 && blob[0] == 0x03;
        }
    }

    /// <summary>
    /// 开机自启。用 HKCU\...\Run 而非计划任务：本程序非提权运行，Run 键足够，
    /// 且用户能在「设置 → 应用 → 启动」里看到并自行关闭，符合预期。
    /// </summary>
    internal static class StartupRegistration
    {
        private const string ValueName = AppInfo.ProductName;

        internal const string AutostartMarker = "--autostart";

        private static bool IsApproved()
        {
            return RunKeyStore.IsApproved(ValueName);
        }

        private static void ClearApprovalBlock()
        {
            try { RunKeyStore.ClearApprovalBlock(ValueName); }
            catch (Exception ex) { Log.Error("清除启动项禁用标记失败", ex); }
        }

        public static bool RepairIfStale()
        {
            bool repaired = RunKeyStore.RepairIfStale(ValueName, "开机自启");
            try
            {
                string cur = RunKeyStore.Get(ValueName);
                if (string.IsNullOrEmpty(cur)) return repaired;
                string exe, args;
                RunKeyStore.SplitCommand(cur, out exe, out args);
                if (string.IsNullOrEmpty(exe)) return repaired;
                if ((" " + args + " ").IndexOf(" " + AutostartMarker + " ", StringComparison.OrdinalIgnoreCase) >= 0) return repaired;
                string fixedCmd = RunKeyStore.BuildCommand(exe, string.IsNullOrEmpty(args) ? AutostartMarker : AutostartMarker + " " + args);
                RunKeyStore.Set(ValueName, fixedCmd);
                Log.Info("开机自启项补上了 " + AutostartMarker + " 标记: " + fixedCmd);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("给开机自启项补标记失败: " + ex.Message);
                return repaired;
            }
        }

        public static bool IsEnabled()
        {
            try
            {
                if (string.IsNullOrEmpty(RunKeyStore.Get(ValueName))) return false;
                return IsApproved();
            }
            catch (Exception ex)
            {
                Log.Error("读取开机自启状态失败", ex);
                return false;
            }
        }

        public static bool SetEnabled(bool enabled, bool startMinimized)
        {
            try
            {
                if (enabled)
                {
                    string cmd = RunKeyStore.BuildCommand(AppInfo.ExecutablePath,
                        startMinimized ? AutostartMarker + " --minimized" : AutostartMarker);
                    RunKeyStore.Set(ValueName, cmd);
                    // 必须在写入 Run 值之后清除禁用标记：用户若曾在任务管理器里
                    // 禁用过本项，那个 0x03 标记会继续压制新写入的启动项，
                    // 结果就是复选框显示"已启用"但开机根本不启动。
                    ClearApprovalBlock();
                    Log.Info("已启用开机自启: " + cmd);
                }
                else
                {
                    RunKeyStore.Remove(ValueName);
                    Log.Info("已关闭开机自启");
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("设置开机自启失败", ex);
                return false;
            }
        }
    }
}
