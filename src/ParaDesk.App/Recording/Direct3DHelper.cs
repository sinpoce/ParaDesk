using System;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using ParaDesk.Core;

namespace ParaDesk.Recording
{
    /// <summary>
    /// 创建 WinRT 捕获所需的 D3D11 设备。
    /// Windows.Graphics.Capture 的帧池要一个 IDirect3DDevice，而它只能由
    /// 原生 D3D11 设备转换而来，所以这层互操作绕不开。
    /// </summary>
    internal static class Direct3DHelper
    {
        [ComImport]
        [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDirect3DDxgiInterfaceAccess
        {
            IntPtr GetInterface([In] ref Guid iid);
        }

        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
            SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true,
            PreserveSig = false)]
        private static extern uint CreateDirect3D11DeviceFromDXGIDevice(
            IntPtr dxgiDevice, out IntPtr graphicsDevice);

        [DllImport("d3d11.dll", SetLastError = true, ExactSpelling = true)]
        private static extern int D3D11CreateDevice(
            IntPtr adapter, int driverType, IntPtr software, uint flags,
            IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
            out IntPtr device, out int featureLevel, out IntPtr immediateContext);

        private const int D3D_DRIVER_TYPE_HARDWARE = 1;
        private const int D3D_DRIVER_TYPE_WARP = 5;
        private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
        private const uint D3D11_SDK_VERSION = 7;

        /// <summary>创建可供 WinRT 捕获使用的 D3D11 设备；失败返回 null。</summary>
        public static IDirect3DDevice CreateDevice(out IntPtr nativeDevice, out IntPtr nativeContext)
        {
            nativeDevice = IntPtr.Zero;
            nativeContext = IntPtr.Zero;

            // BGRA 支持是必需的：捕获帧是 B8G8R8A8 格式
            int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION,
                out nativeDevice, out _, out nativeContext);

            if (hr < 0)
            {
                // 无独显或驱动异常时回退到 WARP 软件渲染，录制仍可用只是更吃 CPU
                Log.Warn("硬件 D3D11 设备创建失败 (0x" + hr.ToString("X8") + ")，回退 WARP");
                hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero,
                    D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION,
                    out nativeDevice, out _, out nativeContext);
                if (hr < 0)
                {
                    Log.Error("D3D11 设备创建失败 0x" + hr.ToString("X8"));
                    return null;
                }
            }

            IntPtr dxgiDevice = IntPtr.Zero;
            IntPtr inspectable = IntPtr.Zero;
            try
            {
                Guid iidDxgiDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"); // IDXGIDevice
                int qi = Marshal.QueryInterface(nativeDevice, ref iidDxgiDevice, out dxgiDevice);
                if (qi < 0)
                {
                    Log.Error("QueryInterface(IDXGIDevice) 失败 0x" + qi.ToString("X8"));
                    ReleaseOut(ref nativeDevice, ref nativeContext);
                    return null;
                }

                CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out inspectable);
                var device = Marshal.GetObjectForIUnknown(inspectable) as IDirect3DDevice;
                if (device == null)
                {
                    Log.Error("转换 IDirect3DDevice 失败");
                    ReleaseOut(ref nativeDevice, ref nativeContext);
                }
                return device;
            }
            catch (Exception ex)
            {
                Log.Error("创建 WinRT D3D 设备失败", ex);
                // 失败时必须自己收回已经通过 out 参数交出去的原生指针，
                // 否则调用方拿到 null 就直接返回，这两个 COM 对象永久泄漏。
                ReleaseOut(ref nativeDevice, ref nativeContext);
                return null;
            }
            finally
            {
                if (dxgiDevice != IntPtr.Zero) Marshal.Release(dxgiDevice);
                if (inspectable != IntPtr.Zero) Marshal.Release(inspectable);
            }
        }

        private static void ReleaseOut(ref IntPtr device, ref IntPtr context)
        {
            try { if (context != IntPtr.Zero) { Marshal.Release(context); context = IntPtr.Zero; } }
            catch { }
            try { if (device != IntPtr.Zero) { Marshal.Release(device); device = IntPtr.Zero; } }
            catch { }
        }
    }
}
