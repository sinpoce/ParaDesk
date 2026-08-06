using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using ParaDesk.Core;
using ParaDesk.Native;

namespace ParaDesk.Recording
{
    /// <summary>录制时的音频来源。</summary>
    public enum AudioSource
    {
        None = 0,
        System = 1,      // 系统播放声音（环回）
        Microphone = 2,  // 麦克风
        Both = 3,        // 两者混合
    }

    [DataContract]
    public class RecordingOptions
    {
        [DataMember(Name = "outputFolder")] public string OutputFolder { get; set; }
        [DataMember(Name = "frameRate")] public int FrameRate { get; set; }
        [DataMember(Name = "bitrateMbps")] public int BitrateMbps { get; set; }
        [DataMember(Name = "captureCursor")] public bool CaptureCursor { get; set; }
        [DataMember(Name = "audio")] public AudioSource Audio { get; set; }

        public int BitrateBps { get { return Math.Max(1, BitrateMbps) * 1000000; } }

        public static string DefaultFolder
        {
            get
            {
                string d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                    AppInfo.ProductName);
                return d;
            }
        }

        public static RecordingOptions CreateDefault()
        {
            return new RecordingOptions
            {
                OutputFolder = DefaultFolder,
                FrameRate = 30,
                BitrateMbps = 12,
                CaptureCursor = true,
                Audio = AudioSource.System,
            };
        }

        public void Normalize()
        {
            if (FrameRate < 5 || FrameRate > 240) FrameRate = 30;
            if (BitrateMbps < 1 || BitrateMbps > 200) BitrateMbps = 12;
            if (!Enum.IsDefined(typeof(AudioSource), Audio)) Audio = AudioSource.System;
            if (string.IsNullOrEmpty(OutputFolder)) OutputFolder = DefaultFolder;
        }
    }

    /// <summary>枚举当前可录制的目标：每个分身桌面窗口 + 每块显示器。</summary>
    internal static class CaptureTargetEnumerator
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        /// <summary>
        /// windows 传入当前打开的分身桌面窗口（句柄 + 标题）。
        /// 分身桌面必须单列出来——它才是用户真正想单独录的东西，
        /// 而且它的画面是硬件合成的，只有按窗口捕获才录得到。
        /// </summary>
        public static List<CaptureTarget> Enumerate(IEnumerable<KeyValuePair<IntPtr, string>> desktopWindows)
        {
            var list = new List<CaptureTarget>();

            if (desktopWindows != null)
            {
                foreach (var kv in desktopWindows)
                {
                    if (kv.Key == IntPtr.Zero) continue;
                    list.Add(new CaptureTarget
                    {
                        Kind = CaptureTargetKind.Window,
                        Handle = kv.Key,
                        Title = kv.Value,
                    });
                }
            }

            foreach (var m in MonitorService.Enumerate())
            {
                // 取该显示器矩形中心点反查 HMONITOR，比枚举回调简单且足够可靠
                var pt = new POINT
                {
                    X = m.Bounds.X + m.Bounds.Width / 2,
                    Y = m.Bounds.Y + m.Bounds.Height / 2,
                };
                IntPtr hmon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
                if (hmon == IntPtr.Zero) continue;

                list.Add(new CaptureTarget
                {
                    Kind = CaptureTargetKind.Monitor,
                    Handle = hmon,
                    DeviceName = m.DeviceName,
                    Title = MonitorNaming.Describe(m),
                });
            }

            return list;
        }
    }
}
