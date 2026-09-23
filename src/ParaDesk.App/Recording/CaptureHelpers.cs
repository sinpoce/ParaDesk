using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using ParaDesk.Core;

namespace ParaDesk.Recording
{
    internal static class CaptureHelpers
    {
        public const int MaxFileStemLength = 40;

        public static int EvenFloor(int v)
        {
            return v - (v % 2);
        }

        public static SizeInt32 EvenSize(int width, int height)
        {
            return new SizeInt32 { Width = EvenFloor(width), Height = EvenFloor(height) };
        }

        public static bool IsUsableSize(SizeInt32 size)
        {
            return size.Width >= 2 && size.Height >= 2;
        }

        public static SizeInt32 EvenPoolSize(SizeInt32 content)
        {
            return new SizeInt32
            {
                Width = Math.Max(2, EvenFloor(content.Width)),
                Height = Math.Max(2, EvenFloor(content.Height)),
            };
        }

        public static bool SameSize(SizeInt32 a, SizeInt32 b)
        {
            return a.Width == b.Width && a.Height == b.Height;
        }

        public static void ConfigureSession(GraphicsCaptureSession session, bool captureCursor)
        {
            try { session.IsCursorCaptureEnabled = captureCursor; }
            catch (Exception ex) { Log.Debug("设置光标捕获失败: " + ex.Message); }

            if (CaptureItemFactory.CanHideBorder)
            {
                try { session.IsBorderRequired = false; }
                catch (Exception ex) { Log.Debug("隐藏捕获边框失败: " + ex.Message); }
            }
        }

        public static string SafeFileStem(string title, string fallback)
        {
            string safe = string.IsNullOrEmpty(title) ? fallback : title;
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            if (safe.Length > MaxFileStemLength)
            {
                int cut = MaxFileStemLength;
                if (char.IsHighSurrogate(safe[cut - 1])) cut--;
                safe = safe.Substring(0, cut);
            }
            if (safe.Length == 0) safe = fallback;
            return safe;
        }

        public static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path) ?? "";
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 2; i < 1000; i++)
            {
                string p = Path.Combine(dir, stem + "_" + i.ToString(CultureInfo.InvariantCulture) + ext);
                if (!File.Exists(p)) return p;
            }
            return Path.Combine(dir, stem + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ext);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable,
            out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);

        public static long GetFreeBytes(string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder)) return -1;
                string dir = Path.GetFullPath(folder);
                while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    dir = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(dir)) return -1;

                if (!dir.EndsWith("\\", StringComparison.Ordinal)) dir += "\\";

                ulong free, total, totalFree;
                if (!GetDiskFreeSpaceEx(dir, out free, out total, out totalFree))
                {
                    Log.Debug("查询磁盘剩余空间失败 (" + Marshal.GetLastWin32Error() + "): " + dir);
                    return -1;
                }
                return free > (ulong)long.MaxValue ? long.MaxValue : (long)free;
            }
            catch (Exception ex)
            {
                Log.Debug("查询磁盘剩余空间失败: " + ex.Message);
                return -1;
            }
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "?";
            double mb = bytes / (1024.0 * 1024.0);
            if (mb >= 1024) return (mb / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            return mb.ToString("0", CultureInfo.InvariantCulture) + " MB";
        }

        public static void ReleaseNative(ref IntPtr p, string what)
        {
            if (p == IntPtr.Zero) return;
            IntPtr local = p;
            p = IntPtr.Zero;
            try { Marshal.Release(local); }
            catch (Exception ex) { Log.Debug("释放" + what + "失败: " + ex.Message); }
        }

        public static void SafeDispose(IDisposable d, string what)
        {
            if (d == null) return;
            try { d.Dispose(); }
            catch (Exception ex) { Log.Debug(what + "失败: " + ex.Message); }
        }

        public static void SafeRun(Action action, string what)
        {
            if (action == null) return;
            try { action(); }
            catch (Exception ex) { Log.Debug(what + "失败: " + ex.Message); }
        }
    }
}
