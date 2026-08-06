using System;
using Microsoft.Win32;

namespace ParaDesk.Core
{
    /// <summary>
    /// 开机自启。用 HKCU\...\Run 而非计划任务：本程序非提权运行，Run 键足够，
    /// 且用户能在「设置 → 应用 → 启动」里看到并自行关闭，符合预期。
    /// </summary>
    internal static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = AppInfo.ProductName;

        private const string ApprovedKey =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        /// <summary>
        /// 任务管理器/设置里禁用启动项时**不会**删除 Run 值，而是往 StartupApproved\Run
        /// 写一个 12 字节二进制：首字节 0x02/0x06=启用，0x03=禁用。
        /// 不读它的话，用户在系统里关掉后我们的复选框仍显示"已启用"（说谎）。
        /// </summary>
        private static bool IsApproved()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKey, false))
                {
                    if (k == null) return true;   // 没有该项即视为启用
                    var blob = k.GetValue(ValueName) as byte[];
                    if (blob == null || blob.Length == 0) return true;
                    return blob[0] != 0x03;
                }
            }
            catch { return true; }
        }

        /// <summary>用户重新启用时必须清掉禁用标记，否则新写的 Run 值仍会被系统抑制。</summary>
        private static void ClearApprovalBlock()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                {
                    if (k == null) return;
                    if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
                }
            }
            catch (Exception ex) { Log.Error("清除启动项禁用标记失败", ex); }
        }

        public static bool IsEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    var v = k.GetValue(ValueName) as string;
                    if (string.IsNullOrEmpty(v)) return false;
                    return IsApproved();
                }
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
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return false;
                    if (enabled)
                    {
                        string cmd = "\"" + AppInfo.ExecutablePath + "\"";
                        if (startMinimized) cmd += " --minimized";
                        k.SetValue(ValueName, cmd, RegistryValueKind.String);
                        // 必须在写入 Run 值之后清除禁用标记：用户若曾在任务管理器里
                        // 禁用过本项，那个 0x03 标记会继续压制新写入的启动项，
                        // 结果就是复选框显示"已启用"但开机根本不启动。
                        ClearApprovalBlock();
                        Log.Info("已启用开机自启: " + cmd);
                    }
                    else
                    {
                        if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
                        Log.Info("已关闭开机自启");
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error("设置开机自启失败", ex);
                return false;
            }
        }
    }
}
