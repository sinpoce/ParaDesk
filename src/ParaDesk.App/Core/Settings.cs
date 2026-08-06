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
        /// <summary>动作标识：toggleDesktop / toggleViewOnly / detach。</summary>
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

        public const int CurrentSchema = 1;

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
            if (!string.IsNullOrEmpty(ActiveProfile))
            {
                foreach (var p in Profiles)
                {
                    if (string.Equals(p.Name, ActiveProfile, StringComparison.Ordinal)) return p;
                }
            }
            return Profiles[0];
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
                if (string.Equals(p.Name, name, StringComparison.Ordinal)) return true;
            return false;
        }

        public bool RemoveProfile(string name)
        {
            if (Profiles == null || Profiles.Count <= 1) return false;   // 至少保留一个
            for (int i = 0; i < Profiles.Count; i++)
            {
                if (!string.Equals(Profiles[i].Name, name, StringComparison.Ordinal)) continue;
                Profiles.RemoveAt(i);
                if (string.Equals(ActiveProfile, name, StringComparison.Ordinal))
                    ActiveProfile = Profiles[0].Name;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 按当前显示器组合挑选最合适的档案：优先选目标显示器仍然存在的那个。
        /// 显示器插拔后自动套用，省得用户每次手动切。
        /// </summary>
        public DesktopProfile PickForCurrentLayout(IEnumerable<string> availableDevices)
        {
            var active = GetActiveProfile();
            if (availableDevices == null) return active;

            var set = new List<string>(availableDevices);
            // 当前档案的目标屏还在，就不折腾
            if (active.MonitorDevice == null || set.Contains(active.MonitorDevice)) return active;

            foreach (var p in Profiles)
                if (p.MonitorDevice != null && set.Contains(p.MonitorDevice)) return p;

            return active;
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
                try
                {
                    if (!File.Exists(path)) return AppSettings.CreateDefault();

                    // 必须自己剥掉 BOM：DataContractJsonSerializer 见到 BOM 会直接
                    // 报"意外字符 ï"。用记事本或 PowerShell 编辑过这个文件就会带 BOM，
                    // 那时用户的全部设置会被静默丢弃——这是不可接受的。
                    byte[] bytes = File.ReadAllBytes(path);
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
                catch (Exception ex)
                {
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

        private static AppSettings Migrate(AppSettings s)
        {
            if (s.SchemaVersion < 1)
            {
                // 首个版本，无需迁移；此处保留升级钩子
                s.SchemaVersion = 1;
            }
            var def = AppSettings.CreateDefault();
            if (s.Profiles == null || s.Profiles.Count == 0) s.Profiles = def.Profiles;
            if (s.Hotkeys == null) s.Hotkeys = def.Hotkeys;
            if (string.IsNullOrEmpty(s.Language)) s.Language = def.Language;
            if (s.Recording == null) s.Recording = ParaDesk.Recording.RecordingOptions.CreateDefault();
            s.Recording.Normalize();

            // 校正越界的枚举与数值。手工改坏 settings.json 后，越界的枚举会让
            // ComboBox.SelectedIndex 在主窗体构造期抛异常——那时 Application.Run
            // 还没装上异常处理器，表现为启动即崩且无任何提示。
            foreach (var p in s.Profiles)
            {
                if (p == null) continue;
                if (!Enum.IsDefined(typeof(WindowMode), p.WindowMode)) p.WindowMode = WindowMode.Fullscreen;
                if (!Enum.IsDefined(typeof(ResolutionMode), p.ResolutionMode)) p.ResolutionMode = ResolutionMode.FollowMonitor;
                if (!Enum.IsDefined(typeof(ClipboardMode), p.Clipboard)) p.Clipboard = ClipboardMode.Shared;
                if (p.ScalePercent != 0 && (p.ScalePercent < 100 || p.ScalePercent > 500)) p.ScalePercent = 0;
                if (p.CustomWidth < 200 || p.CustomWidth > 8192) p.CustomWidth = 1920;
                if (p.CustomHeight < 200 || p.CustomHeight > 8192) p.CustomHeight = 1080;
                if (string.IsNullOrEmpty(p.Name)) p.Name = L.T("默认桌面");
            }
            return s;
        }
    }
}
