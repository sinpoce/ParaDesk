using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
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
        public int Index;

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

    internal class MonitorService : IDisposable
    {
        private const int DebounceMs = 800;

        private bool _hooked;
        private bool _disposed;
        private readonly System.Windows.Forms.Timer _debounce;
        private readonly SynchronizationContext _ui;

        public event EventHandler LayoutChanged;

        public MonitorService()
        {
            _ui = SynchronizationContext.Current;
            if (_ui == null || _ui.GetType() == typeof(SynchronizationContext))
            {
                _ui = new WindowsFormsSynchronizationContext();
            }
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                Log.Warn("MonitorService 不是在 STA 线程上构造的，显示器变化通知可能无法送达");

            _debounce = new System.Windows.Forms.Timer();
            _debounce.Interval = DebounceMs;
            _debounce.Tick += OnDebounceTick;

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            _hooked = true;
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            DisplayCapabilities.Invalidate();
            try
            {
                _ui.Post(delegate { RestartDebounce(); }, null);
            }
            catch (Exception ex) { Log.Error("转发显示器变化通知失败", ex); }
        }

        private void RestartDebounce()
        {
            if (_disposed) return;
            try
            {
                _debounce.Stop();
                _debounce.Start();
            }
            catch (Exception ex) { Log.Error("处理显示器变化失败", ex); }
        }

        private void OnDebounceTick(object sender, EventArgs e)
        {
            _debounce.Stop();
            if (_disposed) return;
            Log.Info("显示器布局变化（去抖后）");

            DisplayCapabilities.Invalidate();

            var h = LayoutChanged;
            if (h != null) h(this, EventArgs.Empty);
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
            if (_disposed) return;
            _disposed = true;
            if (_hooked)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                _hooked = false;
            }
            if (_debounce != null) { _debounce.Stop(); _debounce.Dispose(); }
        }
    }
}
