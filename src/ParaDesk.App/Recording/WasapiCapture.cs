using System;
using System.Runtime.InteropServices;
using System.Threading;
using ParaDesk.Core;

namespace ParaDesk.Recording
{
    /// <summary>
    /// WASAPI 音频采集：系统播放声音（环回）或麦克风。
    ///
    /// 全部手写 COM 互操作而不引第三方库：NAudio 2.x 虽然可用，但它没有按进程环回
    /// （那是 NAudio 3 预览版才有、且明确放弃了 .NET Framework），将来要做
    /// "只录分身桌面的声音"仍得自己写，索性一次写全，也维持零额外依赖。
    /// </summary>
    internal class WasapiCapture : IDisposable
    {
        // ---------------- COM 声明 ----------------

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorRcw { }

        // 每个方法都必须标 [PreserveSig]。否则 CLR 会把 int 返回值当成 HRESULT
        // 由它自己检查，并在参数表末尾追加一个幻影 [out,retval] 参数——
        // 调用约定与真实的 COM 虚表就对不上了。

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration,
                long periodicity, IntPtr format, IntPtr audioSessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags,
                out long devicePosition, out long qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        private const int eRender = 0, eCapture = 1, eConsole = 0;
        private const int CLSCTX_ALL = 23;
        private const int AUDCLNT_SHAREMODE_SHARED = 0;
        private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const int AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = unchecked((int)0x80000000);
        private const int AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
        private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        private const ushort WAVE_FORMAT_PCM = 1;

        // ---------------- 状态 ----------------

        private IAudioClient _client;
        private IAudioCaptureClient _capture;
        private Thread _thread;
        private volatile bool _running;
        private readonly bool _loopback;

        /// <summary>统一输出格式：48kHz / 16bit / 立体声。</summary>
        public const int SampleRate = 48000;
        public const int Channels = 2;
        public const int BitsPerSample = 16;
        public static int BytesPerFrame { get { return Channels * BitsPerSample / 8; } }

        /// <summary>采到的 PCM 数据（已是上述统一格式）。</summary>
        public event Action<byte[], int> DataAvailable;

        public WasapiCapture(bool loopback) { _loopback = loopback; }

        /// <summary>启动采集。返回 null 表示成功。</summary>
        public string Start()
        {
            IntPtr formatPtr = IntPtr.Zero;
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorRcw();
                IMMDevice device;
                int hr = enumerator.GetDefaultAudioEndpoint(
                    _loopback ? eRender : eCapture, eConsole, out device);
                if (hr != 0 || device == null)
                    return _loopback ? L.T("找不到默认播放设备。") : L.T("找不到麦克风设备。");

                Guid iidClient = typeof(IAudioClient).GUID;
                object obj;
                hr = device.Activate(ref iidClient, CLSCTX_ALL, IntPtr.Zero, out obj);
                if (hr != 0 || obj == null) return string.Format(L.T("无法打开音频设备（0x{0}）。"), hr.ToString("X8"));
                _client = (IAudioClient)obj;

                // 直接向音频引擎要 48k/16bit/立体声，由它负责重采样与格式转换。
                // 这样系统声音和麦克风拿到的是同一种格式，混音时不必自己做重采样。
                formatPtr = BuildPcmFormat();
                int flags = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
                if (_loopback) flags |= AUDCLNT_STREAMFLAGS_LOOPBACK;

                // 缓冲 200ms，足以扛住供样端偶发的抖动
                hr = _client.Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 2000000, 0, formatPtr, IntPtr.Zero);
                if (hr != 0)
                {
                    Log.Warn((_loopback ? "环回" : "麦克风") + "初始化失败 0x" + hr.ToString("X8"));
                    return hr == unchecked((int)0x80070005)
                        ? L.T("系统拒绝访问麦克风，请在隐私设置中允许桌面应用使用麦克风。")
                        : string.Format(L.T("音频设备不支持所需格式（0x{0}）。"), hr.ToString("X8"));
                }

                Guid iidCapture = typeof(IAudioCaptureClient).GUID;
                object cap;
                hr = _client.GetService(ref iidCapture, out cap);
                if (hr != 0 || cap == null) return L.T("无法获取音频采集接口。");
                _capture = (IAudioCaptureClient)cap;

                hr = _client.Start();
                if (hr != 0)
                {
                    Log.Warn("音频流启动失败 0x" + hr.ToString("X8"));
                    return string.Format(L.T("音频流无法启动（0x{0}）。"), hr.ToString("X8"));
                }

