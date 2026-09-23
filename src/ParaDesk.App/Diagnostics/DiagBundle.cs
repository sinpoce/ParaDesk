using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ParaDesk.Core;

namespace ParaDesk.Diagnostics
{
    internal static class DiagBundle
    {
        private const string ForbiddenFile = "credential.bin";

        private const int MaxBadSettingsCopies = 3;

        private const long MaxFileBytes = 8L * 1024 * 1024;

        private const string SetupBackupFile = "setup-backup.json";

        private const string AgentRunValue = "ParaDeskChildAgent";

        private const string ApprovedRunKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        [DllImport("kernel32.dll")]
        private static extern ushort GetUserDefaultUILanguage();

        private delegate byte[] Redactor(byte[] content, out int count);

        public static string Create(string outputPath, out string error)
        {
            error = null;
            string target;
            try
            {
                target = ResolveTarget(outputPath, out error);
                if (target == null) return null;
            }
            catch (Exception ex)
            {
                Log.Error("诊断包：解析输出路径失败", ex);
                error = string.Format(L.T("诊断包的保存位置无效：{0}"), ex.Message);
                return null;
            }

            string tmp = target + ".partial";
            try
            {
                var manifest = new StringBuilder();
                manifest.AppendLine("ParaDesk diagnostic bundle");
                manifest.AppendLine("created  = " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
                manifest.AppendLine("version  = " + AppInfo.Version);
                manifest.AppendLine();

                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    string logDir = Log.Dir;
                    string dataDir = AppInfo.DataDir;
                    int redacted = 0;

                    redacted += AddFile(zip, manifest, Log.Path0, "paradesk.log", RedactLogBytes);
                    redacted += AddFile(zip, manifest, Log.Path0 + ".1", "paradesk.log.1", RedactLogBytes);
                    AddFile(zip, manifest, Path.Combine(logDir, "probe.log"), "probe.log", null);
                    AddFile(zip, manifest, Path.Combine(logDir, "spike-display.log"), "spike-display.log", null);
                    redacted += AddFile(zip, manifest, AppInfo.SettingsPath, "settings.json", RedactSettingsBytes);
                    AddFile(zip, manifest, StateFile.Path0, "state.json", null);

                    foreach (string bad in RecentBadSettings(dataDir))
                        redacted += AddFile(zip, manifest, bad, Path.GetFileName(bad), RedactSettingsBytes);

                    string backup = Path.Combine(logDir, SetupBackupFile);
                    bool hasBackup = FileExistsQuiet(backup) || FileExistsQuiet(backup + ".bad");
                    AddFile(zip, manifest, backup, SetupBackupFile, null);
                    AddFile(zip, manifest, backup + ".bad", SetupBackupFile + ".bad", null);

                    AddText(zip, manifest, "environment.txt", Safe(BuildEnvironmentText, "environment"));
                    AddText(zip, manifest, "monitors.txt", Safe(BuildMonitorsText, "monitors"));
                    AddText(zip, manifest, "system.txt", Safe(BuildSystemText, "system"));

                    manifest.AppendLine();
                    manifest.AppendLine("credential.bin is never included.");
                    if (redacted > 0)
                        manifest.AppendLine("startupCommand values in settings*.json and the startup-command lines in the logs " +
                                            "are redacted (" + redacted + " in total).");
                    if (hasBackup)
                        manifest.AppendLine(SetupBackupFile + " contains the computer name and the DOMAIN\\user that ran setup.");
                    manifest.AppendLine("Logs and settings still contain the Windows user name, monitor names and file paths.");
                    AddText(zip, null, "manifest.txt", manifest.ToString());
                }

                File.Move(tmp, target);
            }
            catch (Exception ex)
            {
                Log.Error("生成诊断包失败", ex);
                TryDelete(tmp);
                error = string.Format(L.T("写入诊断包失败：{0}"),
                                      string.IsNullOrEmpty(ex.Message) ? ex.GetType().Name : ex.Message);
                return null;
            }

            Log.Info("已生成诊断包: " + target);
            return target;
        }

        public static int Run(string[] args)
        {
            string path = null;
            if (args != null)
            {
                for (int i = 1; i < args.Length; i++)
                {
                    string a = args[i];
                    if (string.IsNullOrEmpty(a)) continue;
                    if (a.StartsWith("--", StringComparison.Ordinal) || path != null)
                    {
                        Console.WriteLine(L.T("用法：ParaDesk.exe --diagbundle [<路径>]"));
                        return ExitCodes.Usage;
                    }
                    path = a;
                }
            }

            if (path != null)
            {
                try { path = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, path)); }
                catch (Exception ex)
                {
                    Console.WriteLine(string.Format(L.T("诊断包的保存位置无效：{0}"), ex.Message));
                    return ExitCodes.Failed;
                }
            }

