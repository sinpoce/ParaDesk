using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using Microsoft.Win32;
using ParaDesk.Core;

namespace ParaDesk.Diagnostics
{
    internal static class ProbeCommand
    {
        public static int Run()
        {
            return Run(new string[0]);
        }

        public static int Run(string[] args)
        {
            bool json = false;
            if (args != null)
            {
                for (int i = 1; i < args.Length; i++)
                {
                    string a = args[i] ?? "";
                    if (a.Length == 0) continue;
                    if (string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase)) { json = true; continue; }
                    if (Cli.IsHelpVerb(a)) return Cli.Help();
                    return Cli.UnknownArgument(a);
                }
            }

            var r = SystemStatus.Check();
            bool wgc = WgcSupported();
            List<MonitorInfo> monitors = MonitorService.Enumerate();

            string text = BuildText(r, wgc, monitors);
            Log.Info(text.TrimEnd());
            try { File.WriteAllText(Path.Combine(Log.Dir, "probe.log"), text, Encoding.UTF8); }
            catch (Exception ex) { Log.Debug("写入 probe.log 失败: " + ex.Message); }

            if (json) CliOutput.WriteLine(BuildJson(r, wgc, monitors));
            else CliOutput.Write(text);

            return r.ReadyToStart ? ExitCodes.Ok : ExitCodes.NotReady;
        }

        public static string BuildText(EnvironmentReport r, bool wgcSupported, List<MonitorInfo> monitors)
        {
            var sb = new StringBuilder();

            sb.AppendLine(string.Format(L.T("=== {0} v{1} 环境自检 ==="), AppInfo.DisplayTitle, AppInfo.Version));
            sb.AppendLine("OsBuild            = " + OsBuild());
            sb.AppendLine("EditionID          = " + r.EditionId + " (build " + r.BuildNumber + ")");
            sb.AppendLine("IsHomeEdition      = " + r.IsHomeEdition);
            sb.AppendLine("ChildSessionsOn    = " + r.ChildSessionsEnabled);
            sb.AppendLine("TermServiceRunning = " + r.TermServiceRunning);
            sb.AppendLine("RdpListenerEnabled = " + r.RdpListenerEnabled);
            sb.AppendLine("RdpControlOk       = " + r.RdpControlRegistered);
            sb.AppendLine("InsideChildSession = " + r.InsideChildSession);
            sb.AppendLine("ChildSessionId     = " + (r.ChildSessionId == 0xFFFFFFFF ? L.T("(无)") : r.ChildSessionId.ToString()));
            sb.AppendLine("Transport          = 0x" + r.TransportStatus.ToString("X8") +
                          " (" + SystemStatus.DescribeTransportError(r.TransportStatus) + ")");
            if (!string.IsNullOrEmpty(r.TransportPipe))
                sb.AppendLine("TransportPipe      = " + r.TransportPipe);
            sb.AppendLine("WgcSupported       = " + wgcSupported);
            sb.AppendLine("ReadyToStart       = " + r.ReadyToStart);
            if (r.NextAction != null)
                sb.AppendLine("NextAction         = " + r.NextAction);

            if (monitors != null)
            {
                foreach (var m in monitors)
                {
                    sb.AppendLine("Monitor            = " + m.DeviceName +
                                  " bounds=" + m.Bounds + (m.IsPrimary ? " PRIMARY" : ""));
                }
            }
            return sb.ToString();
        }

        public static string BuildJson(EnvironmentReport r, bool wgcSupported, List<MonitorInfo> monitors)
        {
            var dto = new ProbeReport
            {
                AppVersion = AppInfo.Version,
                OsBuild = OsBuild(),
                EditionId = r.EditionId,
                BuildNumber = r.BuildNumber,
                IsHomeEdition = r.IsHomeEdition,
                ChildSessionsEnabled = r.ChildSessionsEnabled,
                TermServiceRunning = r.TermServiceRunning,
                RdpListenerEnabled = r.RdpListenerEnabled,
                RdpControlRegistered = r.RdpControlRegistered,
                InsideChildSession = r.InsideChildSession,
                ChildSessionId = r.ChildSessionId == 0xFFFFFFFF ? -1 : (long)r.ChildSessionId,
                TransportStatus = r.TransportStatus,
                TransportStatusHex = "0x" + r.TransportStatus.ToString("X8"),
                TransportDescription = SystemStatus.DescribeTransportError(r.TransportStatus),
                TransportPipe = r.TransportPipe,
                TransportReady = r.TransportReady,
                ReadyToStart = r.ReadyToStart,
                Blocked = r.Blocked,
                NeedsSetup = r.NeedsSetup,
                NextAction = r.NextAction,
                WgcSupported = wgcSupported,
                Monitors = new List<ProbeMonitor>(),
            };

            if (monitors != null)
            {
                foreach (var m in monitors)
                {
                    dto.Monitors.Add(new ProbeMonitor
                    {
                        Device = m.DeviceName,
                        Name = MonitorNaming.NameOf(m),
                        Index = m.Index,
                        Primary = m.IsPrimary,
                        Bounds = new ProbeBounds
                        {
                            X = m.Bounds.X,
                            Y = m.Bounds.Y,
                            Width = m.Bounds.Width,
                            Height = m.Bounds.Height,
                        },
                    });
                }
            }
            return ControlProtocol.ToJson(dto);
        }

