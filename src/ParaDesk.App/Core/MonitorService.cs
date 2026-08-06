using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ParaDesk.Core
{
    /// <summary>一块显示器的稳定描述。Screen 对象每次枚举都是新实例，只能靠 DeviceName 匹配。</summary>
    internal class MonitorInfo
    {
        public string DeviceName;      // \\.\DISPLAY1
        public Rectangle Bounds;       // 虚拟桌面坐标（副屏可能为负）
        public Rectangle WorkingArea;
        public bool IsPrimary;
        public int Index;              // 1 起，仅用于显示

        public string ShortName
        {
            get { return DeviceName == null ? "?" : DeviceName.Replace(@"\\.\", ""); }
        }

        public string Display
        {
            get
            {
                return string.Format(L.T("显示器 {0}: {1}  {2}×{3}{4}"),
                    Index, ShortName, Bounds.Width, Bounds.Height, IsPrimary ? L.T("（主屏）") : "");
            }
        }
    }

    /// <summary>
    /// 显示器枚举与热插拔通知。
    /// 两个坑：(1) Screen 对象的 Bounds 是构造时快照，永不刷新，所以绝不缓存实例；
    /// (2) DisplaySettingsChanged 在 SystemEvents 的工作线程上触发，且一次布局变更
    /// 会连续触发多次，必须去抖并切回 UI 线程。
    /// </summary>
    internal class MonitorService : IDisposable
    {
        private bool _hooked;
        private readonly System.Windows.Forms.Timer _debounce;

        /// <summary>显示器布局发生变化（插拔/改分辨率/改缩放）。已在 UI 线程去抖后触发。</summary>
        public event EventHandler LayoutChanged;

        public MonitorService()
        {
            _debounce = new System.Windows.Forms.Timer();
            _debounce.Interval = 800;
            _debounce.Tick += delegate
            {
                _debounce.Stop();
                Log.Info("显示器布局变化（去抖后）");
                var h = LayoutChanged;
                if (h != null) h(this, EventArgs.Empty);
            };

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            _hooked = true;
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            // 在工作线程上被调用；Timer 属于 UI 线程，重启它即可把后续处理搬回 UI 线程
            try
            {
                _debounce.Stop();
                _debounce.Start();
            }
            catch (Exception ex) { Log.Error("处理显示器变化失败", ex); }
        }

        /// <summary>每次调用都重新枚举，不缓存。</summary>
        public static List<MonitorInfo> Enumerate()
        {
            var list = new List<MonitorInfo>();
            Screen[] screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                Screen s = screens[i];
                list.Add(new MonitorInfo
                {
                    DeviceName = s.DeviceName,
                    Bounds = s.Bounds,
                    WorkingArea = s.WorkingArea,
                    IsPrimary = s.Primary,
                    Index = i + 1,
                });
            }
            return list;
        }

        /// <summary>按设备名解析显示器；找不到时回退到首个非主屏，再回退到主屏。</summary>
        public static MonitorInfo Resolve(string deviceName)
        {
            var all = Enumerate();
            if (all.Count == 0) return null;

            if (!string.IsNullOrEmpty(deviceName))
            {
                foreach (var m in all)
                    if (string.Equals(m.DeviceName, deviceName, StringComparison.Ordinal)) return m;
                Log.Warn("目标显示器已不存在，回退默认: " + deviceName);
            }

            foreach (var m in all)
                if (!m.IsPrimary) return m;
            return all[0];
        }

        /// <summary>
        /// 在给定列表里精确查找设备，找不到返回 null——**不回退**。
        /// Resolve 找不到时会默默换一块屏、只写一行日志，调用方无从判断到底命中没有；
        /// 界面要区分"就是这块屏"和"这块屏没了、暂时借用别的"，只能靠这个。
        /// </summary>
        public static MonitorInfo Find(List<MonitorInfo> list, string deviceName)
        {
            if (list == null || string.IsNullOrEmpty(deviceName)) return null;
            foreach (var m in list)
            {
                if (string.Equals(m.DeviceName, deviceName, StringComparison.Ordinal)) return m;
            }
            return null;
        }

        /// <summary>推荐的默认目标：第一块非主屏；只有一块屏时返回主屏。</summary>
        public static MonitorInfo DefaultTarget()
        {
            return Resolve(null);
        }

        public void Dispose()
        {
            if (_hooked)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                _hooked = false;
            }
            if (_debounce != null) { _debounce.Stop(); _debounce.Dispose(); }
        }
    }
}
