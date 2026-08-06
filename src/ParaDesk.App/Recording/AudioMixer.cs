using System;
using System.Collections.Generic;
using ParaDesk.Core;

namespace ParaDesk.Recording
{
    /// <summary>
    /// 音频汇流：把系统声音与麦克风两路统一成一条连续的 PCM 流。
    ///
    /// 两个关键点：
    /// 1) 环回在无人播放声音时**不产生数据**，麦克风静音时也可能稀疏。
    ///    音轨一旦出现空洞，播放器就会把后面的声音提前，导致音画不同步。
    ///    所以这里按时间轴取数据，缺多少就补多少静音。
    /// 2) 两路都由音频引擎转成 48kHz/16bit/立体声，混音只需按样本相加，
    ///    不必自己做重采样。
    /// </summary>
    internal class AudioMixer : IDisposable
    {
        private readonly object _sync = new object();
        private WasapiCapture _system;
        private WasapiCapture _mic;

        // 每一路各自的待取队列
        private readonly Queue<byte[]> _systemQueue = new Queue<byte[]>();
        private readonly Queue<byte[]> _micQueue = new Queue<byte[]>();
        private int _systemOffset, _micOffset;
        private int _systemBytes, _micBytes;

        private bool _useSystem, _useMic;

        /// <summary>已交付的总字节数，用于推算时间戳。</summary>
        private long _deliveredBytes;

        public bool Enabled { get { return _useSystem || _useMic; } }

        public static int SampleRate { get { return WasapiCapture.SampleRate; } }
        public static int Channels { get { return WasapiCapture.Channels; } }
        public static int BitsPerSample { get { return WasapiCapture.BitsPerSample; } }
        public static int BytesPerSecond { get { return SampleRate * WasapiCapture.BytesPerFrame; } }

        /// <summary>启动所需的采集路。返回 null 表示成功（部分失败会降级并记录）。</summary>
        public string Start(AudioSource source)
        {
            _useSystem = source == AudioSource.System || source == AudioSource.Both;
            _useMic = source == AudioSource.Microphone || source == AudioSource.Both;
            if (!Enabled) return null;

            string firstError = null;

            if (_useSystem)
            {
                _system = new WasapiCapture(true);
                _system.DataAvailable += OnSystemData;
                string err = _system.Start();
                if (err != null)
                {
                    Log.Warn("系统声音采集不可用：" + err);
                    firstError = err;
                    _system.Dispose(); _system = null; _useSystem = false;
                }
            }

            if (_useMic)
            {
                _mic = new WasapiCapture(false);
                _mic.DataAvailable += OnMicData;
                string err = _mic.Start();
                if (err != null)
                {
                    Log.Warn("麦克风采集不可用：" + err);
                    if (firstError == null) firstError = err;
                    _mic.Dispose(); _mic = null; _useMic = false;
                }
            }

            // 两路都失败才算失败；只挂掉一路就降级继续录另一路
            if (!Enabled) return firstError ?? "音频设备不可用。";
            return null;
        }

        private void OnSystemData(byte[] data, int count)
        {
            lock (_sync)
            {
                _systemQueue.Enqueue(data);
                _systemBytes += count;
                TrimIfBacklogged(_systemQueue, ref _systemBytes, ref _systemOffset);
            }
        }

        private void OnMicData(byte[] data, int count)
        {
            lock (_sync)
            {
                _micQueue.Enqueue(data);
                _micBytes += count;
                TrimIfBacklogged(_micQueue, ref _micBytes, ref _micOffset);
            }
        }

        /// <summary>
        /// 积压超过 3 秒就丢弃最旧的数据。供样端若因故落后，
        /// 宁可丢掉一段旧声音，也不能让内存无限增长。
        /// </summary>
        private static void TrimIfBacklogged(Queue<byte[]> q, ref int total, ref int offset)
        {
            int limit = BytesPerSecond * 3;
            while (total > limit && q.Count > 0)
            {
                var head = q.Dequeue();
                total -= head.Length - offset;
                offset = 0;
            }
        }