        public static bool WgcSupported()
        {
            try { return Recording.CaptureItemFactory.IsSupported; }
            catch (Exception ex)
            {
                Log.Debug("检测 WGC 支持失败: " + ex.Message);
                return false;
            }
        }

        private static string OsBuild()
        {
            try
            {
                const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
                object major = Registry.GetValue(key, "CurrentMajorVersionNumber", null);
                object minor = Registry.GetValue(key, "CurrentMinorVersionNumber", null);
                object build = Registry.GetValue(key, "CurrentBuild", null);
                object ubr = Registry.GetValue(key, "UBR", null);
                if (major is int && minor is int && build != null)
                {
                    string s = major + "." + minor + "." + build;
                    if (ubr is int) s += "." + ubr;
                    return s;
                }
            }
            catch (Exception ex) { Log.Debug("读取系统版本号失败: " + ex.Message); }
            return Environment.OSVersion.Version.ToString();
        }

        [DataContract]
        internal class ProbeReport
        {
            [DataMember(Name = "appVersion", Order = 0)] public string AppVersion;
            [DataMember(Name = "osBuild", Order = 1)] public string OsBuild;
            [DataMember(Name = "editionId", Order = 2)] public string EditionId;
            [DataMember(Name = "buildNumber", Order = 3)] public string BuildNumber;
            [DataMember(Name = "isHomeEdition", Order = 4)] public bool IsHomeEdition;
            [DataMember(Name = "childSessionsEnabled", Order = 5)] public bool ChildSessionsEnabled;
            [DataMember(Name = "termServiceRunning", Order = 6)] public bool TermServiceRunning;
            [DataMember(Name = "rdpListenerEnabled", Order = 7)] public bool RdpListenerEnabled;
            [DataMember(Name = "rdpControlRegistered", Order = 8)] public bool RdpControlRegistered;
            [DataMember(Name = "insideChildSession", Order = 9)] public bool InsideChildSession;
            [DataMember(Name = "childSessionId", Order = 10)] public long ChildSessionId;
            [DataMember(Name = "transportStatus", Order = 11)] public int TransportStatus;
            [DataMember(Name = "transportStatusHex", Order = 12)] public string TransportStatusHex;
            [DataMember(Name = "transportDescription", Order = 13)] public string TransportDescription;
            [DataMember(Name = "transportPipe", Order = 14)] public string TransportPipe;
            [DataMember(Name = "transportReady", Order = 15)] public bool TransportReady;
            [DataMember(Name = "readyToStart", Order = 16)] public bool ReadyToStart;
            [DataMember(Name = "blocked", Order = 17)] public bool Blocked;
            [DataMember(Name = "needsSetup", Order = 18)] public bool NeedsSetup;
            [DataMember(Name = "nextAction", Order = 19)] public string NextAction;
            [DataMember(Name = "wgcSupported", Order = 20)] public bool WgcSupported;
            [DataMember(Name = "monitors", Order = 21)] public List<ProbeMonitor> Monitors;
        }

        [DataContract]
        internal class ProbeMonitor
        {
            [DataMember(Name = "device", Order = 0)] public string Device;
            [DataMember(Name = "name", Order = 1)] public string Name;
            [DataMember(Name = "index", Order = 2)] public int Index;
            [DataMember(Name = "primary", Order = 3)] public bool Primary;
            [DataMember(Name = "bounds", Order = 4)] public ProbeBounds Bounds;
        }

        [DataContract]
        internal class ProbeBounds
        {
            [DataMember(Name = "x", Order = 0)] public int X;
            [DataMember(Name = "y", Order = 1)] public int Y;
            [DataMember(Name = "width", Order = 2)] public int Width;
            [DataMember(Name = "height", Order = 3)] public int Height;
        }
    }
}
