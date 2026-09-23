using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace ParaDesk.Core
{
    /// <summary>桌面窗口呈现方式。</summary>
    public enum WindowMode
    {
        /// <summary>无边框全屏钉在指定显示器（默认）。</summary>
        Fullscreen = 0,
        /// <summary>普通可缩放窗口，松手后分辨率贴齐窗口。</summary>
        Windowed = 1,
        /// <summary>置顶悬浮小窗（画中画）。</summary>
        Pip = 2,
    }

    /// <summary>分辨率来源。</summary>
    public enum ResolutionMode
    {
        /// <summary>跟随所在显示器的实际分辨率（默认）。</summary>
        FollowMonitor = 0,
        /// <summary>使用 CustomWidth/CustomHeight。</summary>
        Custom = 1,
    }

    /// <summary>剪贴板策略。系统默认是与主桌面共享。</summary>
    public enum ClipboardMode
    {
        Shared = 0,
        Manual = 1,
        Off = 2,
    }

    public enum DesktopAudioMode
    {
        Local = 0,
        Remote = 1,
        Mute = 2,
    }

    [DataContract]
    public class DesktopProfile
    {
        [DataMember(Name = "name")] public string Name { get; set; }

        /// <summary>目标显示器设备名（\\.\DISPLAY1）。空=主屏之外的第一块。</summary>
        [DataMember(Name = "monitorDevice")] public string MonitorDevice { get; set; }

        [DataMember(Name = "windowMode")] public WindowMode WindowMode { get; set; }
        [DataMember(Name = "resolutionMode")] public ResolutionMode ResolutionMode { get; set; }
        [DataMember(Name = "customWidth")] public int CustomWidth { get; set; }
        [DataMember(Name = "customHeight")] public int CustomHeight { get; set; }

        /// <summary>桌面缩放百分比，100–500。0=自动跟随所在显示器 DPI。</summary>
        [DataMember(Name = "scalePercent")] public int ScalePercent { get; set; }

        [DataMember(Name = "viewOnly")] public bool ViewOnly { get; set; }
        [DataMember(Name = "alwaysOnTop")] public bool AlwaysOnTop { get; set; }
        [DataMember(Name = "clipboard")] public ClipboardMode Clipboard { get; set; }

        [DataMember(Name = "desktopAudio")] public DesktopAudioMode DesktopAudio { get; set; }

        [DataMember(Name = "startupCommand")] public string StartupCommand { get; set; }

        [DataMember(Name = "startupWorkingDir")] public string StartupWorkingDir { get; set; }

        [DataMember(Name = "keepAwake")] public bool KeepAwake { get; set; }

        [DataMember(Name = "windowX")] public int WindowX { get; set; }
        [DataMember(Name = "windowY")] public int WindowY { get; set; }
        [DataMember(Name = "windowWidth")] public int WindowWidth { get; set; }
        [DataMember(Name = "windowHeight")] public int WindowHeight { get; set; }

        public static DesktopProfile CreateDefault()
        {
            return new DesktopProfile
            {
                Name = L.T("默认桌面"),
                MonitorDevice = null,
                WindowMode = WindowMode.Fullscreen,
                ResolutionMode = ResolutionMode.FollowMonitor,
                CustomWidth = 1920,
                CustomHeight = 1080,
                ScalePercent = 0,
                ViewOnly = false,
                AlwaysOnTop = false,
                Clipboard = ClipboardMode.Shared,
            };
        }
    }

    [DataContract]
    public class HotkeyBinding
    {
        [DataMember(Name = "action")] public string Action { get; set; }
        /// <summary>MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8 的组合。</summary>
        [DataMember(Name = "modifiers")] public int Modifiers { get; set; }
        /// <summary>System.Windows.Forms.Keys 的整型值。</summary>
        [DataMember(Name = "key")] public int Key { get; set; }
        [DataMember(Name = "enabled")] public bool Enabled { get; set; }
    }

    /// <summary>
    /// 用户给某块屏起的名字。
    /// 按 DeviceName（\\.\DISPLAY1）存——那是唯一在重新枚举后仍然稳定的键，
    /// 序号会随 Screen.AllScreens 的顺序变。
    /// </summary>
    [DataContract]
    public class MonitorName
    {
        [DataMember(Name = "device")] public string Device { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
    }

    [DataContract]
    public class AppSettings
    {
        [DataMember(Name = "schemaVersion")] public int SchemaVersion { get; set; }
        [DataMember(Name = "language")] public string Language { get; set; }
        [DataMember(Name = "minimizeToTray")] public bool MinimizeToTray { get; set; }
        [DataMember(Name = "runAtStartup")] public bool RunAtStartup { get; set; }
        [DataMember(Name = "startMinimized")] public bool StartMinimized { get; set; }
        [DataMember(Name = "confirmBeforeClose")] public bool ConfirmBeforeClose { get; set; }
        [DataMember(Name = "autoLogoffOnShutdown")] public bool AutoLogoffOnShutdown { get; set; }
        /// <summary>首次运行向导是否已经走过（跳过也算）。</summary>
        [DataMember(Name = "wizardShown")] public bool WizardShown { get; set; }
        [DataMember(Name = "profiles")] public List<DesktopProfile> Profiles { get; set; }
        [DataMember(Name = "activeProfile")] public string ActiveProfile { get; set; }
        [DataMember(Name = "hotkeys")] public List<HotkeyBinding> Hotkeys { get; set; }
        [DataMember(Name = "recording")] public ParaDesk.Recording.RecordingOptions Recording { get; set; }
        /// <summary>显示器自定义名称；没起过名的屏不会出现在这里。</summary>
        [DataMember(Name = "monitorNames")] public List<MonitorName> MonitorNames { get; set; }

        [DataMember(Name = "autoStartDesktop")] public bool AutoStartDesktop { get; set; }

        [DataMember(Name = "autoReattach")] public bool AutoReattach { get; set; }

        [DataMember(Name = "checkUpdates")] public bool CheckUpdates { get; set; }

        [DataMember(Name = "preferredProfile")] public string PreferredProfile { get; set; }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext c)
        {
            AutoReattach = true;
        }

        public const int CurrentSchema = 2;

        public static AppSettings CreateDefault()
        {
            var s = new AppSettings
            {
                SchemaVersion = CurrentSchema,
                Language = "auto",
                MinimizeToTray = true,
                RunAtStartup = false,
                StartMinimized = false,
                ConfirmBeforeClose = true,
                AutoLogoffOnShutdown = true,
                AutoStartDesktop = false,
                AutoReattach = true,
                CheckUpdates = false,
                Profiles = new List<DesktopProfile> { DesktopProfile.CreateDefault() },
                ActiveProfile = L.T("默认桌面"),
                Recording = ParaDesk.Recording.RecordingOptions.CreateDefault(),
                Hotkeys = new List<HotkeyBinding>
                {
                    // Ctrl+Alt+D 显示/收起桌面；Ctrl+Alt+V 切换 View-only
                    new HotkeyBinding { Action = "toggleDesktop", Modifiers = 2 | 1, Key = (int)'D', Enabled = true },
                    new HotkeyBinding { Action = "toggleViewOnly", Modifiers = 2 | 1, Key = (int)'V', Enabled = true },
                    // Ctrl+Alt+R 起停录制
                    new HotkeyBinding { Action = "toggleRecording", Modifiers = 2 | 1, Key = (int)'R', Enabled = true },
                    new HotkeyBinding { Action = "screenshot", Modifiers = 0, Key = 0, Enabled = false },
                    new HotkeyBinding { Action = "togglePause", Modifiers = 0, Key = 0, Enabled = false },
                    new HotkeyBinding { Action = "detach", Modifiers = 0, Key = 0, Enabled = false },
                    new HotkeyBinding { Action = "pushClipboard", Modifiers = 0, Key = 0, Enabled = false },
                    new HotkeyBinding { Action = "pullClipboard", Modifiers = 0, Key = 0, Enabled = false },
                },
            };
            return s;
        }

        public DesktopProfile GetActiveProfile()
        {
            if (Profiles == null || Profiles.Count == 0)
            {
                Profiles = new List<DesktopProfile> { DesktopProfile.CreateDefault() };
            }
            var p = FindProfile(ActiveProfile);
            return p ?? Profiles[0];
        }

        public DesktopProfile FindProfile(string name)
        {
            if (Profiles == null || string.IsNullOrEmpty(name)) return null;
            foreach (var p in Profiles)
            {
                if (p != null && string.Equals(p.Name, name, StringComparison.Ordinal)) return p;
            }
            return null;
        }

        /// <summary>新增一个档案；名称重复时自动加序号。返回最终名称。</summary>
        public string AddProfile(DesktopProfile p)
        {
            if (Profiles == null) Profiles = new List<DesktopProfile>();
            string baseName = string.IsNullOrEmpty(p.Name) ? L.T("新建方案") : p.Name;
            string name = baseName;
            int n = 2;
            while (HasProfile(name)) { name = baseName + " " + n; n++; }
            p.Name = name;
            Profiles.Add(p);
            return name;
        }

        public bool HasProfile(string name)
        {
            if (Profiles == null) return false;
            foreach (var p in Profiles)
                if (p != null && string.Equals(p.Name, name, StringComparison.Ordinal)) return true;
            return false;
        }

        public bool RemoveProfile(string name)
        {
            if (Profiles == null || Profiles.Count <= 1) return false;   // 至少保留一个
            for (int i = 0; i < Profiles.Count; i++)
            {
                if (Profiles[i] == null || !string.Equals(Profiles[i].Name, name, StringComparison.Ordinal)) continue;
                Profiles.RemoveAt(i);
                if (string.Equals(ActiveProfile, name, StringComparison.Ordinal))
                    ActiveProfile = Profiles[0].Name;
                if (string.Equals(PreferredProfile, name, StringComparison.Ordinal))
                    PreferredProfile = ActiveProfile;
                return true;
            }
            return false;
        }

        public DesktopProfile PickForCurrentLayout(IEnumerable<string> availableDevices)
        {
            var active = GetActiveProfile();
            if (availableDevices == null) return active;

            var set = new List<string>(availableDevices);

            var preferred = FindProfile(PreferredProfile);
            if (preferred != null && TargetAvailable(preferred, set)) return preferred;

            if (TargetAvailable(active, set)) return active;

            foreach (var p in Profiles)
                if (p != null && !string.IsNullOrEmpty(p.MonitorDevice) && set.Contains(p.MonitorDevice)) return p;

            return active;
        }

        private static bool TargetAvailable(DesktopProfile p, List<string> devices)
        {
            return string.IsNullOrEmpty(p.MonitorDevice) || devices.Contains(p.MonitorDevice);
        }
    }

    /// <summary>设置持久化。用 BCL 自带的 DataContractJsonSerializer，避免引入依赖。</summary>
    internal static class SettingsStore
    {
        private static readonly object Sync = new object();

        public static AppSettings Load()
        {
            lock (Sync)
            {
                string path = AppInfo.SettingsPath;
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        if (!File.Exists(path)) return AppSettings.CreateDefault();

                        return FromJsonBytes(ReadAllBytesShared(path));
                    }
                    catch (Exception ex)
                    {
                        bool transient = ex is IOException || ex is UnauthorizedAccessException;
                        if (transient && attempt < ReadRetries)
                        {
                            Log.Debug("读取设置暂时失败，稍后重试: " + ex.Message);
                            System.Threading.Thread.Sleep(ReadRetryDelayMs);
                            continue;
                        }

                        Log.Error("读取设置失败", ex);
                        // 不要静默吞掉：把损坏的文件留证，否则用户只会看到"设置全没了"
                        // 却查不出原因，而且下一次保存会把它彻底覆盖。
                        try
                        {
                            string bad = path + ".bad-" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                            File.Copy(path, bad, true);
                            Log.Warn("已把无法解析的设置文件备份到 " + bad + "，本次使用默认设置");
                        }
                        catch (Exception ex2) { Log.Debug("备份损坏设置失败: " + ex2.Message); }

                        return AppSettings.CreateDefault();
                    }
                }
            }
        }

        internal static AppSettings FromJsonBytes(byte[] bytes)
        {
            int offset = 0;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                offset = 3;

            using (var ms = new MemoryStream(bytes, offset, bytes.Length - offset))
            {
                var ser = new DataContractJsonSerializer(typeof(AppSettings));
                var s = ser.ReadObject(ms) as AppSettings;
                if (s == null) return AppSettings.CreateDefault();
                return Migrate(s);
            }
        }

        internal static bool TryReadQuietly(out AppSettings settings, out string error)
        {
            settings = null;
            error = null;
            string path = AppInfo.SettingsPath;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        settings = AppSettings.CreateDefault();
                        return true;
                    }
                    settings = FromJsonBytes(ReadAllBytesShared(path));
                    return true;
                }
                catch (Exception ex)
                {
                    bool transient = ex is IOException || ex is UnauthorizedAccessException;
                    if (!transient || attempt >= ReadRetries)
                    {
                        error = ex.GetType().Name + ": " + ex.Message;
                        return false;
                    }
                    System.Threading.Thread.Sleep(ReadRetryDelayMs);
                }
            }
        }

        private const int ReadRetries = 3;
        private const int ReadRetryDelayMs = 200;

        private static byte[] ReadAllBytesShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                var bytes = new byte[fs.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int n = fs.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read == bytes.Length) return bytes;
                var part = new byte[read];
                Array.Copy(bytes, part, read);
                return part;
            }
        }

        public static void Save(AppSettings s)
        {
            if (s == null) return;
            lock (Sync)
            {
                try
                {
                    s.SchemaVersion = AppSettings.CurrentSchema;
                    string path = AppInfo.SettingsPath;
                    string tmp = path + ".tmp";

                    using (var fs = File.Create(tmp))
                    {
                        var ser = new DataContractJsonSerializer(typeof(AppSettings));
                        ser.WriteObject(fs, s);
                    }
                    // 原子替换，避免写一半断电留下损坏文件
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                }
                catch (Exception ex)
                {
                    Log.Error("保存设置失败", ex);
                }
            }
        }

        private const int MaxWindowCoordinate = 16384;

        private static AppSettings Migrate(AppSettings s)
        {
            if (s.SchemaVersion < 1)
            {
                s.SchemaVersion = 1;
            }
            if (s.SchemaVersion < 2)
            {
                s.SchemaVersion = 2;
            }

            var def = AppSettings.CreateDefault();

            if (s.Profiles == null) s.Profiles = new List<DesktopProfile>();
            s.Profiles.RemoveAll(p => p == null);
            if (s.Profiles.Count == 0) s.Profiles = def.Profiles;

            if (string.IsNullOrEmpty(s.Language)) s.Language = def.Language;
            if (s.Recording == null) s.Recording = ParaDesk.Recording.RecordingOptions.CreateDefault();
            s.Recording.Normalize();

            s.Hotkeys = MigrateHotkeys(s.Hotkeys, def.Hotkeys);

            // 校正越界的枚举与数值。手工改坏 settings.json 后，越界的枚举会让
            // ComboBox.SelectedIndex 在主窗体构造期抛异常——那时 Application.Run
            // 还没装上异常处理器，表现为启动即崩且无任何提示。
            foreach (var p in s.Profiles)
            {
                if (!Enum.IsDefined(typeof(WindowMode), p.WindowMode)) p.WindowMode = WindowMode.Fullscreen;
                if (!Enum.IsDefined(typeof(ResolutionMode), p.ResolutionMode)) p.ResolutionMode = ResolutionMode.FollowMonitor;
                if (!Enum.IsDefined(typeof(ClipboardMode), p.Clipboard)) p.Clipboard = ClipboardMode.Shared;
                if (!Enum.IsDefined(typeof(DesktopAudioMode), p.DesktopAudio)) p.DesktopAudio = DesktopAudioMode.Local;
                if (p.ScalePercent != 0 && (p.ScalePercent < 100 || p.ScalePercent > 500)) p.ScalePercent = 0;
                if (p.CustomWidth < 200 || p.CustomWidth > 8192) p.CustomWidth = 1920;
                if (p.CustomHeight < 200 || p.CustomHeight > 8192) p.CustomHeight = 1080;
                if (string.IsNullOrEmpty(p.Name)) p.Name = L.T("默认桌面");

                p.WindowX = SaneCoordinate(p.WindowX);
                p.WindowY = SaneCoordinate(p.WindowY);
                p.WindowWidth = SaneCoordinate(p.WindowWidth);
                p.WindowHeight = SaneCoordinate(p.WindowHeight);
                if (p.WindowWidth == 0 || p.WindowHeight == 0) { p.WindowWidth = 0; p.WindowHeight = 0; }

                p.StartupCommand = TrimToNull(p.StartupCommand);
                p.StartupWorkingDir = TrimToNull(p.StartupWorkingDir);
            }

            if (s.FindProfile(s.ActiveProfile) == null) s.ActiveProfile = s.Profiles[0].Name;

            if (s.FindProfile(s.PreferredProfile) == null) s.PreferredProfile = s.ActiveProfile;

            return s;
        }

        private static List<HotkeyBinding> MigrateHotkeys(List<HotkeyBinding> hotkeys, List<HotkeyBinding> defaults)
        {
            var result = new List<HotkeyBinding>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            var taken = new HashSet<long>();

            if (hotkeys != null)
            {
                foreach (var h in hotkeys)
                {
                    if (h == null || string.IsNullOrEmpty(h.Action)) continue;
                    if (!seen.Add(h.Action)) continue;
                    NormalizeHotkey(h);
                    result.Add(h);
                    if (h.Enabled && h.Key != 0) taken.Add(HotkeyCombo(h));
                }
            }

            foreach (var d in defaults)
            {
                if (!seen.Add(d.Action)) continue;
                if (d.Enabled && d.Key != 0 && !taken.Add(HotkeyCombo(d)))
                {
                    Log.Info("热键迁移：动作 " + d.Action + " 的默认组合已被其它动作占用，补成未设置");
                    result.Add(new HotkeyBinding { Action = d.Action, Modifiers = 0, Key = 0, Enabled = false });
                    continue;
                }
                result.Add(d);
            }
            return result;
        }

        private static long HotkeyCombo(HotkeyBinding h)
        {
            return ((long)(h.Modifiers & 0xF) << 32) | (uint)h.Key;
        }

        private static void NormalizeHotkey(HotkeyBinding h)
        {
            h.Modifiers = h.Modifiers < 0 ? 0 : (h.Modifiers & 0xF);

            int key = h.Key < 0 ? 0 : (h.Key & 0xFFFF);
            if (key > 255) key = 0;
            h.Key = key;

            if (h.Key == 0) h.Enabled = false;
        }

        private static int SaneCoordinate(int v)
        {
            return v < 0 || v > MaxWindowCoordinate ? 0 : v;
        }

        private static string TrimToNull(string s)
        {
            if (s == null) return null;
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }
    }
}
