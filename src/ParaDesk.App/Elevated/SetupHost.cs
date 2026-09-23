using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
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

    internal static class SetupHost
    {
        private const string TsKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
        private const string RdpTcpKey = TsKey + @"\WinStations\RDP-Tcp";
        private const string TermServiceKey = @"SYSTEM\CurrentControlSet\Services\TermService";
        private const string CredDelegKey = @"SOFTWARE\Policies\Microsoft\Windows\CredentialsDelegation";
        private const string AllowListKey = CredDelegKey + @"\AllowDefaultCredentials";
        private const string TsPolicyParentKey = @"SOFTWARE\Policies\Microsoft\Windows NT";
        private const string TsPolicyKey = TsPolicyParentKey + @"\Terminal Services";

        private static readonly string[] CreatableKeys = { AllowListKey, CredDelegKey, TsPolicyKey, TsPolicyParentKey };

        private const string RdpFirewallGroup = "@FirewallAPI.dll,-28752";

        private const string LocalhostSpn = "TERMSRV/localhost";
        private const string LoopbackIpSpn = "TERMSRV/127.0.0.1";
        private const string WildcardSpn = "TERMSRV/*";

        private const string MarkerKey = @"SOFTWARE\ParaDesk";
        private const string MarkerValue = "SetupApplied";

        private const string BackupFileName = "setup-backup.json";
        private const string ForeignBackupSuffix = ".other-machine";
        private const int BackupVersion = 1;

        private const int FirewallProfilePublic = 4;

        internal sealed class SetupArgs
        {
            public string LogDir;
            public bool HasFps;
            public string FpsText;
            public bool Sandbox;
            public bool Undo;
            public List<string> Unknown = new List<string>();
        }

        internal static SetupArgs ParseArgs(string[] args)
        {
            var a = new SetupArgs();
            if (args == null) return a;

            int i = 1;
            if (args.Length > 1 && args[1] != null &&
                !args[1].StartsWith("-", StringComparison.Ordinal) && !args[1].StartsWith("/", StringComparison.Ordinal))
            {
                a.LogDir = args[1].Trim().Trim('"');
                i = 2;
            }

            for (; i < args.Length; i++)
            {
                string s = args[i] ?? "";
                if (s.Trim().Length == 0) continue;
                if (string.Equals(s, "--fps", StringComparison.OrdinalIgnoreCase))
                {
                    a.HasFps = true;
                    if (i + 1 < args.Length) a.FpsText = args[++i];
                }
                else if (string.Equals(s, "--sandbox", StringComparison.OrdinalIgnoreCase)) a.Sandbox = true;
                else if (string.Equals(s, "--undo", StringComparison.OrdinalIgnoreCase)) a.Undo = true;
                else a.Unknown.Add(s);
            }
            return a;
        }

        public static int Run(string[] args)
        {
            Log.ProcessTag = "setup";

            SetupArgs a = ParseArgs(args);
            if (!string.IsNullOrEmpty(a.LogDir))
            {
                try
                {
                    if (Path.IsPathRooted(a.LogDir)) Log.OverrideDir = a.LogDir;
                    else Log.Warn("提权进程收到的日志目录不是绝对路径，改用默认目录: " + a.LogDir);
                }
                catch (ArgumentException ex)
                {
                    Log.Warn("提权进程收到的日志目录无效，改用默认目录: " + ex.Message);
                }
            }

            // 提权子进程是独立进程，不继承主进程的语言状态。
            // 不显式加载一次，它弹的对话框就永远是中文——英文用户会在配置的
            // 最后一步撞见一堆中文。
            try { L.Apply(SettingsStore.Load().Language); }
            catch (Exception ex) { Log.Warn("提权进程加载语言失败: " + ex.Message); }

            if (a.Unknown.Count > 0)
            {
                string bad = string.Join(" ", a.Unknown.ToArray());
                Log.Error("提权进程收到不认识的参数，未做任何改动: " + bad);
                MessageBox.Show(string.Format(L.T("不认识的参数：{0}"), bad) + "\r\n\r\n" + L.T("没有对系统做任何改动。"),
                    AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return (int)SetupResult.Failed;
            }
            if ((a.HasFps ? 1 : 0) + (a.Sandbox ? 1 : 0) + (a.Undo ? 1 : 0) > 1)
                Log.Warn("提权进程同时收到多个动作开关，只执行优先级最高的一个（--fps > --sandbox > --undo）");

            // 单独的性能设置分支：--setup <logdir> --fps <n>
            if (a.HasFps)
            {
                int fps;
                if (!int.TryParse(a.FpsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out fps))
                {
                    Log.Error("--fps 参数无效: " + (a.FpsText ?? "(缺失)"));
                    return (int)SetupResult.Failed;
                }
                bool okFps = PerformanceSettings.ApplyFrameInterval(PerformanceSettings.FpsToInterval(fps));
                return okFps ? (int)SetupResult.Success : (int)SetupResult.Failed;
            }

            // 启用 Windows 沙盒可选功能：--setup <logdir> --sandbox
            if (a.Sandbox) return EnableSandboxFeature();

            if (a.Undo) return RunUndo();

            return RunFullSetup();
        }

        private static int RunFullSetup()
        {
            var sb = new StringBuilder();
            bool ok = true;

            string fwErr;
            List<FirewallRuleBackup> fwRules = QueryFirewallRules(out fwErr);

            BackupPlan plan = PrepareBackup(sb, fwRules);

            WriteSetupMarker();

            ok &= Step(sb, L.T("启用子会话"), delegate
            {
                if (NativeMethods.WTSEnableChildSessions(true)) return null;
                return "Win32Error=" + Marshal.GetLastWin32Error();
            });

            ok &= Step(sb, L.T("启用远程桌面监听器"), delegate
            {
                using (var hklm = OpenHklm())
                using (var k = hklm.CreateSubKey(TsKey))
                {
                    if (k == null) return L.T("无法打开注册表键");
                    k.SetValue("fDenyTSConnections", 0, RegistryValueKind.DWord);
                }
                return null;
            });

            bool fwOk = StepEx(sb, L.T("开放防火墙远程桌面规则（仅专用/域网络）"), delegate(List<string> d)
            {
                return EnableFirewallRules(fwRules, fwErr, d);
            });
            if (fwRules != null) ok &= fwOk;
            else Log.Warn("[防火墙] 查询失败，这一步不计入配置结果: " + fwErr);

            if (ShouldRemoveLegacyWildcard(plan.LegacyWildcard))
                StepEx(sb, L.T("移除旧版写入的 TERMSRV/*（它会把默认凭据放行给任何远程桌面服务器）"),
                    RemoveLegacyWildcard);

            // 免密登录的关键：允许把当前登录凭据委派给本机 TERMSRV。
            // 不设这个，每次连接都会弹凭据框——这正是"要重复输密码"的根因。
            StepEx(sb, L.T("允许委派默认凭据（免除重复输入密码）"), EnableCredentialDelegation);

            // 「始终提示输入密码」若被启用，凭据委派也救不了
            Step(sb, L.T("关闭「始终提示输入密码」"), DisablePromptForPassword);

            Step(sb, L.T("TermService 设为自动启动"), delegate
            {
                return RunTool(ScExe(), "config TermService start= auto");
            });

            Step(sb, L.T("重启 TermService"), delegate { return RestartTermService(); });

            // 早期版本往 HKLM 写过一个拦 CrossDeviceResume.exe 的映像劫持项。
            // 实测无效（拒绝发生在 CreateProcess 之前），这里顺手清掉，
            // 不给用户机器留没用的全局设置。
            if (CrossDeviceSettings.HasLegacyIfeo())
                Step(sb, L.T("清理无效的映像劫持项"), delegate
                {
                    return CrossDeviceSettings.RemoveLegacyIfeo() ? null : L.T("删除失败，详见日志");
                });

            string pipe;
            int status = SystemStatus.ProbeTransport(out pipe);
            sb.AppendLine(L.T("子会话通道自检") + " => " + SystemStatus.DescribeTransportError(status));

            WriteSetupMarker();

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
            if (plan.RecordFailed)
            {
                MessageBox.Show(
                    L.T("配置已完成，但没能记录配置前的系统状态。之后撤销配置时，只能撤销能确定是 ParaDesk 做的改动。") +
                    "\r\n\r\n" + L.T("详细信息：") + "\r\n" + sb + "\r\n" + L.T("日志：") + Log.Path0,
                    AppInfo.ProductName + " — " + L.T("配置完成"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return (int)SetupResult.Success;
        }

        private static bool Step(StringBuilder sb, string name, Func<string> action)
        {
            return StepEx(sb, name, delegate(List<string> d) { return action(); });
        }

        private static bool StepEx(StringBuilder sb, string name, Func<List<string>, string> action)
        {
            var details = new List<string>();
            bool ok;
            try
            {
                string err = action(details);
                ok = err == null;
                sb.AppendLine(name + " => " + (ok ? L.T("成功") : L.T("失败: ") + err));
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine(name + " => " + L.T("异常: ") + ex.Message);
                Log.Error("[配置] 步骤异常: " + name, ex);
            }
            foreach (string line in details) sb.AppendLine("    · " + line);
            return ok;
        }

        private static string EnableFirewallRules(List<FirewallRuleBackup> rules, string queryError, List<string> d)
        {
            if (rules == null)
            {
                d.Add(L.T("子会话走本机回环，不依赖这些规则；这一步失败不影响分身桌面"));
                return L.T("查询防火墙规则失败") + ": " + queryError;
            }

            var targets = new List<string>();
            int publicEnabled = 0;
            foreach (FirewallRuleBackup r in rules)
            {
                if (!IsPrivateOnlyProfile(r.Profile))
                {
                    if (r.Enabled)
                    {
                        publicEnabled++;
                        d.Add(string.Format(L.T("规则 {0} 对公共网络也生效，且已是启用状态（可能是旧版本或你自己启用的），未改动"), r.Name));
                        Log.Warn("[防火墙] 对公共网络也生效的规则 " + r.Name + " 已是启用状态（Profile=" + r.Profile +
                                 "），3389 在公共网络上也开放；来源不明，未改动");
                    }
                    else
                    {
                        d.Add(string.Format(L.T("规则 {0} 对公共网络也生效，跳过"), r.Name));
                        Log.Info("[防火墙] 跳过对公共网络也生效的规则 " + r.Name + "（Profile=" + r.Profile + "）");
                    }
                    continue;
                }
                if (r.Enabled)
                {
                    d.Add(string.Format(L.T("规则 {0} 已是启用状态"), r.Name));
                    continue;
                }
                targets.Add(r.Name);
            }

            if (targets.Count == 0)
            {
                d.Add(publicEnabled == 0
                    ? L.T("没有需要启用的规则：子会话走本机回环，不需要对外开放 3389 端口")
                    : L.T("没有需要启用的规则。上面已启用的规则让 3389 端口在公共网络上也开放；分身桌面用不到它们，不需要从别的电脑远程连这台机器的话，可以在「Windows Defender 防火墙 → 高级设置」里停用"));
                return null;
            }

            string err = SetFirewallRulesEnabled(targets, true);
            if (err != null) return err;
            foreach (string n in targets) d.Add(string.Format(L.T("已启用规则 {0}"), n));
            Log.Info("[防火墙] 已启用 " + string.Join(", ", targets.ToArray()));
            return null;
        }

        private static bool IsPrivateOnlyProfile(int profile)
        {
            return profile != 0 && (profile & FirewallProfilePublic) == 0;
        }

        private static bool ShouldRemoveLegacyWildcard(bool legacyWildcard)
        {
            if (!legacyWildcard) return false;
            List<NamedString> current;
            try
            {
                using (var hklm = OpenHklm()) current = ReadAllowList(hklm);
            }
            catch (Exception ex)
            {
                Log.Warn("读取凭据委派白名单失败，跳过旧版通配条目检查: " + ex.Message);
                return false;
            }
            return ListContains(current, WildcardSpn);
        }

        private static string RemoveLegacyWildcard(List<string> d)
        {
            using (var hklm = OpenHklm())
            using (var list = hklm.OpenSubKey(AllowListKey, true))
            {
                if (list == null) return null;
                foreach (string name in list.GetValueNames())
                {
                    string v = list.GetValue(name) as string;
                    if (!string.Equals(v, WildcardSpn, StringComparison.OrdinalIgnoreCase)) continue;
                    list.DeleteValue(name, false);
                    d.Add(string.Format(L.T("已删除条目 {0}"), v));
                }
            }
            Log.Warn("已删除凭据委派白名单里的 TERMSRV/*：它会让本机把默认登录凭据自动委派给任何远程桌面服务器" +
                     "（包括仿冒的），而回环子会话只需要 TERMSRV/localhost。配置前子会话已开启、白名单同时有它与 TERMSRV/localhost，" +
                     "是旧版 ParaDesk 写入的特征。");
            return null;
        }

        private static string EnableCredentialDelegation(List<string> d)
        {
            using (var hklm = OpenHklm())
            {
                using (var k = hklm.CreateSubKey(CredDelegKey))
                {
                    if (k == null) return L.T("无法创建策略键");
                    k.SetValue("AllowDefaultCredentials", 1, RegistryValueKind.DWord);
                    // 与系统默认列表合并，避免覆盖企业环境中已有的条目
                    k.SetValue("ConcatenateDefaults_AllowDefault", 1, RegistryValueKind.DWord);
                }

                using (var list = hklm.CreateSubKey(AllowListKey))
                {
                    if (list == null) return L.T("无法创建白名单键");

                    List<NamedString> existing = ReadValues(list);
                    foreach (string spn in OurSpns(null))
                    {
                        if (ListContains(existing, spn))
                        {
                            d.Add(string.Format(L.T("{0} 已在白名单中"), spn));
                            continue;
                        }
                        int next = 1;
                        while (list.GetValue(next.ToString(CultureInfo.InvariantCulture)) != null) next++;
                        string name = next.ToString(CultureInfo.InvariantCulture);
                        list.SetValue(name, spn, RegistryValueKind.String);
                        existing.Add(new NamedString { Name = name, Value = spn });
                        d.Add(string.Format(L.T("已添加 {0}"), spn));
                    }

                    if (ListContains(existing, WildcardSpn))
                    {
                        d.Add(L.T("白名单里另有 TERMSRV/*：它会把默认凭据放行给任何远程桌面服务器。分身桌面不需要它，如果不是你有意添加的，可以在组策略「允许委派默认凭据」里删掉"));
                        Log.Warn("凭据委派白名单里有 TERMSRV/*，没有证据表明是旧版 ParaDesk 写的，保留未动");
                    }
                }
            }
            return null;
        }

        private static string DisablePromptForPassword()
        {
            using (var hklm = OpenHklm())
            {
                using (var ts = hklm.CreateSubKey(TsPolicyKey))
                {
                    if (ts == null) return L.T("无法创建策略键");
                    ts.SetValue("fPromptForPassword", 0, RegistryValueKind.DWord);
                }
                using (var rdp = hklm.CreateSubKey(RdpTcpKey))
                {
                    if (rdp == null) return L.T("无法打开注册表键");
                    rdp.SetValue("fPromptForPassword", 0, RegistryValueKind.DWord);
                }
            }
            return null;
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
                    catch (InvalidOperationException ex)
                    {
                        // 有活动控制台会话时 TermService 停不掉，这是预期行为，靠重启电脑解决
                        Log.Info("TermService 停不下来（预期内，需重启电脑）: " + ex.Message);
                        return L.T("服务无法停止（需重启电脑）");
                    }
                    catch (System.ServiceProcess.TimeoutException ex)
                    {
                        Log.Info("TermService 停止超时（预期内，需重启电脑）: " + ex.Message);
                        return L.T("服务无法停止（需重启电脑）");
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

        private static string BackupPath()
        {
            return Path.Combine(Log.Dir, BackupFileName);
        }

        private sealed class BackupPlan
        {
            public SetupBackup Backup;
            public bool RecordFailed;
            public bool LegacyWildcard;
        }

        private static BackupPlan PrepareBackup(StringBuilder sb, List<FirewallRuleBackup> fwRules)
        {
            var plan = new BackupPlan();
            string path = BackupPath();
            string name = L.T("备份配置前的系统状态（供撤销使用）");
            if (File.Exists(path))
            {
                bool corrupt;
                SetupBackup existing = LoadBackup(path, out corrupt);
                if (existing == null || !IsForeignBackup(existing))
                {
                    sb.AppendLine(name + " => " + (corrupt
                        ? L.T("已有备份文件但无法读取，保留原文件不覆盖")
                        : L.T("已有备份，保留第一次配置之前的状态")));
                    if (existing != null) TopUpFirewallBackup(path, existing, fwRules);
                    plan.Backup = existing;
                    plan.LegacyWildcard = existing != null && existing.LegacyParaDeskEntries;
                    return plan;
                }

                string moved = path + ForeignBackupSuffix;
                try
                {
                    if (File.Exists(moved)) File.Delete(moved);
                    File.Move(path, moved);
                    Log.Warn("[首次配置] 已有的配置备份来自另一台电脑（" + existing.MachineName + "，机器标识不符），已改名为 " + moved);
                    sb.AppendLine(name + " => " + string.Format(
                        L.T("已有的备份来自另一台电脑，已改名为 {0}，重新记录本机的状态"), Path.GetFileName(moved)));
                }
                catch (Exception ex)
                {
                    Log.Warn("[首次配置] 来自另一台电脑的配置备份改名失败，本次不记录配置前状态: " + ex.Message);
                    sb.AppendLine(name + " => " + L.T("失败: ") +
                                  L.T("已有的备份来自另一台电脑，且无法改名；本次没有记录，撤销时只能尽力而为"));
                    plan.RecordFailed = true;
                    return plan;
                }
            }

            string trace = FindConfiguredTrace();
            if (trace != null)
            {
                sb.AppendLine(name + " => " + L.T("本机已有 ParaDesk 的配置，但找不到配置前的备份：此刻已是配置后的状态，不作为原状态记录；撤销时只撤销能确定是 ParaDesk 做的改动"));
                Log.Warn("[首次配置] 本机已被 ParaDesk 配置过（" + trace + "），但没有配置前的备份" +
                         "（数据目录被删、第一次备份没写成或换了 Windows 账户）；此刻是配置后的状态，不记为原状态");
                return plan;
            }

            SetupBackup created = null;
            StepEx(sb, name, delegate(List<string> d)
            {
                SetupBackup b = CaptureBackup(fwRules);
                SaveBackup(path, b, false);
                created = b;
                d.Add(string.Format(L.T("备份文件：{0}"), path));
                return null;
            });
            plan.Backup = created;
            if (created != null)
            {
                plan.LegacyWildcard = created.LegacyParaDeskEntries;
            }
            else
            {
                Log.Warn("配置前备份失败，之后的撤销只能尽力而为");
                plan.RecordFailed = true;
                plan.LegacyWildcard = LooksLikeLegacyConfigNow();
            }
            return plan;
        }

        private static string FindConfiguredTrace()
        {
            try
            {
                using (var hklm = OpenHklm())
                {
                    using (var k = hklm.OpenSubKey(MarkerKey))
                    {
                        if (k != null && k.GetValue(MarkerValue) != null)
                            return "HKLM\\" + MarkerKey + "\\" + MarkerValue;
                    }
                    bool on;
                    if (NativeMethods.WTSIsChildSessionsEnabled(out on) && on)
                    {
                        List<NamedString> list = ReadAllowList(hklm);
                        if (ListContains(list, LocalhostSpn) && ListContains(list, LoopbackIpSpn))
                            return "子会话已开启，白名单同时有 " + LocalhostSpn + " 与 " + LoopbackIpSpn;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("检查本机是否已被 ParaDesk 配置过失败: " + ex.Message);
            }
            return null;
        }

        private static bool IsLegacySignature(bool childSessionsOn, List<NamedString> allowList)
        {
            return childSessionsOn && ListContains(allowList, LocalhostSpn) && ListContains(allowList, WildcardSpn);
        }

        private static bool LooksLikeLegacyConfigNow()
        {
            try
            {
                bool on;
                if (!NativeMethods.WTSIsChildSessionsEnabled(out on) || !on) return false;
                using (var hklm = OpenHklm()) return IsLegacySignature(true, ReadAllowList(hklm));
            }
            catch (Exception ex)
            {
                Log.Warn("检查旧版 ParaDesk 配置特征失败: " + ex.Message);
                return false;
            }
        }

        private static void WriteSetupMarker()
        {
            try
            {
                using (var hklm = OpenHklm())
                using (var k = hklm.CreateSubKey(MarkerKey))
                {
                    if (k != null) k.SetValue(MarkerValue, 1, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("写入配置标记 HKLM\\" + MarkerKey + " 失败: " + ex.Message);
            }
        }

        private static void RemoveSetupMarker()
        {
            try
            {
                using (var hklm = OpenHklm())
                {
                    using (var k = hklm.OpenSubKey(MarkerKey, true))
                    {
                        if (k == null) return;
                        k.DeleteValue(MarkerValue, false);
                    }
                    using (var k = hklm.OpenSubKey(MarkerKey))
                    {
                        if (k == null || k.ValueCount != 0 || k.SubKeyCount != 0) return;
                    }
                    hklm.DeleteSubKey(MarkerKey, false);
                    Log.Info("[撤销配置] 已删除配置标记 HKLM\\" + MarkerKey);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[撤销配置] 删除配置标记失败: " + ex.Message);
            }
        }

        private static string CurrentMachineIdHash()
        {
            try
            {
                using (var hklm = OpenHklm())
                using (var k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    string guid = k == null ? null : k.GetValue("MachineGuid") as string;
                    if (string.IsNullOrEmpty(guid) || guid.Trim().Length == 0) return null;
                    using (SHA256 sha = SHA256.Create())
                    {
                        byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes("ParaDesk/" + guid.Trim().ToLowerInvariant()));
                        var hex = new StringBuilder(h.Length * 2);
                        foreach (byte x in h) hex.Append(x.ToString("x2", CultureInfo.InvariantCulture));
                        return hex.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取本机标识失败，不核对备份是否来自本机: " + ex.Message);
                return null;
            }
        }

        private static bool IsForeignBackup(SetupBackup b)
        {
            if (b == null || string.IsNullOrEmpty(b.MachineIdHash)) return false;
            string cur = CurrentMachineIdHash();
            if (string.IsNullOrEmpty(cur)) return false;
            return !string.Equals(b.MachineIdHash, cur, StringComparison.OrdinalIgnoreCase);
        }

        private static SetupBackup CaptureBackup(List<FirewallRuleBackup> fwRules)
        {
            var b = new SetupBackup();
            b.Version = BackupVersion;
            b.Note = "ParaDesk 第一次配置前的系统状态，撤销配置时据此恢复。请勿手动修改。";
            b.CreatedAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
            b.MachineName = Environment.MachineName;
            b.CreatedBy = Environment.UserDomainName + "\\" + Environment.UserName;
            b.MachineIdHash = CurrentMachineIdHash();

            bool enabled;
            b.ChildSessionsKnown = NativeMethods.WTSIsChildSessionsEnabled(out enabled);
            b.ChildSessionsEnabled = b.ChildSessionsKnown && enabled;

            using (var hklm = OpenHklm())
            {
                b.DenyTsConnections = ReadDword(hklm, TsKey, "fDenyTSConnections");
                b.AllowDefaultCredentials = ReadDword(hklm, CredDelegKey, "AllowDefaultCredentials");
                b.ConcatenateDefaults = ReadDword(hklm, CredDelegKey, "ConcatenateDefaults_AllowDefault");
                b.AllowList = ReadAllowList(hklm);
                b.PromptForPasswordPolicy = ReadDword(hklm, TsPolicyKey, "fPromptForPassword");
                b.PromptForPasswordRdpTcp = ReadDword(hklm, RdpTcpKey, "fPromptForPassword");
                b.TermServiceStart = ReadDword(hklm, TermServiceKey, "Start");
                b.TermServiceDelayed = ReadDword(hklm, TermServiceKey, "DelayedAutostart");

                b.KeysAbsent = new List<string>();
                foreach (string key in CreatableKeys)
                {
                    using (var k = hklm.OpenSubKey(key))
                    {
                        if (k == null) b.KeysAbsent.Add(key);
                    }
                }
            }

            b.LegacyParaDeskEntries = IsLegacySignature(b.ChildSessionsKnown && b.ChildSessionsEnabled, b.AllowList);

            b.FirewallQueried = fwRules != null;
            b.FirewallRules = PrivateOnlyRules(fwRules);

            Log.Info("[首次配置] 已记录配置前状态：子会话=" +
                     (b.ChildSessionsKnown ? (b.ChildSessionsEnabled ? "开" : "关") : "未知") +
                     "，fDenyTSConnections=" + DescribeDword(b.DenyTsConnections) +
                     "，白名单 " + b.AllowList.Count + " 条" + (b.LegacyParaDeskEntries ? "（含旧版 ParaDesk 条目）" : "") +
                     "，TermService Start=" + DescribeDword(b.TermServiceStart) +
                     "，待启用防火墙规则 " + b.FirewallRules.Count + " 条" +
                     (b.MachineIdHash == null ? "，未能读到本机标识" : ""));
            return b;
        }

        private static List<FirewallRuleBackup> PrivateOnlyRules(List<FirewallRuleBackup> all)
        {
            var list = new List<FirewallRuleBackup>();
            if (all != null)
            {
                foreach (FirewallRuleBackup r in all)
                    if (r != null && IsPrivateOnlyProfile(r.Profile)) list.Add(r);
            }
            return list;
        }

        private static void TopUpFirewallBackup(string path, SetupBackup existing, List<FirewallRuleBackup> fwRules)
        {
            if (existing.FirewallQueried || fwRules == null) return;
            existing.FirewallQueried = true;
            existing.FirewallRules = PrivateOnlyRules(fwRules);
            try
            {
                SaveBackup(path, existing, true);
                Log.Info("[首次配置] 已为已有备份补记防火墙规则状态：" + existing.FirewallRules.Count + " 条");
            }
            catch (Exception ex)
            {
                Log.Warn("补记防火墙备份失败，撤销时防火墙只能保持现状: " + ex.Message);
            }
        }

        private static void SaveBackup(string path, SetupBackup b, bool overwrite)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, Encoding.UTF8, false, true))
            {
                new DataContractJsonSerializer(typeof(SetupBackup)).WriteObject(w, b);
                w.Flush();
            }
            if (!overwrite)
            {
                if (File.Exists(path))
                {
                    File.Delete(tmp);
                    return;
                }
                File.Move(tmp, path);
                return;
            }

            if (!File.Exists(path))
            {
                File.Delete(tmp);
                throw new FileNotFoundException("配置备份已不存在，未补记", path);
            }
            try
            {
                File.Replace(tmp, path, null);
            }
            catch (Exception)
            {
                try { File.Delete(tmp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                throw;
            }
        }

        private static SetupBackup LoadBackup(string path, out bool corrupt)
        {
            corrupt = false;
            if (!File.Exists(path)) return null;
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    var b = new DataContractJsonSerializer(typeof(SetupBackup)).ReadObject(fs) as SetupBackup;
                    if (b == null || b.Version < 1)
                    {
                        corrupt = true;
                        Log.Warn("配置备份文件内容无效: " + path);
                        return null;
                    }
                    if (b.AllowList == null) b.AllowList = new List<NamedString>();
                    if (b.FirewallRules == null) b.FirewallRules = new List<FirewallRuleBackup>();
                    if (b.KeysAbsent == null) b.KeysAbsent = new List<string>();
                    return b;
                }
            }
            catch (Exception ex)
            {
                corrupt = true;
                Log.Warn("读取配置备份文件失败: " + path + " :: " + ex.Message);
                return null;
            }
        }

        private static int RunUndo()
        {
            string path = BackupPath();
            bool corrupt;
            SetupBackup backup = LoadBackup(path, out corrupt);
            bool foreign = false;
            if (backup != null && IsForeignBackup(backup))
            {
                Log.Warn("[撤销配置] 配置备份来自另一台电脑（" + backup.MachineName + "，机器标识不符），按没有备份处理: " + path);
                foreign = true;
                backup = null;
            }
            bool legacy = backup != null && backup.LegacyParaDeskEntries;
            bool trusted = backup != null && !legacy;
            Log.Info("[撤销配置] 开始；备份=" +
                     (backup != null ? path + (legacy ? "（建立于旧版配置之后）" : "")
                      : foreign ? "来自另一台电脑" : corrupt ? "无法读取" : "无"));

            var sb = new StringBuilder();
            bool ok = true;
            bool rebootRequired = false;

            ok &= StepEx(sb, L.T("关闭 Windows 的子会话功能"), delegate(List<string> d)
            {
                if (trusted && backup.ChildSessionsKnown && backup.ChildSessionsEnabled)
                {
                    d.Add(L.T("配置前就已开启，保持不变"));
                    return null;
                }
                bool cur;
                if (NativeMethods.WTSIsChildSessionsEnabled(out cur) && !cur)
                {
                    d.Add(L.T("已是关闭状态"));
                    return null;
                }
                if (!NativeMethods.WTSEnableChildSessions(false))
                    return "Win32Error=" + Marshal.GetLastWin32Error();
                rebootRequired = true;
                return null;
            });

            if (trusted)
            {
                ok &= StepEx(sb, L.T("恢复远程桌面监听器设置"), delegate(List<string> d)
                {
                    using (var hklm = OpenHklm())
                        return RestoreDword(hklm, TsKey, "fDenyTSConnections", "fDenyTSConnections",
                            backup.DenyTsConnections, d);
                });

                ok &= StepEx(sb, L.T("恢复防火墙远程桌面规则"), delegate(List<string> d)
                {
                    return UndoFirewall(backup, d);
                });
            }

            ok &= StepEx(sb, L.T("撤销凭据委派设置"), delegate(List<string> d)
            {
                return UndoCredentialDelegation(backup, d);
            });

            if (trusted)
            {
                ok &= StepEx(sb, L.T("恢复「始终提示输入密码」设置"), delegate(List<string> d)
                {
                    return UndoPromptForPassword(backup, d);
                });

                ok &= StepEx(sb, L.T("恢复 TermService 启动类型"), delegate(List<string> d)
                {
                    return UndoTermServiceStart(backup, d);
                });
            }

            if (CrossDeviceSettings.HasLegacyIfeo())
                ok &= Step(sb, L.T("清理无效的映像劫持项"), delegate
                {
                    return CrossDeviceSettings.RemoveLegacyIfeo() ? null : L.T("删除失败，详见日志");
                });

            List<string> publicOpen = trusted ? null : FindPublicEnabledRules();

            Log.Info("[撤销配置] " + sb.ToString().Replace("\r\n", " | "));

            if (ok)
            {
                RemoveSetupMarker();
                RetireBackup(path, corrupt, foreign);
            }

            SetupResult result = !ok ? SetupResult.Failed
                : rebootRequired ? SetupResult.RebootRequired
                : SetupResult.Success;
            ShowUndoReport(sb, backup != null, corrupt, foreign, legacy, publicOpen, result);
            return (int)result;
        }

        private static List<string> FindPublicEnabledRules()
        {
            string err;
            List<FirewallRuleBackup> rules = QueryFirewallRules(out err);
            if (rules == null)
            {
                Log.Warn("[撤销配置] 查询防火墙规则失败，结果里不列出对公共网络开放的规则: " + err);
                return null;
            }
            var names = new List<string>();
            foreach (FirewallRuleBackup r in rules)
                if (r.Enabled && !IsPrivateOnlyProfile(r.Profile)) names.Add(r.Name);
            if (names.Count > 0)
                Log.Warn("[撤销配置] 这些远程桌面防火墙规则仍启用且对公共网络生效（来源不明，未改动）: " +
                         string.Join(", ", names.ToArray()));
            return names;
        }

        private static string UndoFirewall(SetupBackup backup, List<string> d)
        {
            if (!backup.FirewallQueried)
            {
                d.Add(L.T("配置前没能读到防火墙规则的状态，未改动"));
                return null;
            }

            var wanted = new List<string>();
            foreach (FirewallRuleBackup r in backup.FirewallRules)
                if (r != null && !r.Enabled && !string.IsNullOrEmpty(r.Name)) wanted.Add(r.Name);
            if (wanted.Count == 0)
            {
                d.Add(L.T("配置时没有启用过防火墙规则，无需恢复"));
                return null;
            }

            string err;
            List<FirewallRuleBackup> current = QueryFirewallRules(out err);
            if (current == null) return L.T("查询防火墙规则失败") + ": " + err;

            var toDisable = new List<string>();
            foreach (string name in wanted)
            {
                FirewallRuleBackup cur = null;
                foreach (FirewallRuleBackup c in current)
                    if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) { cur = c; break; }

                if (cur == null)
                {
                    d.Add(string.Format(L.T("规则 {0} 已不在远程桌面规则组中，跳过"), name));
                    continue;
                }
                if (!cur.Enabled)
                {
                    d.Add(string.Format(L.T("规则 {0} 已是停用状态"), name));
                    continue;
                }
                toDisable.Add(cur.Name);
            }
            if (toDisable.Count == 0) return null;

            err = SetFirewallRulesEnabled(toDisable, false);
            if (err != null) return err;
            foreach (string n in toDisable) d.Add(string.Format(L.T("已停用规则 {0}"), n));
            Log.Info("[撤销配置] 已停用防火墙规则 " + string.Join(", ", toDisable.ToArray()));
            return null;
        }

        private static bool ShouldUndoAllowEntry(string v, SetupBackup backup, bool legacyPairNow)
        {
            if (v == null || !IsOurSpn(v, backup != null ? backup.MachineName : null)) return false;
            bool isWildcard = string.Equals(v, WildcardSpn, StringComparison.OrdinalIgnoreCase);
            if (backup == null) return !isWildcard || legacyPairNow;
            if (!backup.LegacyParaDeskEntries) return !isWildcard && !ListContains(backup.AllowList, v);
            bool mayBeLegacy = isWildcard || string.Equals(v, LocalhostSpn, StringComparison.OrdinalIgnoreCase);
            return mayBeLegacy || !ListContains(backup.AllowList, v);
        }

        private static string UndoCredentialDelegation(SetupBackup backup, List<string> d)
        {
            bool knowsOriginal = backup != null && !backup.LegacyParaDeskEntries;

            using (var hklm = OpenHklm())
            {
                int removed = 0;
                using (var list = hklm.OpenSubKey(AllowListKey, true))
                {
                    if (list != null)
                    {
                        List<NamedString> before = ReadValues(list);
                        bool legacyPairNow = ListContains(before, LocalhostSpn) && ListContains(before, WildcardSpn);
                        foreach (string name in list.GetValueNames())
                        {
                            string v = list.GetValue(name) as string;
                            if (!ShouldUndoAllowEntry(v, backup, legacyPairNow))
                            {
                                if (string.Equals(v, WildcardSpn, StringComparison.OrdinalIgnoreCase))
                                    Log.Warn("[撤销配置] 保留白名单里的 TERMSRV/*：没有证据表明它是 ParaDesk 写的");
                                continue;
                            }
                            list.DeleteValue(name, false);
                            removed++;
                            d.Add(string.Format(L.T("已删除条目 {0}"), v));
                        }
                    }
                }
                if (removed > 0) Log.Info("[撤销配置] 已从凭据委派白名单删除 " + removed + " 个条目");

                if (knowsOriginal)
                {
                    string e1 = RestoreDword(hklm, CredDelegKey, "AllowDefaultCredentials", "AllowDefaultCredentials",
                        backup.AllowDefaultCredentials, d);
                    string e2 = RestoreDword(hklm, CredDelegKey, "ConcatenateDefaults_AllowDefault",
                        "ConcatenateDefaults_AllowDefault", backup.ConcatenateDefaults, d);
                    DeleteAbsentKeysIfEmpty(hklm, backup, new[] { AllowListKey, CredDelegKey }, d);
                    return e1 ?? e2;
                }

                if (removed > 0 && IsKeyEmptyOrMissing(hklm, AllowListKey))
                {
                    DeleteValueIfPresent(hklm, CredDelegKey, "AllowDefaultCredentials", d);
                    DeleteValueIfPresent(hklm, CredDelegKey, "ConcatenateDefaults_AllowDefault", d);
                    DeleteKeyIfEmpty(hklm, AllowListKey, d);
                    DeleteKeyIfEmpty(hklm, CredDelegKey, d);
                }
                if (removed == 0 && d.Count == 0) d.Add(L.T("没有 ParaDesk 写入的条目"));
            }
            return null;
        }

        private static string UndoPromptForPassword(SetupBackup backup, List<string> d)
        {
            using (var hklm = OpenHklm())
            {
                string e1 = RestoreDword(hklm, TsPolicyKey, "fPromptForPassword",
                    @"Policies\...\Terminal Services\fPromptForPassword", backup.PromptForPasswordPolicy, d);
                string e2 = RestoreDword(hklm, RdpTcpKey, "fPromptForPassword",
                    @"RDP-Tcp\fPromptForPassword", backup.PromptForPasswordRdpTcp, d);
                DeleteAbsentKeysIfEmpty(hklm, backup, new[] { TsPolicyKey, TsPolicyParentKey }, d);
                return e1 ?? e2;
            }
        }

        private static string UndoTermServiceStart(SetupBackup backup, List<string> d)
        {
            DwordBackup s = backup.TermServiceStart;
            if (s == null || !s.Exists || !s.IsDword || s.Value < 2 || s.Value > 4)
            {
                d.Add(L.T("配置前的启动类型未知，未改动"));
                return null;
            }
            DwordBackup bd = backup.TermServiceDelayed;
            bool wantDelayed = s.Value == 2 && bd != null && bd.Exists && bd.IsDword && bd.Value != 0;

            DwordBackup curStart, curDelayed;
            using (var hklm = OpenHklm())
            {
                curStart = ReadDword(hklm, TermServiceKey, "Start");
                curDelayed = ReadDword(hklm, TermServiceKey, "DelayedAutostart");
            }
            bool curIsDelayed = curDelayed.Exists && curDelayed.IsDword && curDelayed.Value != 0;
            if (curStart.Exists && curStart.IsDword && curStart.Value == s.Value &&
                (s.Value != 2 || curIsDelayed == wantDelayed))
            {
                d.Add(string.Format(L.T("与配置前一致（{0}），无需改动"), StartTypeName(s.Value, wantDelayed)));
                return null;
            }

            string mode = wantDelayed ? "delayed-auto" : s.Value == 2 ? "auto" : s.Value == 3 ? "demand" : "disabled";
            string err = RunTool(ScExe(), "config TermService start= " + mode);
            if (err != null) return err;
            d.Add(string.Format(L.T("已恢复为 {0}"), StartTypeName(s.Value, wantDelayed)));
            return null;
        }

        private static string StartTypeName(int start, bool delayed)
        {
            if (start == 2) return delayed ? L.T("自动（延迟启动）") : L.T("自动");
            if (start == 3) return L.T("手动启动");
            return L.T("禁止启动");
        }

        private static void RetireBackup(string path, bool corrupt, bool foreign)
        {
            try
            {
                if (!File.Exists(path)) return;
                if (corrupt || foreign)
                {
                    string moved = path + (foreign ? ForeignBackupSuffix : ".bad");
                    if (File.Exists(moved)) File.Delete(moved);
                    File.Move(path, moved);
                    Log.Info("[撤销配置] " + (foreign ? "来自另一台电脑的" : "无法读取的") + "备份已改名为 " + moved);
                }
                else
                {
                    File.Delete(path);
                    Log.Info("[撤销配置] 已删除配置备份 " + path);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[撤销配置] 处理备份文件失败: " + ex.Message);
            }
        }

        private static void ShowUndoReport(StringBuilder steps, bool hadBackup, bool corrupt, bool foreign, bool legacy,
            List<string> publicOpen, SetupResult result)
        {
            bool ok = result == SetupResult.Success || result == SetupResult.RebootRequired;
            var body = new StringBuilder();
            body.AppendLine(ok
                ? L.T("已撤销 ParaDesk 对系统所做的配置。")
                : hadBackup
                    ? L.T("有步骤没有完成，配置备份已保留，可以稍后重试。")
                    : L.T("有步骤没有完成，可以稍后重试。"));
            if (result == SetupResult.RebootRequired)
                body.AppendLine(L.T("重启电脑后子会话功能才会完全关闭。"));
            body.AppendLine();

            if (!hadBackup || legacy)
            {
                if (legacy)
                    body.AppendLine(L.T("配置前的备份是在旧版本配置之后才建立的：子会话开关与凭据委派条目按 ParaDesk 写入的处理。"));
                else if (foreign)
                    body.AppendLine(L.T("配置备份来自另一台电脑，按没有备份处理。"));
                else if (corrupt)
                    body.AppendLine(L.T("配置备份文件无法读取，按没有备份处理。"));
                else
                    body.AppendLine(L.T("没有找到配置前的备份（可能由旧版本配置，或备份文件已被删除），只撤销能确定是 ParaDesk 做的改动。"));
                body.AppendLine(L.T("远程桌面监听器、防火墙远程桌面规则、「始终提示输入密码」、TermService 启动类型的原值无法确定，未改动。如果不需要远程桌面，可以在 Windows「设置 → 系统 → 远程桌面」里关闭。"));
                if (publicOpen != null && publicOpen.Count > 0)
                    body.AppendLine(string.Format(L.T("这些远程桌面防火墙规则仍启用，且对公共网络也生效：{0}"),
                        string.Join(", ", publicOpen.ToArray())));
                body.AppendLine();
            }

            body.Append(steps);
            body.AppendLine();

            int? interval = PerformanceSettings.GetFrameInterval();
            if (interval.HasValue && interval.Value > 0)
                body.AppendLine(L.T("帧率上限是在「画面」页单独设置的，未改动；需要时在那里改回 30 FPS。"));
            body.AppendLine(L.T("分身桌面守护程序的启动项与跨设备开关属于当前用户的设置，由 ParaDesk 主程序在撤销成功后清理。"));
            body.AppendLine();
            body.Append(L.T("日志：") + Log.Path0);

            MessageBox.Show(body.ToString(),
                AppInfo.ProductName + " — " + (ok ? L.T("撤销配置完成") : L.T("撤销配置未完成")),
                MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private static RegistryKey OpenHklm()
        {
            return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);
        }

        private static DwordBackup ReadDword(RegistryKey hklm, string subKey, string name)
        {
            var b = new DwordBackup();
            using (var k = hklm.OpenSubKey(subKey))
            {
                if (k == null) return b;
                object v = k.GetValue(name);
                if (v == null) return b;
                b.Exists = true;
                if (k.GetValueKind(name) == RegistryValueKind.DWord && v is int)
                {
                    b.IsDword = true;
                    b.Value = (int)v;
                }
            }
            return b;
        }

        private static string DescribeDword(DwordBackup b)
        {
            if (b == null || !b.Exists) return "(无)";
            return b.IsDword ? b.Value.ToString(CultureInfo.InvariantCulture) : "(非 DWORD)";
        }

        private static string RestoreDword(RegistryKey hklm, string subKey, string name, string label,
            DwordBackup b, List<string> d)
        {
            if (b == null)
            {
                d.Add(string.Format(L.T("{0}：备份里没有这一项，未改动"), label));
                return null;
            }
            if (b.Exists && !b.IsDword)
            {
                d.Add(string.Format(L.T("{0}：原值不是 DWORD，无法原样恢复，保持现状"), label));
                return null;
            }

            using (var k = hklm.OpenSubKey(subKey, true))
            {
                object cur = k == null ? null : k.GetValue(name);
                if (!b.Exists)
                {
                    if (cur == null)
                    {
                        d.Add(string.Format(L.T("{0}：与配置前一致，无需改动"), label));
                        return null;
                    }
                    k.DeleteValue(name, false);
                    d.Add(string.Format(L.T("{0}：已删除（配置前不存在）"), label));
                    return null;
                }

                if (cur is int && (int)cur == b.Value && k.GetValueKind(name) == RegistryValueKind.DWord)
                {
                    d.Add(string.Format(L.T("{0}：与配置前一致，无需改动"), label));
                    return null;
                }
            }

            using (var w = hklm.CreateSubKey(subKey))
            {
                if (w == null) return L.T("无法打开注册表键");
                w.SetValue(name, b.Value, RegistryValueKind.DWord);
            }
            d.Add(string.Format(L.T("{0}：已恢复为 {1}"), label, b.Value.ToString(CultureInfo.InvariantCulture)));
            return null;
        }

        private static void DeleteValueIfPresent(RegistryKey hklm, string subKey, string name, List<string> d)
        {
            using (var k = hklm.OpenSubKey(subKey, true))
            {
                if (k == null || k.GetValue(name) == null) return;
                k.DeleteValue(name, false);
            }
            d.Add(string.Format(L.T("{0}：已删除"), name));
        }

        private static bool IsKeyEmptyOrMissing(RegistryKey hklm, string subKey)
        {
            using (var k = hklm.OpenSubKey(subKey))
            {
                return k == null || (k.ValueCount == 0 && k.SubKeyCount == 0);
            }
        }

        private static void DeleteKeyIfEmpty(RegistryKey hklm, string subKey, List<string> d)
        {
            using (var k = hklm.OpenSubKey(subKey))
            {
                if (k == null || k.ValueCount != 0 || k.SubKeyCount != 0) return;
            }
            hklm.DeleteSubKey(subKey, false);
            d.Add(string.Format(L.T("已删除空的策略键 {0}"), subKey));
            Log.Info("[撤销配置] 已删除空键 HKLM\\" + subKey);
        }

        private static void DeleteAbsentKeysIfEmpty(RegistryKey hklm, SetupBackup backup, string[] candidates, List<string> d)
        {
            foreach (string key in candidates)
            {
                bool absentBefore = false;
                foreach (string k in backup.KeysAbsent)
                    if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) { absentBefore = true; break; }
                if (absentBefore) DeleteKeyIfEmpty(hklm, key, d);
            }
        }

        private static List<NamedString> ReadAllowList(RegistryKey hklm)
        {
            using (var list = hklm.OpenSubKey(AllowListKey))
            {
                return list == null ? new List<NamedString>() : ReadValues(list);
            }
        }

        private static List<NamedString> ReadValues(RegistryKey key)
        {
            var result = new List<NamedString>();
            foreach (string name in key.GetValueNames())
            {
                string v = key.GetValue(name) as string;
                if (!string.IsNullOrEmpty(v)) result.Add(new NamedString { Name = name, Value = v });
            }
            return result;
        }

        private static bool ListContains(List<NamedString> list, string value)
        {
            if (list == null) return false;
            foreach (NamedString e in list)
                if (e != null && string.Equals(e.Value, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string[] OurSpns(string extraMachineName)
        {
            if (string.IsNullOrEmpty(extraMachineName) ||
                string.Equals(extraMachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                return new[] { LocalhostSpn, LoopbackIpSpn, "TERMSRV/" + Environment.MachineName };
            return new[] { LocalhostSpn, LoopbackIpSpn, "TERMSRV/" + Environment.MachineName, "TERMSRV/" + extraMachineName };
        }

        private static bool IsOurSpn(string value, string extraMachineName)
        {
            if (string.Equals(value, WildcardSpn, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string s in OurSpns(extraMachineName))
                if (string.Equals(value, s, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static List<FirewallRuleBackup> QueryFirewallRules(out string error)
        {
            error = null;
            const string script =
                "try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }; " +
                "try { Get-NetFirewallRule -Group '" + RdpFirewallGroup + "' -ErrorAction Stop | ForEach-Object { " +
                "'RULE' + [char]9 + $_.Name + [char]9 + [int]$_.Profile + [char]9 + $_.Enabled } } " +
                "catch { if ($_.FullyQualifiedErrorId -like 'CmdletizationQuery_NotFound*') { exit 0 }; " +
                "'ERR' + [char]9 + $_.Exception.Message; exit 1 }";

            ProcResult r;
            try { r = RunPowerShell(script, 60000); }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Error("[防火墙] 无法启动 PowerShell", ex);
                return null;
            }
            if (r.TimedOut) { error = L.T("执行超时"); return null; }
            if (r.ExitCode != 0)
            {
                error = DescribeExit(r);
                Log.Warn("[防火墙] 查询规则失败: " + error);
                return null;
            }

            var list = new List<FirewallRuleBackup>();
            foreach (string raw in r.Out.Split('\n'))
            {
                string line = raw.TrimEnd('\r').TrimStart('﻿');
                if (!line.StartsWith("RULE\t", StringComparison.Ordinal)) continue;
                string[] parts = line.Split('\t');
                if (parts.Length < 4 || parts[1].Length == 0) continue;
                int profile;
                if (!int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out profile)) continue;
                list.Add(new FirewallRuleBackup
                {
                    Name = parts[1],
                    Profile = profile,
                    Enabled = string.Equals(parts[3].Trim(), "True", StringComparison.OrdinalIgnoreCase),
                });
            }
            Log.Info("[防火墙] 远程桌面组规则: " + DescribeRules(list));
            return list;
        }

        private static string DescribeRules(List<FirewallRuleBackup> rules)
        {
            if (rules.Count == 0) return "(无)";
            var sb = new StringBuilder();
            foreach (FirewallRuleBackup r in rules)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(r.Name).Append(" Profile=").Append(r.Profile).Append(r.Enabled ? " 已启用" : " 未启用");
            }
            return sb.ToString();
        }

        private static string SetFirewallRulesEnabled(List<string> names, bool enable)
        {
            var quoted = new StringBuilder();
            foreach (string n in names)
            {
                if (!IsSafeRuleName(n))
                {
                    Log.Warn("[防火墙] 规则名含不安全字符，跳过: " + n);
                    return string.Format(L.T("规则名无效：{0}"), n);
                }
                if (quoted.Length > 0) quoted.Append(',');
                quoted.Append('\'').Append(n).Append('\'');
            }

            string script =
                "try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }; " +
                "try { " + (enable ? "Enable" : "Disable") + "-NetFirewallRule -Name " + quoted + " -ErrorAction Stop } " +
                "catch { 'ERR' + [char]9 + $_.Exception.Message; exit 1 }";

            ProcResult r = RunPowerShell(script, 60000);
            if (r.TimedOut) return L.T("执行超时");
            if (r.ExitCode != 0)
            {
                string err = DescribeExit(r);
                Log.Warn("[防火墙] " + (enable ? "启用" : "停用") + "规则失败: " + err);
                return err;
            }
            return null;
        }

        private static bool IsSafeRuleName(string n)
        {
            if (string.IsNullOrEmpty(n) || n.Length > 256) return false;
            foreach (char c in n)
            {
                if (c < 128 && char.IsLetterOrDigit(c)) continue;
                if ("-_. {}()".IndexOf(c) >= 0) continue;
                return false;
            }
            return true;
        }

        private sealed class ProcResult
        {
            public bool TimedOut;
            public int ExitCode;
            public string Out = "";
            public string Err = "";
        }

        private static ProcResult RunProcess(string exe, string args, int timeoutMs, Encoding encoding)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (encoding != null)
            {
                psi.StandardOutputEncoding = encoding;
                psi.StandardErrorEncoding = encoding;
            }

            var outSb = new StringBuilder();
            var errSb = new StringBuilder();
            var result = new ProcResult();
            using (var p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (outSb) outSb.AppendLine(e.Data);
                };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (errSb) errSb.AppendLine(e.Data);
                };

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                if (p.WaitForExit(timeoutMs))
                {
                    p.WaitForExit();
                    result.ExitCode = p.ExitCode;
                }
                else
                {
                    result.TimedOut = true;
                    try
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception ex)
                    {
                        Log.Warn("结束超时的子进程失败: " + Path.GetFileName(exe) + " :: " + ex.Message);
                    }
                    Log.Error("子进程超时（" + (timeoutMs / 1000) + " 秒）已结束: " + Path.GetFileName(exe) + " " + args);
                }
            }
            lock (outSb) result.Out = outSb.ToString();
            lock (errSb) result.Err = errSb.ToString();
            return result;
        }

        private static ProcResult RunPowerShell(string script, int timeoutMs)
        {
            return RunProcess(PowerShellExe(),
                "-NoProfile -NonInteractive -Command \"" + script + "\"", timeoutMs, new UTF8Encoding(false));
        }

        private static string RunTool(string exe, string args)
        {
            ProcResult r = RunProcess(exe, args, 30000, null);
            if (r.TimedOut) return L.T("执行超时");
            if (r.ExitCode == 0) return null;
            string err = DescribeExit(r);
            Log.Warn(Path.GetFileName(exe) + " " + args + " 失败: " + err);
            return err;
        }

        private static string DescribeExit(ProcResult r)
        {
            string text = FirstLine(r.Err);
            if (text.Length == 0) text = LastLine(r.Out);
            if (text.StartsWith("ERR\t", StringComparison.Ordinal)) text = text.Substring(4).Trim();
            string head = string.Format(L.T("退出码 {0}"), r.ExitCode);
            return text.Length == 0 ? head : head + ": " + text;
        }

        private static string FirstLine(string s)
        {
            foreach (string line in (s ?? "").Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0) return t.Length > 300 ? t.Substring(0, 300) + "…" : t;
            }
            return "";
        }

        private static string LastLine(string s)
        {
            string[] lines = (s ?? "").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string t = lines[i].Trim();
                if (t.Length > 0) return t.Length > 300 ? t.Substring(0, 300) + "…" : t;
            }
            return "";
        }

        private static string ScExe()
        {
            string p = Path.Combine(Environment.SystemDirectory, "sc.exe");
            return File.Exists(p) ? p : "sc.exe";
        }

        private static string PowerShellExe()
        {
            string p = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            return File.Exists(p) ? p : "powershell.exe";
        }

        /// <summary>
        /// 启用 Windows 沙盒可选功能。
        ///
        /// 用 DISM 而不是 PowerShell 的 Enable-WindowsOptionalFeature：
        /// 后者在部分精简系统上缺 DISM 模块，而 dism.exe 是系统自带、一定在。
        /// /norestart 是必须的——这里不能替用户重启机器，只把"要重启"如实告诉他。
        /// </summary>
        private static int EnableSandboxFeature()
        {
            try
            {
                ProcResult r = RunProcess(Path.Combine(Environment.SystemDirectory, "dism.exe"),
                    "/online /enable-feature /featurename:Containers-DisposableClientVM /all /norestart",
                    600000, null);

                if (r.TimedOut)
                {
                    Log.Error("[沙盒] DISM 超时", null);
                    return (int)SetupResult.Failed;
                }

                Log.Info("[沙盒] DISM 退出码 " + r.ExitCode +
                         (r.Err.Length > 0 ? " stderr=" + r.Err.Trim() : ""));

                // 0 = 成功；3010 = 成功但需要重启，对我们来说是同一件事
                if (r.ExitCode == 0 || r.ExitCode == 3010)
                    return (int)SetupResult.RebootRequired;

                Log.Warn("[沙盒] 启用失败: " + r.Out.Trim());
                return (int)SetupResult.Failed;
            }
            catch (Exception ex)
            {
                Log.Error("[沙盒] 启用异常", ex);
                return (int)SetupResult.Failed;
            }
        }

        private static void Show(StringBuilder sb, string title, MessageBoxIcon icon)
        {
            MessageBox.Show(sb + "\r\n" + L.T("日志：") + Log.Path0,
                AppInfo.ProductName + " — " + title, MessageBoxButtons.OK, icon);
        }
    }

    [DataContract]
    internal sealed class SetupBackup
    {
        [DataMember] public int Version;
        [DataMember] public string Note;
        [DataMember] public string CreatedAt;
        [DataMember] public string MachineName;
        [DataMember] public string CreatedBy;

        [DataMember] public bool ChildSessionsKnown;
        [DataMember] public bool ChildSessionsEnabled;

        [DataMember] public DwordBackup DenyTsConnections;

        [DataMember] public bool FirewallQueried;
        [DataMember] public List<FirewallRuleBackup> FirewallRules;

        [DataMember] public DwordBackup AllowDefaultCredentials;
        [DataMember] public DwordBackup ConcatenateDefaults;
        [DataMember] public List<NamedString> AllowList;
        [DataMember] public bool LegacyParaDeskEntries;

        [DataMember] public DwordBackup PromptForPasswordPolicy;
        [DataMember] public DwordBackup PromptForPasswordRdpTcp;

        [DataMember] public DwordBackup TermServiceStart;
        [DataMember] public DwordBackup TermServiceDelayed;

        [DataMember] public List<string> KeysAbsent;

        [DataMember] public string MachineIdHash;
    }

    [DataContract]
    internal sealed class DwordBackup
    {
        [DataMember] public bool Exists;
        [DataMember] public bool IsDword;
        [DataMember] public int Value;
    }

    [DataContract]
    internal sealed class FirewallRuleBackup
    {
        [DataMember] public string Name;
        [DataMember] public bool Enabled;
        [DataMember] public int Profile;
    }

    [DataContract]
    internal sealed class NamedString
    {
        [DataMember] public string Name;
        [DataMember] public string Value;
    }
}