            string error;
            string result = Create(path, out error);
            if (result == null)
            {
                Console.WriteLine(string.IsNullOrEmpty(error) ? string.Format(L.T("生成诊断包失败：{0}"), "?") : error);
                return ExitCodes.Failed;
            }

            Console.WriteLine(result);
            return ExitCodes.Ok;
        }

        private static string AutoName(string dir)
        {
            string stem = "ParaDesk-diag-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string p = Path.Combine(dir, stem + ".zip");
            for (int n = 2; File.Exists(p) || Directory.Exists(p) || File.Exists(p + ".partial"); n++)
                p = Path.Combine(dir, stem + "-" + n.ToString(CultureInfo.InvariantCulture) + ".zip");
            return p;
        }

        private static string ResolveTarget(string outputPath, out string error)
        {
            error = null;
            string target;

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                string dir = null;
                try { dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
                catch (Exception ex) { Log.Debug("取桌面目录失败: " + ex.Message); }
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = AppInfo.DataDir;
                return AutoName(dir);
            }

            string raw = outputPath.Trim();
            target = Path.GetFullPath(raw);
            bool dirLike = raw.EndsWith("\\", StringComparison.Ordinal) || raw.EndsWith("/", StringComparison.Ordinal);
            if (dirLike || Directory.Exists(target))
            {
                if (!Directory.Exists(target))
                {
                    try { Directory.CreateDirectory(target); }
                    catch (Exception ex)
                    {
                        error = string.Format(L.T("无法创建输出目录：{0}"), ex.Message);
                        return null;
                    }
                }
                return AutoName(target);
            }

            if (string.IsNullOrEmpty(Path.GetExtension(target))) target += ".zip";

            if (File.Exists(target) || Directory.Exists(target))
            {
                error = string.Format(L.T("目标位置已有同名的文件或文件夹，没有覆盖：{0}"), target);
                return null;
            }

            string parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                try { Directory.CreateDirectory(parent); }
                catch (Exception ex)
                {
                    error = string.Format(L.T("无法创建输出目录：{0}"), ex.Message);
                    return null;
                }
            }
            return target;
        }

        private static List<string> RecentBadSettings(string dataDir)
        {
            var result = new List<string>();
            try
            {
                if (!Directory.Exists(dataDir)) return result;
                var files = new List<FileInfo>();
                foreach (string f in Directory.GetFiles(dataDir, "settings.json.bad-*"))
                {
                    try { files.Add(new FileInfo(f)); }
                    catch (Exception ex) { Log.Debug("诊断包：读取文件信息失败 " + f + ": " + ex.Message); }
                }
                files.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });
                for (int i = 0; i < files.Count && i < MaxBadSettingsCopies; i++) result.Add(files[i].FullName);
            }
            catch (Exception ex) { Log.Debug("诊断包：列出损坏设置留证失败: " + ex.Message); }
            return result;
        }

        private static int AddFile(ZipArchive zip, StringBuilder manifest, string path, string entryName, Redactor redact)
        {
            if (string.Equals(Path.GetFileName(path), ForbiddenFile, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entryName, ForbiddenFile, StringComparison.OrdinalIgnoreCase))
            {
                manifest.AppendLine("skipped  " + entryName + "  (forbidden)");
                return 0;
            }

            try
            {
                if (!File.Exists(path))
                {
                    manifest.AppendLine("missing  " + entryName);
                    return 0;
                }

                using (var src = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                FileShare.ReadWrite | FileShare.Delete))
                {
                    long len = src.Length;
                    bool truncated = false;
                    if (len > MaxFileBytes)
                    {
                        src.Seek(len - MaxFileBytes, SeekOrigin.Begin);
                        truncated = true;
                    }

                    byte[] content = null;
                    int redacted = 0;
                    if (redact != null)
                    {
                        using (var ms = new MemoryStream())
                        {
                            src.CopyTo(ms);
                            content = ms.ToArray();
                        }
                        if (truncated) content = DropFirstLine(content);
                        content = redact(content, out redacted);
                    }

                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    try { entry.LastWriteTime = File.GetLastWriteTime(path); }
                    catch (Exception ex) { Log.Debug("诊断包：设置条目时间失败: " + ex.Message); }

                    using (var dst = entry.Open())
                    {
                        if (content != null) dst.Write(content, 0, content.Length);
                        else src.CopyTo(dst);
                    }

                    manifest.AppendLine("included " + entryName + "  (" + len + " bytes" +
                                        (truncated ? ", only the last " + MaxFileBytes + " bytes" : "") +
                                        (redacted > 0 ? ", " + redacted + " redacted" : "") + ")");
                    return redacted;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("诊断包：收录 " + path + " 失败: " + ex.Message);
                manifest.AppendLine("failed   " + entryName + "  (" + ex.GetType().Name + ": " + ex.Message + ")");
                return 0;
            }
        }

        private static bool FileExistsQuiet(string path)
        {
            try { return File.Exists(path); }
            catch (Exception) { return false; }
        }

        private static byte[] DropFirstLine(byte[] content)
        {
            int nl = Array.IndexOf(content, (byte)'\n');
            if (nl < 0) return new byte[0];
            var rest = new byte[content.Length - nl - 1];
            Array.Copy(content, nl + 1, rest, 0, rest.Length);
            return rest;
        }

        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        private static string AsLatin1(string s)
        {
            return Latin1.GetString(Encoding.UTF8.GetBytes(s));
        }

        private static readonly Regex StartupCommandField = new Regex(
            "(\"(?i:startupCommand)\"[ \\t\\r\\n]*:[ \\t\\r\\n]*)\"((?:[^\"\\\\]|\\\\.)*)(\"|\\z)",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);

        private static readonly Regex StartupCommandLogLine = new Regex(
            "(" + Regex.Escape(AsLatin1("已执行启动命令")) +
            "|" + Regex.Escape(AsLatin1("执行启动命令失败: ")) +
            "|" + Regex.Escape(AsLatin1("的登录后自动运行已改为: ")) + ")([^\\r\\n]*)",
            RegexOptions.CultureInvariant);

        private static readonly string FailureMarker = AsLatin1("执行启动命令失败: ");

        private const string Redacted = "<redacted>";

        internal static byte[] RedactSettingsBytes(byte[] content, out int count)
        {
            count = 0;
            if (content == null || content.Length == 0) return content ?? new byte[0];
            int n = 0;
            string text = StartupCommandField.Replace(Latin1.GetString(content), delegate(Match m)
            {
                if (m.Groups[2].Length == 0) return m.Value;
                n++;
                return m.Groups[1].Value + "\"<redacted, " + m.Groups[2].Length + " bytes>\"";
            });
            count = n;
            return n == 0 ? content : Latin1.GetBytes(text);
        }

        internal static byte[] RedactLogBytes(byte[] content, out int count)
        {
            count = 0;
            if (content == null || content.Length == 0) return content ?? new byte[0];
            int n = 0;
            string text = StartupCommandLogLine.Replace(Latin1.GetString(content), delegate(Match m)
            {
                string rest = m.Groups[2].Value;
                if (rest.Length == 0) return m.Value;
                string keep = "";
                if (m.Groups[1].Value == FailureMarker)
                {
                    int sep = rest.LastIndexOf(" :: ", StringComparison.Ordinal);
                    if (sep >= 0) keep = rest.Substring(sep);
                }
                n++;
                return m.Groups[1].Value + Redacted + keep;
            });
            count = n;
            return n == 0 ? content : Latin1.GetBytes(text);
        }

        private static void AddText(ZipArchive zip, StringBuilder manifest, string entryName, string text)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(true)))
                w.Write(text ?? "");
            if (manifest != null) manifest.AppendLine("included " + entryName + "  (generated)");
        }

        private static string Safe(Func<string> build, string what)
        {
            try { return build(); }
            catch (Exception ex)
            {
                Log.Error("诊断包：生成 " + what + " 失败", ex);
                return "failed to collect " + what + ": " + ex;
            }
        }

        private static string BuildEnvironmentText()
        {
            var r = SystemStatus.Check();
            var sb = new StringBuilder();
            sb.AppendLine("=== SystemStatus.Check() ===");
            sb.AppendLine("EditionId            = " + r.EditionId);
            sb.AppendLine("BuildNumber          = " + r.BuildNumber);
            sb.AppendLine("IsHomeEdition        = " + r.IsHomeEdition);
            sb.AppendLine("ChildSessionsEnabled = " + r.ChildSessionsEnabled);
            sb.AppendLine("TermServiceRunning   = " + r.TermServiceRunning);
            sb.AppendLine("RdpListenerEnabled   = " + r.RdpListenerEnabled);
            sb.AppendLine("RdpControlRegistered = " + r.RdpControlRegistered);
            sb.AppendLine("InsideChildSession   = " + r.InsideChildSession);
            sb.AppendLine("ChildSessionId       = " + (r.ChildSessionId == 0xFFFFFFFF ? "(none)" : r.ChildSessionId.ToString()));
            sb.AppendLine("TransportStatus      = 0x" + r.TransportStatus.ToString("X8") +
                          " (" + SystemStatus.DescribeTransportError(r.TransportStatus) + ")");
            sb.AppendLine("TransportPipe        = " + r.TransportPipe);
            sb.AppendLine("TransportReady       = " + r.TransportReady);
            sb.AppendLine("ReadyToStart         = " + r.ReadyToStart);
            sb.AppendLine("Blocked              = " + r.Blocked);
            sb.AppendLine("NeedsSetup           = " + r.NeedsSetup);
            sb.AppendLine("NextAction           = " + (r.NextAction ?? "(none)"));
            sb.AppendLine("WgcSupported         = " + WgcSupported());
            return sb.ToString();
        }

        private static bool WgcSupported()
        {
            try { return Recording.CaptureItemFactory.IsSupported; }
            catch (Exception ex)
            {
                Log.Debug("诊断包：检测 WGC 支持失败: " + ex.Message);
                return false;
            }
        }

        private static string BuildMonitorsText()
        {
            var names = ReadMonitorNames();

            var sb = new StringBuilder();
            sb.AppendLine("=== Monitors ===");
            List<MonitorInfo> list = MonitorService.Enumerate();
            foreach (var m in list)
            {
                string custom;
                if (names == null || !names.TryGetValue(m.DeviceName ?? "", out custom))
                    custom = MonitorNaming.CustomName(m.DeviceName);

                var mode = DisplayCapabilities.GetCurrent(m.DeviceName);
                sb.AppendLine("[" + m.Index + "] " + m.DeviceName + (m.IsPrimary ? "  PRIMARY" : ""));
                sb.AppendLine("    customName = " + (string.IsNullOrEmpty(custom) ? "(none)" : custom));
                sb.AppendLine("    bounds     = " + m.Bounds.X + "," + m.Bounds.Y + " " + m.Bounds.Width + "x" + m.Bounds.Height);
                sb.AppendLine("    workArea   = " + m.WorkingArea.X + "," + m.WorkingArea.Y + " " +
                              m.WorkingArea.Width + "x" + m.WorkingArea.Height);
                sb.AppendLine("    mode       = " + (mode == null ? "(unknown)" :
                              mode.Width + "x" + mode.Height + " @ " + mode.Frequency + " Hz"));
            }
            if (list.Count == 0) sb.AppendLine("(no monitors)");
            return sb.ToString();
        }

        private static Dictionary<string, string> ReadMonitorNames()
        {
            try
            {
                string path = AppInfo.SettingsPath;
                if (!File.Exists(path)) return null;
                byte[] bytes;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var ms = new MemoryStream())
                {
                    fs.CopyTo(ms);
                    bytes = ms.ToArray();
                }
                var s = SettingsStore.FromJsonBytes(bytes);
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                if (s != null && s.MonitorNames != null)
                {
                    foreach (var n in s.MonitorNames)
                        if (n != null && !string.IsNullOrEmpty(n.Device)) d[n.Device] = n.Name;
                }
                return d;
            }
            catch (Exception ex)
            {
                Log.Debug("诊断包：读取显示器自定义名失败: " + ex.Message);
                return null;
            }
        }

        private static string BuildSystemText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== System ===");
            sb.AppendLine("EditionId        = " + SystemStatus.EditionId());
            sb.AppendLine("Build            = " + SystemStatus.BuildNumber() + "." + ReadHklm(
                              @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR"));
            sb.AppendLine("OSVersion        = " + Environment.OSVersion.VersionString);
            sb.AppendLine("Is64BitOS        = " + Environment.Is64BitOperatingSystem);
            sb.AppendLine("Is64BitProcess   = " + Environment.Is64BitProcess);
            sb.AppendLine("Architecture     = " + PlatformInfo.Describe());
            sb.AppendLine("NetFxRelease     = " + NetFrameworkRelease());
            sb.AppendLine("CLR              = " + Environment.Version);
            sb.AppendLine("mstscax.dll      = " + FileVersionOf(Path.Combine(Environment.SystemDirectory, "mstscax.dll")));
            sb.AppendLine("InstalledUILang  = " + SafeCultureName(delegate { return CultureInfo.InstalledUICulture; }));
            sb.AppendLine("UserUILang       = " + UserUiLanguage());
            sb.AppendLine("UserLocale       = " + SafeCultureName(delegate { return CultureInfo.CurrentCulture; }));
            sb.AppendLine("AppLanguage      = " + L.Current);
            sb.AppendLine("AppVersion       = " + AppInfo.Version);
            sb.AppendLine("AppPath          = " + SafeString(delegate { return AppInfo.ExecutablePath; }));
            sb.AppendLine("DataDir          = " + AppInfo.DataDir);
            sb.AppendLine("LogDir           = " + Log.Dir);
            sb.AppendLine("SessionId        = " + SafeString(delegate { return Process.GetCurrentProcess().SessionId.ToString(); }));
            sb.AppendLine("TimeZone         = " + SafeString(delegate { return TimeZoneInfo.Local.Id; }));

            sb.AppendLine();
            sb.AppendLine(@"=== HKCU\" + RunKeyStore.KeyPath + " ===");
            sb.AppendLine("(values of the HKCU hive this process sees; an elevated process run as another admin sees that account's)");
            AppendRunValue(sb, AppInfo.ProductName);
            AppendRunValue(sb, AgentRunValue);
            return sb.ToString();
        }

        private static void AppendRunValue(StringBuilder sb, string name)
        {
            string cmd;
            bool exists;
            try
            {
                exists = RunKeyStore.Exists(name);
                cmd = RunKeyStore.Get(name);
            }
            catch (Exception ex)
            {
                sb.AppendLine(name + " = (error: " + ex.Message + ")");
                return;
            }

            if (!exists) sb.AppendLine(name + " = (none)");
            else if (cmd == null) sb.AppendLine(name + " = (present, not a string value)");
            else
            {
                sb.AppendLine(name + " = " + cmd);
                string exe, args;
                RunKeyStore.SplitCommand(cmd, out exe, out args);
                sb.AppendLine("    exeExists     = " + SafeString(delegate { return File.Exists(exe).ToString(); }));
                sb.AppendLine("    sameAsCurrent = " + SafeString(delegate
                {
                    return RunKeyStore.SamePath(exe, AppInfo.ExecutablePath).ToString();
                }));
            }
            sb.AppendLine("    approved      = " + ReadApproved(name));
        }

        private static string ReadApproved(string name)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(ApprovedRunKey, false))
                {
                    if (k == null) return "(no key, default enabled)";
                    var blob = k.GetValue(name) as byte[];
                    if (blob == null || blob.Length == 0) return "(no value, default enabled)";
                    return "0x" + blob[0].ToString("X2", CultureInfo.InvariantCulture) +
                           (blob[0] == 0x03 ? " (disabled)" : " (enabled)");
                }
            }
            catch (Exception ex) { return "(error: " + ex.Message + ")"; }
        }

        private static string NetFrameworkRelease()
        {
            string v = ReadHklm(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release");
            return string.IsNullOrEmpty(v) ? "(unknown)" : v;
        }

        private static string FileVersionOf(string path)
        {
            try
            {
                if (!File.Exists(path)) return "(missing) " + path;
                var vi = FileVersionInfo.GetVersionInfo(path);
                return vi.FileVersion + "  (" + path + ")";
            }
            catch (Exception ex) { return "(error: " + ex.Message + ")"; }
        }

        private static string UserUiLanguage()
        {
            try
            {
                int lcid = GetUserDefaultUILanguage();
                string name;
                try { name = new CultureInfo(lcid).Name; }
                catch (Exception) { name = "?"; }
                return name + " (0x" + lcid.ToString("X4") + ")";
            }
            catch (Exception ex) { return "(error: " + ex.Message + ")"; }
        }

        private static string SafeCultureName(Func<CultureInfo> get)
        {
            try
            {
                var c = get();
                return c == null ? "(null)" : c.Name;
            }
            catch (Exception ex) { return "(error: " + ex.Message + ")"; }
        }

        private static string SafeString(Func<string> get)
        {
            try { return get() ?? "(null)"; }
            catch (Exception ex) { return "(error: " + ex.Message + ")"; }
        }

        private static string ReadHklm(string subKey, string name)
        {
            try
            {
                object v = Registry.GetValue(@"HKEY_LOCAL_MACHINE\" + subKey, name, null);
                return v == null ? "" : v.ToString();
            }
            catch (Exception ex)
            {
                Log.Debug("诊断包：读取注册表 " + subKey + "\\" + name + " 失败: " + ex.Message);
                return "";
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Log.Debug("诊断包：删除临时文件失败 " + path + ": " + ex.Message); }
        }
    }
}
