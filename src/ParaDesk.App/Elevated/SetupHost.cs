using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using ParaDesk.Core;
using ParaDesk.Native;

namespace ParaDesk.Elevated
{
    /// <summary>提权子进程的执行结果。</summary>
    public enum SetupResult
    {
        Success = 0,
        Failed = 1,
        RebootRequired = 2,
        Cancelled = 3,
    }

    /// <summary>
    /// 一次性系统配置（需要管理员）。做四件事：
    /// 1) 启用子会话；2) 启用远程桌面监听器（子会话的必要前置）；
    /// 3) TermService 设为自动启动并尝试重启；4) 自检通道。
    /// TermService 在有活动控制台会话时通常停不掉，所以第 4 步失败是预期内的，
    /// 返回 RebootRequired 让主程序引导用户重启。
    /// </summary>
    internal static class SetupHost
    {
        public static int Run(string[] args)
        {
            if (args.Length > 1)
            {
                string dir = args[1].Trim('"');
                try { if (Path.IsPathRooted(dir)) Log.OverrideDir = dir; } catch { }
            }

            // 提权子进程是独立进程，不继承主进程的语言状态。
            // 不显式加载一次，它弹的对话框就永远是中文——英文用户会在配置的
            // 最后一步撞见一堆中文。
            try { L.Apply(SettingsStore.Load().Language); }
            catch (Exception ex) { Log.Warn("提权进程加载语言失败: " + ex.Message); }

            // 单独的性能设置分支：--setup <logdir> --fps <n>
            for (int i = 2; i + 1 < args.Length; i++)
            {
                if (!string.Equals(args[i], "--fps", StringComparison.OrdinalIgnoreCase)) continue;
                int fps;
                if (!int.TryParse(args[i + 1], out fps)) break;
                bool okFps = PerformanceSettings.ApplyFrameInterval(PerformanceSettings.FpsToInterval(fps));
                return okFps ? (int)SetupResult.Success : (int)SetupResult.Failed;
            }

            // 启用 Windows 沙盒可选功能：--setup <logdir> --sandbox
            for (int i = 2; i < args.Length; i++)
            {
                if (!string.Equals(args[i], "--sandbox", StringComparison.OrdinalIgnoreCase)) continue;
                return EnableSandboxFeature();
            }

            var sb = new StringBuilder();
            bool ok = true;

            ok &= Step(sb, L.T("启用子会话"), delegate
            {
                if (NativeMethods.WTSEnableChildSessions(true)) return null;
                return "Win32Error=" + Marshal.GetLastWin32Error();
            });

            ok &= Step(sb, L.T("启用远程桌面监听器"), delegate
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server",
                    "fDenyTSConnections", 0, RegistryValueKind.DWord);
                return null;
            });

            // 防火墙规则用与语言无关的组标识，避免中文系统上组名不匹配
            Step(sb, L.T("开放防火墙远程桌面规则"), delegate
            {
                return RunTool("powershell.exe",
                    "-NoProfile -NonInteractive -Command \"Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28752'\"");
            });

            // 免密登录的关键：允许把当前登录凭据委派给本机 TERMSRV。
            // 不设这个，每次连接都会弹凭据框——这正是"要重复输密码"的根因。
            Step(sb, L.T("允许委派默认凭据（免除重复输入密码）"), EnableCredentialDelegation);

            Step(sb, L.T("TermService 设为自动启动"), delegate
            {
                return RunTool("sc.exe", "config TermService start= auto");
            });

            Step(sb, L.T("重启 TermService"), delegate { return RestartTermService(); });

            // 早期版本往 HKLM 写过一个拦 CrossDeviceResume.exe 的映像劫持项。
            // 实测无效（拒绝发生在 CreateProcess 之前），这里顺手清掉，
            // 不给用户机器留没用的全局设置。
            if (CrossDeviceSettings.HasLegacyIfeo())
                Step(sb, L.T("清理无效的映像劫持项"), delegate
                {
                    return CrossDeviceSettings.RemoveLegacyIfeo() ? null : "删除失败";
                });

            string pipe;
            int status = SystemStatus.ProbeTransport(out pipe);
            sb.AppendLine("子会话通道自检 => " + SystemStatus.DescribeTransportError(status));

            Log.Info("[首次配置] " + sb.ToString().Replace("\r\n", " | "));

