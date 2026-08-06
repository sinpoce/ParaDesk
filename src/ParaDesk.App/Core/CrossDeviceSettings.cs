using System;
using Microsoft.Win32;

namespace ParaDesk.Core
{
    /// <summary>
    /// 处理"分身桌面里弹 Windows 无效错误框"这件事。
    ///
    /// 走过的弯路值得记下来，免得以后又试一遍：
    ///   · 设置里关「移动设备」——实测 IsResumeAllowed 早就是 0，照弹；
    ///   · 组策略 EnableCdp=0 / EnableMmx=0——这台机器上本来就是 0，照弹；
    ///   · 映像劫持（IFEO）把 CrossDeviceResume.exe 重定向到空壳——照弹。
    /// IFEO 无效说明拒绝发生在 CreateProcess **之前**，调用方多半是 AppContainer 进程，
    /// 它本来就没有拉起 Win32 可执行文件的权限。根因在微软那边，改不了。
    ///
    /// 所以改成在子会话里放一个守护进程，专门关掉那个框（见 ChildSessionAgent）。
    /// 注册在 HKCU 的 Run 键下——子会话是同一个账户的完整交互登录，
    /// 会照常执行这个键，因此**完全不需要管理员权限**。
    /// </summary>
    internal static class CrossDeviceSettings
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ParaDeskChildAgent";

        /// <summary>「跨设备恢复」的用户级开关。</summary>
        private const string ResumeKey =
            @"Software\Microsoft\Windows\CurrentVersion\CrossDeviceResume\Configuration";

        /// <summary>早期版本写过的映像劫持项，现已证明无效，加载时顺手清掉。</summary>
        private const string LegacyIfeoKey =
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\CrossDeviceResume.exe";

        public static bool IsEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    return k != null && k.GetValue(ValueName) != null;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// 配置系统时一并做的"消掉那个错误框"处理：先按官方开关把跨设备恢复关掉，
        /// 再装上守护兜底。
        ///
        /// 为什么两件都做：官方开关在某些机器上也许真能拦住那次拉起（无从验证，
        /// 至少它是正当做法、代价为零）；但在这台机器上实测三个开关本来就是关的、
        /// 照样弹，所以不能只靠它。守护才是保证。
        ///
        /// 刻意由**非提权**的主进程调用：这些都写在 HKCU，
        /// 而 UAC 提权后的子进程有可能挂在另一个管理员账户下，那样就写错人了。
        /// </summary>
        public static bool ApplyAll(bool enable)
        {
            bool ok = Apply(enable);

            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(ResumeKey))
                {
                    if (k != null)
                    {
                        if (enable)
                        {
                            k.SetValue("IsResumeAllowed", 0, RegistryValueKind.DWord);
                            k.SetValue("IsOneDriveResumeAllowed", 0, RegistryValueKind.DWord);
                            Log.Info("已关闭跨设备恢复的用户开关");
                        }
                        else
                        {
                            // 恢复时把我们写的值删掉，而不是写回 1——
                            // 本来是什么状态由 Windows 自己决定，我们不替它做主
                            if (k.GetValue("IsResumeAllowed") != null) k.DeleteValue("IsResumeAllowed", false);
                            if (k.GetValue("IsOneDriveResumeAllowed") != null) k.DeleteValue("IsOneDriveResumeAllowed", false);
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Warn("写入跨设备用户开关失败: " + ex.Message); }

            return ok;
        }

        /// <summary>注册／注销子会话守护。不需要提权。</summary>
        public static bool Apply(bool enable)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return false;
                    if (enable)
                    {
                        k.SetValue(ValueName,
                            "\"" + AppInfo.ExecutablePath + "\" --childagent",
                            RegistryValueKind.String);
                        Log.Info("已启用子会话守护（关闭 Windows 无效错误框）");
                    }
                    else
                    {
                        if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
                        Log.Info("已关闭子会话守护");
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("写入子会话守护启动项失败", ex);
                return false;
            }
        }

        /// <summary>是否还残留着那个无效的映像劫持项。</summary>
        public static bool HasLegacyIfeo()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(LegacyIfeoKey))
                {
                    return k != null && k.GetValue("Debugger") != null;
                }
            }
            catch { return false; }
        }

        /// <summary>删掉那个无效的映像劫持项（需要管理员，由提权子进程调用）。</summary>
        public static bool RemoveLegacyIfeo()
        {
            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(LegacyIfeoKey, false);
                Log.Info("已删除遗留的映像劫持项");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("删除映像劫持项失败", ex);
                return false;
            }
        }
    }
}