                _running = true;
                _thread = new Thread(CaptureLoop);
                _thread.IsBackground = true;
                _thread.Name = _loopback ? "ParaDesk-Loopback" : "ParaDesk-Mic";
                _thread.Start();

                Log.Info((_loopback ? "系统声音" : "麦克风") + "采集已启动");
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("启动音频采集失败", ex);
                return L.T("启动音频采集失败：") + ex.Message;
            }
            finally
            {
                if (formatPtr != IntPtr.Zero) Marshal.FreeHGlobal(formatPtr);
            }
        }

        private static IntPtr BuildPcmFormat()
        {
            var fmt = new WAVEFORMATEX
            {
                wFormatTag = WAVE_FORMAT_PCM,
                nChannels = Channels,
                nSamplesPerSec = SampleRate,
                wBitsPerSample = BitsPerSample,
                nBlockAlign = (ushort)(Channels * BitsPerSample / 8),
                cbSize = 0,
            };
            fmt.nAvgBytesPerSec = fmt.nSamplesPerSec * fmt.nBlockAlign;

            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WAVEFORMATEX)));
            Marshal.StructureToPtr(fmt, p, false);
            return p;
        }

        /// <summary>采集中途失败的原因；null 表示一切正常。供录制结束时如实汇报。</summary>
        public string FailureReason { get; private set; }

        private void CaptureLoop()
        {
            try
            {
                while (_running)
                {
                    uint packet;
                    int hr = _capture.GetNextPacketSize(out packet);
                    if (hr != 0) { Fail("GetNextPacketSize", hr); return; }

                    if (packet == 0) { Thread.Sleep(5); continue; }

                    while (packet > 0 && _running)
                    {
                        IntPtr data;
                        uint frames, flags;
                        long devPos, qpc;
                        hr = _capture.GetBuffer(out data, out frames, out flags, out devPos, out qpc);
                        if (hr != 0) { Fail("GetBuffer", hr); return; }

                        int bytes = (int)frames * BytesPerFrame;
                        if (bytes > 0)
                        {
                            var buffer = new byte[bytes];
                            // 静音包不携带有效数据，按零填充即可
                            if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && data != IntPtr.Zero)
                                Marshal.Copy(data, buffer, 0, bytes);

                            var h = DataAvailable;
                            if (h != null) h(buffer, bytes);
                        }

                        _capture.ReleaseBuffer(frames);
                        hr = _capture.GetNextPacketSize(out packet);
                        if (hr != 0) { Fail("GetNextPacketSize", hr); return; }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("音频采集循环异常", ex);
                FailureReason = string.Format(L.T("{0}采集中断："), _loopback ? L.T("系统声音") : L.T("麦克风")) + ex.Message;
                _running = false;
            }
        }

        /// <summary>
        /// 采集中途失败。以前这里是静默 break，结果录制方一直以为音频还在采，
        /// 用户录完才发现后半段没声音且毫无提示。
        /// 设备被拔掉或默认设备切换时（AUDCLNT_E_DEVICE_INVALIDATED）就会走到这里。
        /// </summary>
        private void Fail(string where, int hr)
        {
            _running = false;
            string what = _loopback ? L.T("系统声音") : L.T("麦克风");
            string reason;
            switch (unchecked((uint)hr))
            {
                case 0x88890004: reason = string.Format(L.T("{0}设备已失效（可能被拔出或切换了默认设备）"), what); break;
                case 0x88890001: reason = string.Format(L.T("{0}设备未初始化"), what); break;
                case 0x88890008: reason = string.Format(L.T("{0}设备已被独占占用"), what); break;
                default: reason = string.Format(L.T("{0}采集失败（{1} 0x{2}）"), what, where, hr.ToString("X8")); break;
            }
            FailureReason = reason;
            Log.Warn("音频采集中断: " + reason);
        }

        public void Stop()
        {
            _running = false;
            try { if (_thread != null && _thread.IsAlive) _thread.Join(500); }
            catch { }
            _thread = null;

            try { if (_client != null) _client.Stop(); } catch { }
        }

        public void Dispose()
        {
            Stop();
            try { if (_capture != null) { Marshal.ReleaseComObject(_capture); _capture = null; } } catch { }
            try { if (_client != null) { Marshal.ReleaseComObject(_client); _client = null; } } catch { }
        }
    }
}