            if (!ok)
            {
                Show(sb, L.T("配置失败"), MessageBoxIcon.Error);
                return (int)SetupResult.Failed;
            }
            if (status != 0)
            {
                MessageBox.Show(
                    L.T("设置已全部写入，但子会话监听器需要重启电脑后才会启动。") + "\r\n\r\n" +
                    L.T("请重启电脑，然后直接启动桌面（无需再次配置）。") + "\r\n\r\n" +
                    L.T("详细信息：") + "\r\n" + sb,
                    AppInfo.ProductName + " — " + L.T("需要重启电脑"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return (int)SetupResult.RebootRequired;
            }
            return (int)SetupResult.Success;
        }

        /// <summary>执行一步；委托返回 null 表示成功，返回字符串表示失败原因。</summary>
        private static bool Step(StringBuilder sb, string name, Func<string> action)
        {
            try
            {
                string err = action();
                sb.AppendLine(name + " => " + (err == null ? L.T("成功") : L.T("失败: ") + err));
                return err == null;
            }
            catch (Exception ex)
            {
                sb.AppendLine(name + " => " + L.T("异常: ") + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 启用「允许委派默认凭据」并把本机 TERMSRV 加入白名单。
        /// 对应组策略：计算机配置 → 管理模板 → 系统 → 凭据分配 → 允许委派默认凭据。
        /// 同时关掉「始终提示输入密码」，两者任一未处理都会导致每次弹凭据框。
        /// </summary>
        private static string EnableCredentialDelegation()
        {
            const string root = @"SOFTWARE\Policies\Microsoft\Windows\CredentialsDelegation";
            using (var k = Registry.LocalMachine.CreateSubKey(root))
            {
                if (k == null) return "无法创建策略键";
                k.SetValue("AllowDefaultCredentials", 1, RegistryValueKind.DWord);
                // 与系统默认列表合并，避免覆盖企业环境中已有的条目
                k.SetValue("ConcatenateDefaults_AllowDefault", 1, RegistryValueKind.DWord);
            }

            // 白名单是子键，条目按 "1"、"2"… 递增命名
            using (var list = Registry.LocalMachine.CreateSubKey(root + @"\AllowDefaultCredentials"))
            {
                if (list == null) return "无法创建白名单键";

                string[] wanted = { "TERMSRV/localhost", "TERMSRV/*" };
                var existing = new System.Collections.Generic.List<string>();
                foreach (string name in list.GetValueNames())
                {
                    var v = list.GetValue(name) as string;
                    if (!string.IsNullOrEmpty(v)) existing.Add(v);
                }

                int next = 1;
                foreach (string w in wanted)
                {
                    if (existing.Contains(w)) continue;
                    while (list.GetValue(next.ToString()) != null) next++;
                    list.SetValue(next.ToString(), w, RegistryValueKind.String);
                    next++;
                }
            }

            // 「始终提示输入密码」若被启用，凭据委派也救不了
            try
            {
                using (var ts = Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services"))
                {
                    if (ts != null) ts.SetValue("fPromptForPassword", 0, RegistryValueKind.DWord);
                }
                Registry.SetValue(
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp",
                    "fPromptForPassword", 0, RegistryValueKind.DWord);
            }
            catch (Exception ex) { Log.Warn("关闭“始终提示输入密码”失败: " + ex.Message); }

            return null;
        }

        /// <summary>
        /// 启用 Windows 沙盒可选功能。
        ///
        /// 用 DISM 而不是 PowerShell 的 Enable-WindowsOptionalFeature：
        /// 后者在部分精简系统上缺 DISM 模块，而 dism.exe 是系统自带、一定在。
        /// /norestart 是必须的——这里不能替用户重启机器，只把"要重启"如实告诉他。
        /// 装组件比改注册表慢得多，超时给足 10 分钟。
        /// </summary>
        private static int EnableSandboxFeature()
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "dism.exe"),
                Arguments = "/online /enable-feature /featurename:Containers-DisposableClientVM /all /norestart",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            try
            {
                using (var p = Process.Start(psi))
                {
                    string outText = p.StandardOutput.ReadToEnd();
                    string errText = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(600000))
                    {
                        try { p.Kill(); } catch { }
                        Log.Error("[沙盒] DISM 超时", null);
                        return (int)SetupResult.Failed;
                    }

                    Log.Info("[沙盒] DISM 退出码 " + p.ExitCode +
                             (errText.Length > 0 ? " stderr=" + errText.Trim() : ""));

                    // 0 = 成功；3010 = 成功但需要重启，对我们来说是同一件事
                    if (p.ExitCode == 0 || p.ExitCode == 3010)
                        return (int)SetupResult.RebootRequired;

                    Log.Warn("[沙盒] 启用失败: " + outText.Trim());
                    return (int)SetupResult.Failed;
                }
            }
            catch (Exception ex)
            {
                Log.Error("[沙盒] 启用异常", ex);
                return (int)SetupResult.Failed;
            }
        }

        private static string RunTool(string exe, string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi))
            {
                if (!p.WaitForExit(30000)) return "超时";
                return p.ExitCode == 0 ? null : "退出码 " + p.ExitCode;
            }
        }

        private static string RestartTermService()
        {
            using (var sc = new ServiceController("TermService"))
            {
                sc.Refresh();
                if (sc.Status == ServiceControllerStatus.Running && sc.CanStop)
                {
                    try
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                    }
                    catch
                    {
                        // 有活动控制台会话时 TermService 停不掉，这是预期行为，靠重启电脑解决
                        return "服务无法停止（需重启电脑）";
                    }
                }
                sc.Refresh();
                if (sc.Status != ServiceControllerStatus.Running)
                {
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
                }
                sc.Refresh();
                return sc.Status == ServiceControllerStatus.Running ? null : sc.Status.ToString();
            }
        }

        private static void Show(StringBuilder sb, string title, MessageBoxIcon icon)
        {
            MessageBox.Show(sb + "\r\n" + L.T("日志：") + Log.Path0,
                AppInfo.ProductName + " — " + title, MessageBoxButtons.OK, icon);
        }
    }
}
