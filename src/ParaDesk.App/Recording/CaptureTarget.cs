using System;
using System.Runtime.InteropServices;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using ParaDesk.Core;

namespace ParaDesk.Recording
{
    /// <summary>录制目标的种类。</summary>
    internal enum CaptureTargetKind
    {
        Window,
        Monitor,
    }

    /// <summary>一个可录制的目标（某个分身桌面窗口，或某块显示器）。</summary>
    internal class CaptureTarget
    {
        public CaptureTargetKind Kind;
        public string Title;
        public IntPtr Handle;      // HWND 或 HMONITOR
        public string DeviceName;  // 显示器时为 \\.\DISPLAY1

        public override string ToString() { return Title; }
    }

    /// <summary>
    /// 创建 GraphicsCaptureItem。
    ///
    /// 为什么必须用 Windows.Graphics.Capture 而不是 BitBlt / 桌面复制：
    /// 分身桌面的画面由 mstscax 以 DirectX 硬件合成方式绘制，BitBlt/gdigrab
    /// 对这类内容只会录出黑屏；而桌面复制 API 只能整屏、且够不到另一个会话。
    /// WGC 读的是 DWM 重定向表面，既能拿到硬件合成内容，窗口被遮挡也照录不误。
    /// </summary>
    internal static class CaptureItemFactory
    {
        // 必须写全名：Windows.Foundation.Metadata 也有一个 GuidAttribute，会撞名
        [ComImport]
        [System.Runtime.InteropServices.Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
            IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
        }

        // GraphicsCaptureItem 的 IID，作为 riid 传给上面两个方法
        private static readonly Guid GraphicsCaptureItemIid =
            new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");

        /// <summary>当前系统是否支持屏幕捕获（需 Win10 1903+）。</summary>
        public static bool IsSupported
        {
            get
            {
                try { return GraphicsCaptureSession.IsSupported(); }
                catch (Exception ex)
                {
                    Log.Warn("检测屏幕捕获支持失败: " + ex.Message);
                    return false;
                }
            }
        }

        /// <summary>Win11 才能隐藏系统绘制的黄色捕获边框。</summary>
        public static bool CanHideBorder
        {
            get
            {
                try
                {
                    return ApiInformation.IsPropertyPresent(
                        "Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired");
                }
                catch { return false; }
            }
        }

        public static GraphicsCaptureItem CreateForWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            return Create(delegate(IGraphicsCaptureItemInterop interop)
            {
                Guid iid = GraphicsCaptureItemIid;
                return interop.CreateForWindow(hwnd, ref iid);
            }, L.T("窗口"));
        }

        public static GraphicsCaptureItem CreateForMonitor(IntPtr hmonitor)
        {
            if (hmonitor == IntPtr.Zero) return null;
            return Create(delegate(IGraphicsCaptureItemInterop interop)
            {
                Guid iid = GraphicsCaptureItemIid;
                return interop.CreateForMonitor(hmonitor, ref iid);
            }, L.T("显示器"));
        }

        private static GraphicsCaptureItem Create(Func<IGraphicsCaptureItemInterop, IntPtr> make, string what)
        {
            IntPtr raw = IntPtr.Zero;
            try
            {
                // .NET Framework 的 CLR 自带 WinRT 投影，可以直接拿激活工厂；
                // 这也是 net48 反而比 .NET 8+ 更容易做 WGC 的原因（后者要 CsWinRT）。
                var factory = (IGraphicsCaptureItemInterop)System.Runtime.InteropServices
                    .WindowsRuntime.WindowsRuntimeMarshal
                    .GetActivationFactory(typeof(GraphicsCaptureItem));

                raw = make(factory);
                if (raw == IntPtr.Zero) return null;

                return (GraphicsCaptureItem)Marshal.GetObjectForIUnknown(raw);
            }
            catch (Exception ex)
            {
                Log.Error("创建" + what + "捕获项失败", ex);
                return null;
            }
            finally
            {
                if (raw != IntPtr.Zero) Marshal.Release(raw);
            }
        }
    }
}
