using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
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
        private const int FramePoolBuffers = 2;

        private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(5);

        private static readonly TimeSpan BitmapCopyTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan FileOpTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan EncodeTimeout = TimeSpan.FromSeconds(15);

        public static void CaptureAsync(CaptureTarget target, string outputFolder, string targetPath,
                                        bool captureCursor, Action<string, string> done)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err = null;
                string path = null;
                try { path = CaptureCore(target, outputFolder, targetPath, captureCursor, out err); }
                catch (Exception ex)
                {
                    Log.Error("截图失败", ex);
                    path = null;
                    err = ex.Message;
                }
                if (done == null) return;
                try { done(path, err); }
                catch (Exception ex) { Log.Error("截图回调异常", ex); }
            });
        }

        public static string Capture(CaptureTarget target, string outputFolder, out string error)
        {
            return CaptureCore(target, outputFolder, null, false, out error);
        }

        private static string CaptureCore(CaptureTarget target, string outputFolder, string targetPath,
                                          bool captureCursor, out string error)
        {
            error = null;
            if (target == null) { error = L.T("未选择截图目标。"); return null; }
            if (!CaptureItemFactory.IsSupported)
            {
                error = L.T("当前系统不支持屏幕捕获（需要 Windows 10 1903 或更高版本）。");
                return null;
            }

            string dir, fileName;
            bool exactPath;
            if (!ResolveOutput(target, outputFolder, targetPath, out dir, out fileName, out exactPath, out error)) return null;

            IDirect3DDevice device = null;
            IntPtr nativeDevice = IntPtr.Zero, nativeContext = IntPtr.Zero;
            Direct3D11CaptureFramePool pool = null;
            GraphicsCaptureSession session = null;

            var gate = new object();
            Direct3D11CaptureFrame captured = null;
            bool closed = false;

            try
            {
                var item = target.Kind == CaptureTargetKind.Window
                    ? CaptureItemFactory.CreateForWindow(target.Handle)
                    : CaptureItemFactory.CreateForMonitor(target.Handle);
                if (item == null) { error = L.T("无法捕获该目标（窗口可能已关闭）。"); return null; }

                device = Direct3DHelper.CreateDevice(out nativeDevice, out nativeContext);
                if (device == null) { error = L.T("显卡设备初始化失败。"); return null; }

                var size = CaptureHelpers.EvenSize(item.Size.Width, item.Size.Height);
                if (!CaptureHelpers.IsUsableSize(size)) { error = L.T("目标尺寸无效。"); return null; }

                pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    device, DirectXPixelFormat.B8G8R8A8UIntNormalized, FramePoolBuffers, size);

                var got = new ManualResetEventSlim(false);
                pool.FrameArrived += delegate(Direct3D11CaptureFramePool sender, object args)
                {
                    Direct3D11CaptureFrame f;
                    try { f = sender.TryGetNextFrame(); }
                    catch (Exception ex)
                    {
                        Log.Debug("截图取帧失败: " + ex.Message);
                        return;
                    }
                    if (f == null) return;

                    bool keep = false;
                    lock (gate)
                    {
                        if (!closed && captured == null) { captured = f; keep = true; }
                    }
                    if (keep) got.Set();
                    else CaptureHelpers.SafeDispose(f, "归还多余的截图帧");
                };

                session = pool.CreateCaptureSession(item);
                CaptureHelpers.ConfigureSession(session, captureCursor);
                session.StartCapture();

                if (!got.Wait(FirstFrameTimeout))
                {
                    error = L.T("等待画面超时，请稍后重试。");
                    return null;
                }

                Direct3D11CaptureFrame frame;
                lock (gate) frame = captured;

                string path = SaveSurfaceAsPng(frame.Surface, dir, fileName, exactPath);
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
                Direct3D11CaptureFrame leftover;
                lock (gate)
                {
                    closed = true;
                    leftover = captured;
                    captured = null;
                }
                CaptureHelpers.SafeDispose(leftover, "释放截图帧");
                CaptureHelpers.SafeDispose(session, "停止截图会话");
                CaptureHelpers.SafeDispose(pool, "释放截图帧池");
                CaptureHelpers.ReleaseNative(ref nativeContext, " D3D11 上下文");
                CaptureHelpers.ReleaseNative(ref nativeDevice, " D3D11 设备");
            }
        }

        private static bool ResolveOutput(CaptureTarget target, string outputFolder, string targetPath,
                                          out string dir, out string fileName, out bool exactPath, out string error)
        {
            dir = null;
            fileName = null;
            exactPath = false;
            error = null;
            string baseDir = string.IsNullOrEmpty(outputFolder) ? RecordingOptions.DefaultFolder : outputFolder;
            try
            {
                string p = targetPath == null ? "" : targetPath.Trim();
                if (p.Length == 0)
                {
                    dir = Path.GetFullPath(baseDir);
                    fileName = DefaultFileName(target);
                }
                else
                {
                    if (!Path.IsPathRooted(p)) p = Path.Combine(Path.GetFullPath(baseDir), p);
                    bool isDir = p.EndsWith("\\", StringComparison.Ordinal) ||
                                 p.EndsWith("/", StringComparison.Ordinal) || Directory.Exists(p);
                    p = Path.GetFullPath(p);
                    if (isDir)
                    {
                        dir = p;
                        fileName = DefaultFileName(target);
                    }
                    else
                    {
                        if (!string.Equals(Path.GetExtension(p), ".png", StringComparison.OrdinalIgnoreCase))
                            p += ".png";
                        dir = Path.GetDirectoryName(p);
                        fileName = Path.GetFileName(p);
                        exactPath = true;
                    }
                }
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(fileName))
                    throw new ArgumentException(targetPath);

                dir = Path.GetDirectoryName(Path.Combine(dir, fileName));
                Directory.CreateDirectory(dir);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("截图保存路径无效: " + targetPath + " - " + ex.Message);
                error = L.T("截图保存路径无效：") + ex.Message;
                return false;
            }
        }

        private static string DefaultFileName(CaptureTarget target)
        {
            string safe = CaptureHelpers.SafeFileStem(target.Title, L.T("未命名截图"));
            return string.Format("{0}_{1:yyyyMMdd_HHmmss}.png", safe, DateTime.Now);
        }

        private static string SaveSurfaceAsPng(IDirect3DSurface surface, string dir, string fileName, bool exactPath)
        {
            using (var bitmap = Await(SoftwareBitmap.CreateCopyFromSurfaceAsync(surface), BitmapCopyTimeout, "复制位图"))
            {
                var folder = Await(StorageFolder.GetFolderFromPathAsync(dir), FileOpTimeout, "打开目录");

                string name = exactPath
                    ? fileName + ".tmp"
                    : Path.GetFileName(CaptureHelpers.UniquePath(Path.Combine(dir, fileName)));
                var file = Await(folder.CreateFileAsync(name, CreationCollisionOption.GenerateUniqueName),
                    FileOpTimeout, "创建文件");
                string path = file.Path;

                bool ok = false;
                try
                {
                    using (IRandomAccessStream stream = Await(file.OpenAsync(FileAccessMode.ReadWrite), FileOpTimeout, "打开文件"))
                    {
                        var encoder = Await(BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream),
                            FileOpTimeout, "创建 PNG 编码器");

                        // 捕获帧带 alpha 通道但内容不透明；转成 Ignore 避免某些看图程序显示异常
                        using (var converted = SoftwareBitmap.Convert(
                            bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore))
                        {
                            encoder.SetSoftwareBitmap(converted);
                            Await(encoder.FlushAsync(), EncodeTimeout, "编码 PNG");
                        }
                    }
                    string result = exactPath ? MoveIntoPlace(path, Path.Combine(dir, fileName)) : path;
                    ok = true;
                    return result;
                }
                finally
                {
                    if (!ok)
                    {
                        try { File.Delete(path); }
                        catch (Exception ex) { Log.Debug("删除残缺截图失败: " + ex.Message); }
                    }
                }
            }
        }

        private const int ReplaceRetries = 8;
        private const int ReplaceRetryDelayMs = 125;

        private static string MoveIntoPlace(string tmp, string final)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(final)) File.Replace(tmp, final, null);
                    else File.Move(tmp, final);
                    return final;
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException) && !(ex is UnauthorizedAccessException)) throw;
                    if (attempt >= ReplaceRetries)
                    {
                        throw new IOException(string.Format(
                            L.T("无法覆盖截图文件 {0}（可能正被其它程序打开，或是只读文件）："), final) + ex.Message, ex);
                    }
                    Thread.Sleep(ReplaceRetryDelayMs);
                }
            }
        }

        private static T Await<T>(IAsyncOperation<T> op, TimeSpan timeout, string step)
        {
            var task = op.AsTask();
            WaitOrThrow(task, timeout, step);
            return task.GetAwaiter().GetResult();
        }

        private static void Await(IAsyncAction op, TimeSpan timeout, string step)
        {
            var task = op.AsTask();
            WaitOrThrow(task, timeout, step);
            task.GetAwaiter().GetResult();
        }

        private static void WaitOrThrow(Task task, TimeSpan timeout, string step)
        {
            bool done;
            try { done = task.Wait(timeout); }
            catch (AggregateException) { done = true; }
            if (done) return;
            Log.Warn("截图步骤超时: " + step + "（" + timeout.TotalSeconds + " 秒）");
            throw new TimeoutException(L.T("保存截图超时，请稍后重试。"));
        }
    }
}
