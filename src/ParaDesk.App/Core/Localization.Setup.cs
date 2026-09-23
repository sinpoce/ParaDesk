using System;

namespace ParaDesk.Core
{
    internal static partial class L
    {
        static partial void AddSetup(Action<string, string> Add)
        {
            Add("备份配置前的系统状态（供撤销使用）", "Back up the pre-setup system state (for undo)");
            Add("开放防火墙远程桌面规则（仅专用/域网络）",
                "Open the Remote Desktop firewall rules (private/domain networks only)");
            Add("移除旧版写入的 TERMSRV/*（它会把默认凭据放行给任何远程桌面服务器）",
                "Remove TERMSRV/* written by older versions (it hands your default credentials to any Remote Desktop server)");
            Add("关闭「始终提示输入密码」", "Turn off “Always prompt for password”");
            Add("子会话通道自检", "Child session channel self-test");

            Add("没有对系统做任何改动。", "Nothing was changed on the system.");

            Add("配置已完成，但没能记录配置前的系统状态。之后撤销配置时，只能撤销能确定是 ParaDesk 做的改动。",
                "Setup is complete, but the system state from before setup couldn't be recorded. A later undo can only revert the changes known to come from ParaDesk.");

            Add("已有备份，保留第一次配置之前的状态",
                "a backup already exists; keeping the state from before the first setup");
            Add("已有备份文件但无法读取，保留原文件不覆盖",
                "a backup file exists but can't be read; it was kept, not overwritten");
            Add("备份文件：{0}", "Backup file: {0}");
            Add("已有的备份来自另一台电脑，已改名为 {0}，重新记录本机的状态",
                "the existing backup came from another PC; it was renamed to {0} and this PC's state was recorded");
            Add("本机已有 ParaDesk 的配置，但找不到配置前的备份：此刻已是配置后的状态，不作为原状态记录；撤销时只撤销能确定是 ParaDesk 做的改动",
                "ParaDesk has already configured this PC, but there's no pre-setup backup: the current state is the configured one, so it wasn't recorded as the original; undo will only revert changes known to come from ParaDesk");
            Add("规则 {0} 对公共网络也生效，且已是启用状态（可能是旧版本或你自己启用的），未改动",
                "Rule {0} also applies to public networks and is already enabled (by an older version or by you); left unchanged");
            Add("没有需要启用的规则。上面已启用的规则让 3389 端口在公共网络上也开放；分身桌面用不到它们，不需要从别的电脑远程连这台机器的话，可以在「Windows Defender 防火墙 → 高级设置」里停用",
                "No rule needed enabling. The enabled rules above also open port 3389 on public networks. The parallel desktop doesn't use them; if you don't connect to this PC from other computers, you can disable them in Windows Defender Firewall → Advanced settings");
            Add("子会话走本机回环，不依赖这些规则；这一步失败不影响分身桌面",
                "The child session runs over the local loopback and doesn't depend on these rules; this step failing doesn't affect the parallel desktop");
            Add("白名单里另有 TERMSRV/*：它会把默认凭据放行给任何远程桌面服务器。分身桌面不需要它，如果不是你有意添加的，可以在组策略「允许委派默认凭据」里删掉",
                "The allow list also contains TERMSRV/*, which hands your default credentials to any Remote Desktop server. The parallel desktop doesn't need it; if you didn't add it on purpose, remove it in the Group Policy setting “Allow delegating default credentials”");
            Add("规则 {0} 对公共网络也生效，跳过", "Rule {0} also applies to public networks; skipped");
            Add("规则 {0} 已是启用状态", "Rule {0} is already enabled");
            Add("已启用规则 {0}", "Enabled rule {0}");
            Add("没有需要启用的规则：子会话走本机回环，不需要对外开放 3389 端口",
                "No rule needed enabling: the child session runs over the local loopback, so port 3389 doesn't need to be open to other machines");
            Add("{0} 已在白名单中", "{0} is already in the allow list");
            Add("已添加 {0}", "Added {0}");
            Add("已删除条目 {0}", "Removed entry {0}");

            Add("删除失败，详见日志", "couldn't delete it — see the log");
            Add("无法创建策略键", "couldn't create the policy key");
            Add("无法创建白名单键", "couldn't create the allow-list key");
            Add("无法打开注册表键", "couldn't open the registry key");
            Add("执行超时", "timed out");
            Add("退出码 {0}", "exit code {0}");
            Add("服务无法停止（需重启电脑）", "the service can't be stopped (restart the PC)");
            Add("查询防火墙规则失败", "couldn't query the firewall rules");
            Add("规则名无效：{0}", "invalid rule name: {0}");
            Add("已有的备份来自另一台电脑，且无法改名；本次没有记录，撤销时只能尽力而为",
                "the existing backup came from another PC and couldn't be renamed; nothing was recorded this time, so undo will be best-effort");

            Add("恢复远程桌面监听器设置", "Restore the Remote Desktop listener setting");
            Add("恢复防火墙远程桌面规则", "Restore the Remote Desktop firewall rules");
            Add("撤销凭据委派设置", "Undo the credential delegation settings");
            Add("恢复「始终提示输入密码」设置", "Restore the “Always prompt for password” setting");
            Add("恢复 TermService 启动类型", "Restore the TermService startup type");

            Add("配置前就已开启，保持不变", "It was already on before setup; left unchanged");
            Add("已是关闭状态", "Already off");
            Add("配置前没能读到防火墙规则的状态，未改动",
                "The firewall rule states couldn't be read before setup; left unchanged");
            Add("配置时没有启用过防火墙规则，无需恢复", "Setup didn't enable any firewall rule; nothing to restore");
            Add("规则 {0} 已不在远程桌面规则组中，跳过", "Rule {0} is no longer in the Remote Desktop rule group; skipped");
            Add("规则 {0} 已是停用状态", "Rule {0} is already disabled");
            Add("已停用规则 {0}", "Disabled rule {0}");
            Add("没有 ParaDesk 写入的条目", "No entries written by ParaDesk");
            Add("已删除空的策略键 {0}", "Removed the empty policy key {0}");
            Add("{0}：备份里没有这一项，未改动", "{0}: not in the backup; left unchanged");
            Add("{0}：原值不是 DWORD，无法原样恢复，保持现状",
                "{0}: the original value wasn't a DWORD and can't be restored as-is; left unchanged");
            Add("{0}：与配置前一致，无需改动", "{0}: same as before setup; no change needed");
            Add("{0}：已删除（配置前不存在）", "{0}: removed (it didn't exist before setup)");
            Add("{0}：已删除", "{0}: removed");
            Add("{0}：已恢复为 {1}", "{0}: restored to {1}");
            Add("配置前的启动类型未知，未改动", "The startup type before setup is unknown; left unchanged");
            Add("与配置前一致（{0}），无需改动", "Same as before setup ({0}); no change needed");
            Add("已恢复为 {0}", "Restored to {0}");
            Add("自动（延迟启动）", "Automatic (delayed start)");
            Add("手动启动", "Manual");
            Add("禁止启动", "Disabled");

            Add("撤销配置完成", "Undo complete");
            Add("撤销配置未完成", "Undo incomplete");
            Add("有步骤没有完成，配置备份已保留，可以稍后重试。",
                "Some steps didn't finish. The setup backup has been kept, so you can try again later.");
            Add("有步骤没有完成，可以稍后重试。", "Some steps didn't finish. You can try again later.");
            Add("重启电脑后子会话功能才会完全关闭。", "Child sessions will be fully turned off after you restart the PC.");
            Add("配置备份文件无法读取，按没有备份处理。", "The setup backup file couldn't be read, so it was treated as missing.");
            Add("配置备份来自另一台电脑，按没有备份处理。", "The setup backup came from another PC, so it was treated as missing.");
            Add("没有找到配置前的备份（可能由旧版本配置，或备份文件已被删除），只撤销能确定是 ParaDesk 做的改动。",
                "No pre-setup backup was found (an older version may have done the setup, or the backup file was deleted), so only changes known to come from ParaDesk were undone.");
            Add("远程桌面监听器、防火墙远程桌面规则、「始终提示输入密码」、TermService 启动类型的原值无法确定，未改动。如果不需要远程桌面，可以在 Windows「设置 → 系统 → 远程桌面」里关闭。",
                "The original values of the Remote Desktop listener, the Remote Desktop firewall rules, “Always prompt for password” and the TermService startup type are unknown, so they were left as they are. If you don't need Remote Desktop, turn it off in Windows Settings → System → Remote Desktop.");
            Add("配置前的备份是在旧版本配置之后才建立的：子会话开关与凭据委派条目按 ParaDesk 写入的处理。",
                "The pre-setup backup was made after an older version had already configured the system, so the child session switch and the credential delegation entries were treated as ParaDesk's.");
            Add("这些远程桌面防火墙规则仍启用，且对公共网络也生效：{0}",
                "These Remote Desktop firewall rules are still enabled and also apply to public networks: {0}");
            Add("帧率上限是在「画面」页单独设置的，未改动；需要时在那里改回 30 FPS。",
                "The frame-rate cap is set separately on the Display page and was left unchanged; set it back to 30 FPS there if needed.");
            Add("分身桌面守护程序的启动项与跨设备开关属于当前用户的设置，由 ParaDesk 主程序在撤销成功后清理。",
                "The parallel desktop helper's startup entry and the cross-device switches are per-user settings; the ParaDesk app cleans them up after a successful undo.");
        }
    }
}
