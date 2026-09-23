using System;
using System.Collections.Generic;

namespace ParaDesk.Core
{
    /// <summary>
    /// 显示器的显示名。用户没起名时回落到「显示器 N」。
    ///
    /// 为什么单独放一层而不是塞进 MonitorInfo：MonitorService 每次都重新枚举、
    /// 刻意不缓存（Screen.Bounds 是构造时快照），让它去读设置会把
    /// "枚举硬件"和"读用户配置"两件事绑死。这里只做查表。
    /// </summary>
    internal static class MonitorNaming
    {
        private static AppSettings _settings;

        /// <summary>设置加载完成后注入一次。没注入时一切照旧回落到「显示器 N」。</summary>
        public static void Bind(AppSettings s) { _settings = s; }

        /// <summary>用户起的名字；没起过返回 null。</summary>
        public static string CustomName(string device)
        {
            if (_settings == null || _settings.MonitorNames == null || string.IsNullOrEmpty(device))
                return null;

            foreach (var n in _settings.MonitorNames)
            {
                if (n != null && string.Equals(n.Device, device, StringComparison.Ordinal))
                    return string.IsNullOrEmpty(n.Name) ? null : n.Name;
            }
            return null;
        }

        /// <summary>界面上称呼这块屏用的名字：自定义名，或「显示器 N」。</summary>
        public static string NameOf(MonitorInfo m)
        {
            if (m == null) return L.T("未知显示器");
            string custom = CustomName(m.DeviceName);
            if (!string.IsNullOrEmpty(custom)) return custom;
            return string.Format(L.T("显示器 {0}"), m.Index);
        }

        /// <summary>名字 + 分辨率（+ 主屏），用于下拉条目这类需要区分度的地方。</summary>
        public static string Describe(MonitorInfo m)
        {
            if (m == null) return L.T("未知显示器");
            return string.Format(
                m.IsPrimary ? L.T("{0}（{1}×{2}，主屏）") : L.T("{0}（{1}×{2}）"),
                NameOf(m), m.Bounds.Width, m.Bounds.Height);
        }

        public const int MaxNameLength = 40;

        public static string FindDeviceByName(string name)
        {
            if (_settings == null || _settings.MonitorNames == null || string.IsNullOrEmpty(name)) return null;
            foreach (var n in _settings.MonitorNames)
                if (n != null && string.Equals(n.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) return n.Device;
            return null;
        }

        public static bool Rename(string device, string name)
        {
            if (_settings == null || string.IsNullOrEmpty(device)) return false;
            if (_settings.MonitorNames == null) _settings.MonitorNames = new List<MonitorName>();

            name = (name ?? "").Trim();
            if (name.Length > MaxNameLength) name = name.Substring(0, MaxNameLength).TrimEnd();

            for (int i = 0; i < _settings.MonitorNames.Count; i++)
            {
                var n = _settings.MonitorNames[i];
                if (n == null || !string.Equals(n.Device, device, StringComparison.Ordinal)) continue;

                if (name.Length == 0)
                {
                    _settings.MonitorNames.RemoveAt(i);
                    return true;
                }
                if (string.Equals(n.Name, name, StringComparison.Ordinal)) return false;
                n.Name = name;
                return true;
            }

            if (name.Length == 0) return false;
            _settings.MonitorNames.Add(new MonitorName { Device = device, Name = name });
            return true;
        }
    }
}
