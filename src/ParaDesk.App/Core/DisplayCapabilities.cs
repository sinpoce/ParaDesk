using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ParaDesk.Core
{
    /// <summary>显示器支持的一种显示模式。</summary>
    internal class DisplayMode : IEquatable<DisplayMode>
    {
        public int Width;
        public int Height;
        public int Frequency;   // Hz

        public string Resolution { get { return Width + " × " + Height; } }

        /// <summary>常见比例标注，帮用户快速识别（如 16:9）。</summary>
        public string AspectLabel
        {
            get
            {
                if (Width <= 0 || Height <= 0) return "";
                int g = Gcd(Width, Height);
                int w = Width / g, h = Height / g;
                // 约简后过大的比例（如 683:384）对用户无意义，只标注常见值
                if (w <= 32 && h <= 32) return w + ":" + h;
                double r = (double)Width / Height;
                if (Math.Abs(r - 16.0 / 9) < 0.01) return "16:9";
                if (Math.Abs(r - 16.0 / 10) < 0.01) return "16:10";
                if (Math.Abs(r - 4.0 / 3) < 0.01) return "4:3";
                if (Math.Abs(r - 21.0 / 9) < 0.03) return "21:9";
                return "";
            }
        }

        private static int Gcd(int a, int b) { while (b != 0) { int t = b; b = a % b; a = t; } return a; }

        public bool Equals(DisplayMode other)
        {
            return other != null && other.Width == Width && other.Height == Height
                   && other.Frequency == Frequency;
        }

        public override bool Equals(object obj) { return Equals(obj as DisplayMode); }

        public override int GetHashCode()
        {
            return (Width * 397) ^ (Height * 31) ^ Frequency;
        }
    }

    /// <summary>
    /// 枚举显示器真实支持的分辨率与刷新率。
    /// 不再用写死的挡位——不同屏幕能力差别很大（有 60Hz 的也有 144Hz 的，
    /// 有 16:9 也有 16:10），写死既可能给出屏幕做不到的选项，
    /// 也会漏掉它本来支持的模式。
    /// </summary>
    internal static class DisplayCapabilities
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            private const int CCHDEVICENAME = 32;
            private const int CCHFORMNAME = 32;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
            public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
            public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        private const int ENUM_CURRENT_SETTINGS = -1;

        /// <summary>枚举该显示器支持的全部模式（已按分辨率、刷新率排序去重）。</summary>
        public static List<DisplayMode> GetModes(string deviceName)
        {
            var list = new List<DisplayMode>();
            var seen = new HashSet<string>();
            if (string.IsNullOrEmpty(deviceName)) return list;

            try
            {
                var dm = new DEVMODE();
                dm.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE));

                for (int i = 0; EnumDisplaySettings(deviceName, i, ref dm); i++)
                {
                    // 只取 32 位色，避免同一分辨率因色深不同重复出现
                    if (dm.dmBitsPerPel != 32) continue;
                    if (dm.dmPelsWidth < 200 || dm.dmPelsHeight < 200) continue;

                    var mode = new DisplayMode
                    {
                        Width = (int)dm.dmPelsWidth,
                        Height = (int)dm.dmPelsHeight,
                        Frequency = (int)dm.dmDisplayFrequency,
                    };
                    string key = mode.Width + "x" + mode.Height + "@" + mode.Frequency;
                    if (seen.Add(key)) list.Add(mode);
                }
            }
            catch (Exception ex)
            {
                Log.Error("枚举显示模式失败: " + deviceName, ex);
            }

            list.Sort(delegate(DisplayMode a, DisplayMode b)
            {
                long pa = (long)a.Width * a.Height, pb = (long)b.Width * b.Height;
                if (pa != pb) return pb.CompareTo(pa);              // 分辨率从高到低
                if (a.Width != b.Width) return b.Width.CompareTo(a.Width);
                return b.Frequency.CompareTo(a.Frequency);          // 刷新率从高到低
            });
            return list;
        }

        /// <summary>该显示器当前正在使用的模式。</summary>
        public static DisplayMode GetCurrent(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName)) return null;
            try
            {
                var dm = new DEVMODE();
                dm.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE));
                if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm)) return null;
                return new DisplayMode
                {
                    Width = (int)dm.dmPelsWidth,
                    Height = (int)dm.dmPelsHeight,
                    Frequency = (int)dm.dmDisplayFrequency,
                };
            }
            catch (Exception ex)
            {
                Log.Error("读取当前显示模式失败: " + deviceName, ex);
                return null;
            }
        }

        /// <summary>
        /// 该显示器支持的分辨率（去掉刷新率维度）。
        /// 注意宽度必须是偶数——RDP 显示协议的硬性要求，奇数宽会被整组忽略。
        /// </summary>
        public static List<DisplayMode> GetResolutions(string deviceName)
        {
            var result = new List<DisplayMode>();
            var seen = new HashSet<string>();
            foreach (var m in GetModes(deviceName))
            {
                if (m.Width % 2 != 0) continue;
                if (m.Width > 8192 || m.Height > 8192) continue;   // 协议上限
                string key = m.Width + "x" + m.Height;
                if (!seen.Add(key)) continue;
                result.Add(new DisplayMode { Width = m.Width, Height = m.Height, Frequency = 0 });
            }
            return result;
        }

        /// <summary>该显示器支持的刷新率（从高到低）。</summary>
        public static List<int> GetRefreshRates(string deviceName)
        {
            var seen = new HashSet<int>();
            var list = new List<int>();
            foreach (var m in GetModes(deviceName))
            {
                if (m.Frequency < 20 || m.Frequency > 1000) continue;
                if (seen.Add(m.Frequency)) list.Add(m.Frequency);
            }
            list.Sort(delegate(int a, int b) { return b.CompareTo(a); });
            return list;
        }
    }
}
