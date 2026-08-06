using System;
using System.IO;
using System.Threading;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using ParaDesk.Core;

namespace ParaDesk.Recording
{
    /// <summary>
    /// 单帧截图。复用录制那套 WGC 捕获——普通截图方式（BitBlt/PrintWindow）
    /// 对分身桌面这类硬件合成内容只会得到黑图，必须走同一条路。
    /// </summary>
    internal static class Screenshot
    {
        /// <summary>抓取一帧存成 PNG，返回文件路径；失败返回 null。</summary>
        public static string Capture(CaptureTarget target, string outputFolder, out string error)
        {
            error = null;
            if (target == null) { error = L.T("未选择截图目标。"); return null; }
            if (!CaptureItemFactory.IsSupported)
            {
                error = L.T("当前系统不支持屏幕捕获（需要 Windows 10 1903 或更高版本）。");
                return null;
            }

            IDirect3DDevice device = null;
            IntPtr nativeDevice = IntPtr.Zero, nativeContext = IntPtr.Zero;
            Direct3D11CaptureFramePool pool = null;
            GraphicsCaptureSession session = null;

            try
            {
                var item = target.Kind == CaptureTargetKind.Window
                    ? CaptureItemFactory.CreateForWindow(target.Handle)
                    : CaptureItemFactory.CreateForMonitor(target.Handle);
                if (item == null) { error = L.T("无法捕获该目标（窗口可能已关闭）。"); return null; }

                device = Direct3DHelper.CreateDevice(out nativeDevice, out nativeContext);
                if (device == null) { error = L.T("显卡设备初始化失败。"); return null; }

                var size = new SizeInt32
                {
                    Width = item.Size.Width - (item.Size.Width % 2),
                    Height = item.Size.Height - (item.Size.Height % 2),
                };
                if (size.Width < 2 || size.Height < 2) { error = L.T("目标尺寸无效。"); return null; }

                pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);

                Direct3D11CaptureFrame captured = null;
                var got = new ManualResetEventSlim(false);

                pool.FrameArrived += delegate(Direct3D11CaptureFramePool sender, object args)
                {
                    if (captured != null) return;
                    var f = sender.TryGetNextFrame();
                    if (f == null) return;
                    captured = f;
                    got.Set();
                };

                session = pool.CreateCaptureSession(item);
                try { session.IsCursorCaptureEnabled = false; } catch { }
                if (CaptureItemFactory.CanHideBorder)
                {
                    try { session.IsBorderRequired = false; } catch { }
                }
                session.StartCapture();

                // WGC 只在画面变化时送帧；静止画面可能要等一会儿才有第一帧
                if (!got.Wait(TimeSpan.FromSeconds(5)) || captured == null)
                {
                    error = L.T("等待画面超时，请稍后重试。");
                    return null;
                }

                string dir = string.IsNullOrEmpty(outputFolder)
                    ? RecordingOptions.DefaultFolder : outputFolder;
                Directory.CreateDirectory(dir);

                string safe = target.Title ?? L.T("未命名截图");
                foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
                if (safe.Length > 40) safe = safe.Substring(0, 40);
                string path = Path.Combine(dir,
                    string.Format("{0}_{1:yyyyMMdd_HHmmss}.png", safe, DateTime.Now));

                using (captured)
                {
                    SaveSurfaceAsPng(captured.Surface, path);
                }

                Log.Info("已截图: " + path);
                return path;
            }
            catch (Exception ex)
            {
                Log.Error("截图失败", ex);
                error = ex.Message;
                return null;
            }
            finally
            {
                try { if (session != null) session.Dispose(); } catch { }
                try { if (pool != null) pool.Dispose(); } catch { }
                try
                {
                    if (nativeContext != IntPtr.Zero)
                        System.Runtime.InteropServices.Marshal.Release(nativeContext);
                    if (nativeDevice != IntPtr.Zero)
                        System.Runtime.InteropServices.Marshal.Release(nativeDevice);
                }
                catch { }
            }
        }

        /// <summary>
        /// 用 WinRT 的 SoftwareBitmap + BitmapEncoder 存 PNG。
        /// 走 WinRT 这条路可以直接从 D3D 表面拿位图，不必自己做纹理回读。
        /// </summary>
        private static void SaveSurfaceAsPng(IDirect3DSurface surface, string path)
        {
            var bmpTask = SoftwareBitmap.CreateCopyFromSurfaceAsync(surface).AsTask();
            bmpTask.Wait(TimeSpan.FromSeconds(15));
            using (var bitmap = bmpTask.Result)
            {
                var folderTask = StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)).AsTask();
                folderTask.Wait(TimeSpan.FromSeconds(10));

                var fileTask = folderTask.Result.CreateFileAsync(
                    Path.GetFileName(path), CreationCollisionOption.ReplaceExisting).AsTask();
                fileTask.Wait(TimeSpan.FromSeconds(10));

                var streamTask = fileTask.Result.OpenAsync(FileAccessMode.ReadWrite).AsTask();
                streamTask.Wait(TimeSpan.FromSeconds(10));

                using (IRandomAccessStream stream = streamTask.Result)
                {
                    var encTask = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask();
                    encTask.Wait(TimeSpan.FromSeconds(10));
                    var encoder = encTask.Result;

                    // 捕获帧带 alpha 通道但内容不透明；转成 Ignore 避免某些看图程序显示异常
                    var converted = SoftwareBitmap.Convert(
                        bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                    encoder.SetSoftwareBitmap(converted);

                    var flushTask = encoder.FlushAsync().AsTask();
                    flushTask.Wait(TimeSpan.FromSeconds(15));
                    converted.Dispose();
                }
            }
        }
    }
}
