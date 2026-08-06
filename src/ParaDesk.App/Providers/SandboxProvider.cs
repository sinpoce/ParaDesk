using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;
using ParaDesk.Core;

namespace ParaDesk.Providers
{
    /// <summary>
    /// Windows Sandbox 桌面后端。
    ///
    /// 为什么需要它：子会话全系统同时只允许一个（这是 Windows 的硬限制，
    /// 微软自家 Power Automate 与 UiPath 也一样）。想要"第二个、第三个桌面"，
    /// 只能换后端。Sandbox 是代价最小的一种：系统自带、免额外授权、秒级启动。
    ///
    /// 代价也要讲清楚：它是即抛环境，关闭即全部丢失；与主桌面**不共享文件**
    /// （只能靠映射文件夹），因此不适合"AI 接着我的工作继续干"这类场景——
    /// 那仍应该用子会话。它适合的是一次性的隔离任务。
    /// </summary>
    internal static class SandboxProvider
    {
        /// <summary>
        /// 沙盒功能当前没装，但这台机器装得上（即：不是家庭版）。
        /// 家庭版压根没有这个可选组件，DISM 也装不了，只能如实告诉用户。
        /// </summary>
        public static bool CanEnable()
        {
            if (SystemStatus.IsHomeEdition()) return false;
            return !File.Exists(Path.Combine(Environment.SystemDirectory, "WindowsSandbox.exe"));
        }

        /// <summary>本机是否具备运行 Windows Sandbox 的条件。</summary>
        public static bool IsAvailable(out string reason)
        {
            reason = null;

            if (SystemStatus.IsHomeEdition())
            {
                reason = L.T("Windows 家庭版不含沙盒功能，需专业版及以上。");
                return false;
            }

            string exe = Path.Combine(Environment.SystemDirectory, "WindowsSandbox.exe");
            if (!File.Exists(exe))
            {
                reason = L.T("系统未启用「Windows 沙盒」功能，可在「启用或关闭 Windows 功能」中开启（需重启）。");
                return false;
            }

            // 刻意不再做"虚拟化是否开启"的预判：
            // 之前那段检查在所有路径上都返回 true，是死代码，反而让人以为已经校验过。
            // 可靠的判据只有 WindowsSandbox.exe 是否存在——启用沙盒功能本身就要求
            // 虚拟化可用，Windows 在启用阶段就会拦截。真正的失败留给启动时的报错。
            return true;
        }

        /// <summary>
        /// 生成 .wsb 配置并启动沙盒。
        /// mappedFolder 会以只读或可写方式映射进沙盒，是与主机交换文件的唯一通道。
        /// </summary>
        public static string Launch(string mappedFolder, bool writable, int memoryMb, out string configPath)
        {
            configPath = null;
            string reason;
            if (!IsAvailable(out reason)) return reason;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("<Configuration>");
                sb.AppendLine("  <VGpu>Enable</VGpu>");
                sb.AppendLine("  <Networking>Enable</Networking>");
                sb.AppendLine("  <ClipboardRedirection>Enable</ClipboardRedirection>");
                if (memoryMb >= 2048)
                    sb.AppendLine("  <MemoryInMB>" + memoryMb + "</MemoryInMB>");

                if (!string.IsNullOrEmpty(mappedFolder) && Directory.Exists(mappedFolder))
                {
                    sb.AppendLine("  <MappedFolders>");
                    sb.AppendLine("    <MappedFolder>");
                    sb.AppendLine("      <HostFolder>" + Escape(mappedFolder) + "</HostFolder>");
                    sb.AppendLine("      <ReadOnly>" + (writable ? "false" : "true") + "</ReadOnly>");
                    sb.AppendLine("    </MappedFolder>");
                    sb.AppendLine("  </MappedFolders>");
                }
                sb.AppendLine("</Configuration>");

                string dir = Path.Combine(AppInfo.DataDir, "Sandbox");
                Directory.CreateDirectory(dir);
                configPath = Path.Combine(dir, "paradesk.wsb");
                File.WriteAllText(configPath, sb.ToString(), new UTF8Encoding(false));

                // 直接调 WindowsSandbox.exe 并传配置，比靠 .wsb 的文件关联更可靠
                // （关联可能被其他程序抢占，或在精简版系统上缺失）
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "WindowsSandbox.exe"),
                    Arguments = "\"" + configPath + "\"",
                    UseShellExecute = true,
                };
                Process.Start(psi);

                Log.Info("已启动 Windows 沙盒，配置: " + configPath);
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("启动 Windows 沙盒失败", ex);
                return L.T("启动沙盒失败：") + ex.Message;
            }
        }

        private static string Escape(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        public static bool IsRunning()
        {
            try
            {
                // 24H2 起客户端进程改名，两个名字都要查
                return Process.GetProcessesByName("WindowsSandboxClient").Length > 0
                    || Process.GetProcessesByName("WindowsSandboxRemoteSession").Length > 0;
            }
            catch { return false; }
        }
    }
}