        /// <summary>
        /// 取出指定时长的 PCM 数据。数据不足的部分补静音，
        /// 保证音轨在时间上永远连续。
        /// </summary>
        public byte[] Read(TimeSpan duration, out TimeSpan timestamp)
        {
            int bytes = (int)(duration.TotalSeconds * BytesPerSecond);
            bytes -= bytes % WasapiCapture.BytesPerFrame;   // 对齐到整帧
            if (bytes <= 0) bytes = WasapiCapture.BytesPerFrame;

            var result = new byte[bytes];

            lock (_sync)
            {
                timestamp = TimeSpan.FromSeconds((double)_deliveredBytes / BytesPerSecond);

                if (_useSystem)
                    Drain(_systemQueue, ref _systemOffset, ref _systemBytes, result, bytes, false);
                if (_useMic)
                    Drain(_micQueue, ref _micOffset, ref _micBytes, result, bytes, _useSystem);

                _deliveredBytes += bytes;
            }
            return result;
        }

        /// <summary>把队列里的数据搬进目标缓冲；mix=true 时与已有内容相加而非覆盖。</summary>
        private static void Drain(Queue<byte[]> q, ref int offset, ref int total,
                                  byte[] dest, int need, bool mix)
        {
            int written = 0;
            while (written < need && q.Count > 0)
            {
                var head = q.Peek();
                int avail = head.Length - offset;
                int take = Math.Min(avail, need - written);

                if (mix) MixInto(dest, written, head, offset, take);
                else Buffer.BlockCopy(head, offset, dest, written, take);

                written += take;
                offset += take;
                total -= take;

                if (offset >= head.Length) { q.Dequeue(); offset = 0; }
            }
            // 不足的部分保持为 0（静音），这正是保证时间轴连续的关键
        }

        /// <summary>16 位 PCM 相加混音，带饱和截断防止溢出爆音。</summary>
        private static void MixInto(byte[] dest, int destOffset, byte[] src, int srcOffset, int count)
        {
            for (int i = 0; i + 1 < count; i += 2)
            {
                int di = destOffset + i, si = srcOffset + i;
                short a = (short)(dest[di] | (dest[di + 1] << 8));
                short b = (short)(src[si] | (src[si + 1] << 8));

                int sum = a + b;
                if (sum > short.MaxValue) sum = short.MaxValue;
                else if (sum < short.MinValue) sum = short.MinValue;

                dest[di] = (byte)(sum & 0xFF);
                dest[di + 1] = (byte)((sum >> 8) & 0xFF);
            }
        }

        /// <summary>
        /// 丢弃已经攒下的音频并把时间轴归零。
        ///
        /// 必须在视频时钟起跑的那一刻调用：音频采集要早于编码管线建立才能不丢开头，
        /// 但那段"预卷"数据若留着，整条音轨就会比画面恒定超前一段——
        /// 表现为全程音画不同步，而且时长检查完全看不出来。
        /// </summary>
        public void ResetTimeline()
        {
            lock (_sync)
            {
                _systemQueue.Clear(); _micQueue.Clear();
                _systemBytes = _micBytes = 0;
                _systemOffset = _micOffset = 0;
                _deliveredBytes = 0;
            }
        }

        /// <summary>任一路中途失败的原因；null 表示正常。</summary>
        public string FailureReason
        {
            get
            {
                if (_system != null && _system.FailureReason != null) return _system.FailureReason;
                if (_mic != null && _mic.FailureReason != null) return _mic.FailureReason;
                return null;
            }
        }

        public void Stop()
        {
            if (_system != null) _system.Stop();
            if (_mic != null) _mic.Stop();
        }

        public void Dispose()
        {
            if (_system != null) { _system.Dispose(); _system = null; }
            if (_mic != null) { _mic.Dispose(); _mic = null; }
            lock (_sync)
            {
                _systemQueue.Clear(); _micQueue.Clear();
                _systemBytes = _micBytes = 0;
                _systemOffset = _micOffset = 0;
            }
        }
    }
}
