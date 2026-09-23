using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace ParaDesk.Core
{
    [DataContract]
    internal class DesktopStateSnapshot
    {
        [DataMember(Name = "version", Order = 0)] public int Version { get; set; }
        [DataMember(Name = "running", Order = 1)] public bool Running { get; set; }
        [DataMember(Name = "pid", Order = 2)] public int Pid { get; set; }
        [DataMember(Name = "updatedAt", Order = 3)] public string UpdatedAt { get; set; }
        [DataMember(Name = "appVersion", Order = 4)] public string AppVersion { get; set; }

        [DataMember(Name = "desktopState", Order = 10)] public string DesktopState { get; set; }
        [DataMember(Name = "attached", Order = 11)] public bool Attached { get; set; }
        [DataMember(Name = "childSessionExists", Order = 12)] public bool ChildSessionExists { get; set; }
        [DataMember(Name = "childSessionId", Order = 13)] public long ChildSessionId { get; set; }
        [DataMember(Name = "connectedAt", Order = 14)] public string ConnectedAt { get; set; }

        [DataMember(Name = "profile", Order = 20)] public string Profile { get; set; }
        [DataMember(Name = "monitorDevice", Order = 21)] public string MonitorDevice { get; set; }
        [DataMember(Name = "monitorName", Order = 22)] public string MonitorName { get; set; }
        [DataMember(Name = "monitorX", Order = 23)] public int MonitorX { get; set; }
        [DataMember(Name = "monitorY", Order = 24)] public int MonitorY { get; set; }
        [DataMember(Name = "monitorWidth", Order = 25)] public int MonitorWidth { get; set; }
        [DataMember(Name = "monitorHeight", Order = 26)] public int MonitorHeight { get; set; }

        [DataMember(Name = "width", Order = 30)] public int Width { get; set; }
        [DataMember(Name = "height", Order = 31)] public int Height { get; set; }
        [DataMember(Name = "scalePercent", Order = 32)] public int ScalePercent { get; set; }
        [DataMember(Name = "windowMode", Order = 33)] public string WindowMode { get; set; }

        [DataMember(Name = "viewOnly", Order = 40)] public bool ViewOnly { get; set; }
        [DataMember(Name = "alwaysOnTop", Order = 41)] public bool AlwaysOnTop { get; set; }
        [DataMember(Name = "clipboard", Order = 42)] public string Clipboard { get; set; }

        [DataMember(Name = "recording", Order = 50)] public bool Recording { get; set; }
        [DataMember(Name = "recordingPaused", Order = 51)] public bool RecordingPaused { get; set; }
        [DataMember(Name = "recordingPath", Order = 52)] public string RecordingPath { get; set; }
        [DataMember(Name = "recordingTarget", Order = 53)] public string RecordingTarget { get; set; }
    }

    internal static class StateFile
    {
        public const int CurrentVersion = 1;

        private const int WriteRetries = 2;
        private const int WriteRetryDelayMs = 50;

        private const int MaxFileBytes = 1024 * 1024;

        private static readonly object Sync = new object();

        public static string Path0 { get { return Path.Combine(AppInfo.DataDir, "state.json"); } }

        public static void Write(DesktopStateSnapshot s)
        {
            if (s == null) return;
            lock (Sync)
            {
                try
                {
                    s.Version = CurrentVersion;
                    s.UpdatedAt = DateTime.Now.ToString("o");
                    if (s.Pid == 0) s.Pid = CurrentPid();
                    if (string.IsNullOrEmpty(s.AppVersion)) s.AppVersion = AppInfo.Version;

                    byte[] bytes = Encoding.UTF8.GetBytes(ControlProtocol.ToJson(s));
                    WriteAtomic(Path0, bytes);
                }
                catch (Exception ex)
                {
                    Log.Debug("写入 state.json 失败: " + ex.Message);
                }
            }
        }

        private static void WriteAtomic(string path, byte[] bytes)
        {
            string tmp = path + ".tmp";
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.WriteAllBytes(tmp, bytes);
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                    return;
                }
                catch (IOException)
                {
                    if (attempt >= WriteRetries) throw;
                    Thread.Sleep(WriteRetryDelayMs);
                }
            }
        }

        public static DesktopStateSnapshot TryRead()
        {
            try
            {
                string path = Path0;
                if (!File.Exists(path)) return null;

                byte[] bytes;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length <= 0 || fs.Length > MaxFileBytes) return null;
                    bytes = new byte[(int)fs.Length];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = fs.Read(bytes, read, bytes.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < bytes.Length) return null;
                }

                int offset = 0;
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) offset = 3;
                if (bytes.Length - offset <= 0) return null;

                using (var ms = new MemoryStream(bytes, offset, bytes.Length - offset))
                {
                    var ser = new DataContractJsonSerializer(typeof(DesktopStateSnapshot));
                    return ser.ReadObject(ms) as DesktopStateSnapshot;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("读取 state.json 失败: " + ex.Message);
                return null;
            }
        }

        public static void MarkStopped()
        {
            try
            {
                var s = TryRead();
                if (s == null) return;
                ApplyStopped(s);
                Write(s);
            }
            catch (Exception ex)
            {
                Log.Debug("标记 state.json 为已停止失败: " + ex.Message);
            }
        }

        internal static void ApplyStopped(DesktopStateSnapshot s)
        {
            if (s == null) return;
            s.Running = false;
            s.Attached = false;
            s.DesktopState = "idle";
            s.Recording = false;
            s.RecordingPaused = false;
        }

        private static int _pid;

        private static int CurrentPid()
        {
            if (_pid == 0)
            {
                using (var p = System.Diagnostics.Process.GetCurrentProcess()) _pid = p.Id;
            }
            return _pid;
        }
    }
}
