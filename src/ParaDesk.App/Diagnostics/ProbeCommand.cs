using System;
using System.IO;
using System.Text;
using ParaDesk.Core;

namespace ParaDesk.Diagnostics
{
    /// <summary>只读环境自检（--probe）。不改动任何系统状态，供排障与自动化验证使用。</summary>
    internal static class ProbeCommand
    {
        public static int Run()
        {
            var r = SystemStatus.Check();
            var sb = new StringBuilder();

            sb.AppendLine("=== " + AppInfo.Title + " v" + AppInfo.Version + " 环境自检 ===");
            sb.AppendLine("EditionID          = " + r.EditionId + " (build " + r.BuildNumber + ")");
            sb.AppendLine("IsHomeEdition      = " + r.IsHomeEdition);
            sb.AppendLine("ChildSessionsOn    = " + r.ChildSessionsEnabled);
            sb.AppendLine("TermServiceRunning = " + r.TermServiceRunning);
            sb.AppendLine("RdpListenerEnabled = " + r.RdpListenerEnabled);
            sb.AppendLine("RdpControlOk       = " + r.RdpControlRegistered);
            sb.AppendLine("InsideChildSession = " + r.InsideChildSession);
            sb.AppendLine("ChildSessionId     = " + (r.ChildSessionId == 0xFFFFFFFF ? "(无)" : r.ChildSessionId.ToString()));
            sb.AppendLine("Transport          = 0x" + r.TransportStatus.ToString("X8") +
                          " (" + SystemStatus.DescribeTransportError(r.TransportStatus) + ")");
            if (!string.IsNullOrEmpty(r.TransportPipe))
                sb.AppendLine("TransportPipe      = " + r.TransportPipe);
            sb.AppendLine("ReadyToStart       = " + r.ReadyToStart);
            if (r.NextAction != null)
                sb.AppendLine("NextAction         = " + r.NextAction);

            foreach (var m in MonitorService.Enumerate())
            {
                sb.AppendLine("Monitor            = " + m.DeviceName +
                              " bounds=" + m.Bounds + (m.IsPrimary ? " PRIMARY" : ""));
            }

            string text = sb.ToString();
            Console.Write(text);
            Log.Info(text.TrimEnd());
            try { File.WriteAllText(Path.Combine(Log.Dir, "probe.log"), text, Encoding.UTF8); }
            catch { }

            return r.ReadyToStart ? 0 : 2;
        }
    }
}
