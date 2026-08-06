using System;
using System.IO;
using System.Threading;
using ParaDesk.Core;
using ParaDesk.Recording;

namespace ParaDesk.Diagnostics
{
    /// <summary>
    /// 录制自检（--rectest [秒数]）：录主显示器指定秒数，然后读回文件真实时长做比对。
    /// 录制的时间轴对不对不能靠肉眼看，必须量。
    /// </summary>
    internal static class RecordTest
    {
        /// <summary>程序是窗口子系统，控制台输出看不见，一律写日志。</summary>
        private static void Say(string msg)
        {
            Console.WriteLine(msg);
            Log.Info("[rectest] " + msg);
        }

        public static int Run(string[] args)
        {
            int seconds = 10;
            if (args.Length > 1) int.TryParse(args[1], out seconds);
            if (seconds < 3) seconds = 3;
            if (seconds > 120) seconds = 120;

            if (!CaptureItemFactory.IsSupported)
            {
                Say("当前系统不支持屏幕捕获");
                return 2;
            }

            var targets = CaptureTargetEnumerator.Enumerate(null);
            CaptureTarget target = null;
            foreach (var t in targets)
            {
                if (t.Kind != CaptureTargetKind.Monitor) continue;
                target = t;
                break;
            }
            if (target == null) { Console.WriteLine("找不到可录制的显示器"); return 2; }

            var options = RecordingOptions.CreateDefault();
            options.OutputFolder = Path.Combine(Path.GetTempPath(), "ParaDeskRecTest");
            options.FrameRate = 30;
            options.BitrateMbps = 8;
            options.Audio = AudioSource.System;   // 验证音轨是否真的写进了文件

            var recorder = new ScreenRecorder();
            string finishedPath = null;
            string error = null;
            var done = new ManualResetEventSlim(false);

            recorder.Stopped += delegate(object s, RecordingStoppedEventArgs e)
            {
                finishedPath = e.FilePath;
                error = e.Error;
                done.Set();
            };

            Say("目标: " + target.Title);
            Say("计划录制 " + seconds + " 秒 ...");

            var wall = System.Diagnostics.Stopwatch.StartNew();
            string err = recorder.Start(target, options);
            if (err != null) { Console.WriteLine("启动失败: " + err); return 1; }

            // 期间制造一段静止：静止时 WGC 不送帧，正是之前误判流结束的场景
            Thread.Sleep(seconds * 1000);

            recorder.Stop();
            if (!done.Wait(TimeSpan.FromSeconds(30)))
            {
                Say("停止超时，编码未在 30 秒内收尾");
                return 1;
            }
            wall.Stop();

            if (error != null) { Console.WriteLine("录制报错: " + error); return 1; }
            if (string.IsNullOrEmpty(finishedPath) || !File.Exists(finishedPath))
            {
                Say("未生成文件");
                return 1;
            }

            Say("带音频    : " + (recorder.HasAudio ? "是（系统声音）" : "否" +
                (recorder.AudioWarning != null ? " —— " + recorder.AudioWarning : "")));

            var fi = new FileInfo(finishedPath);
            double actual = ReadDurationSeconds(finishedPath);
            Say("音轨检测  : " + (HasAudioTrack(finishedPath) ? "文件中存在音频轨" : "未检出音频轨"));

            Say("");
            Say("文件      : " + finishedPath);
            Say("大小      : " + (fi.Length / 1024.0 / 1024.0).ToString("N2") + " MB");
            Say("墙钟耗时  : " + wall.Elapsed.TotalSeconds.ToString("N1") + " s");
            Say("视频时长  : " + (actual > 0 ? actual.ToString("N1") + " s" : "读取失败"));

            if (actual <= 0)
            {
                Say("结果      : 无法读取时长，请手动播放确认");
                return 1;
            }

            double ratio = actual / seconds;
            Say("时长比    : " + ratio.ToString("N2") + "（1.00 为正确）");

            bool ok = ratio > 0.85 && ratio < 1.15;
            Say("结果      : " + (ok ? "通过 —— 时间轴与真实时间一致" :
                (ratio < 1 ? "偏短 —— 录制被提前结束或丢帧" : "偏长 —— 时间戳推进过快")));

            Log.Info("录制自检: 计划 " + seconds + "s, 实际 " + actual.ToString("N1") +
                     "s, 比值 " + ratio.ToString("N2") + ", " + (ok ? "通过" : "未通过"));
            return ok ? 0 : 1;
        }

        /// <summary>
        /// 检测 MP4 里是否真的有音频轨。
        /// 不能只看"我启用了音频"——必须验证音轨确实写进了文件。
        /// 做法是在 moov 里找 'soun' 处理器类型标记。
        /// </summary>
        private static bool HasAudioTrack(string path)
        {
            try
            {
                byte[] needle = { (byte)'s', (byte)'o', (byte)'u', (byte)'n' };
                using (var fs = File.OpenRead(path))
                {
                    // 音轨元数据在 moov 里，通常靠近文件头或尾，扫前后各 2MB 足够
                    long len = fs.Length;
                    if (ScanFor(fs, 0, (int)Math.Min(2 * 1024 * 1024, len), needle)) return true;
                    if (len > 2 * 1024 * 1024)
                        return ScanFor(fs, len - 2 * 1024 * 1024, 2 * 1024 * 1024, needle);
                }
            }
            catch (Exception ex) { Log.Debug("检测音轨失败: " + ex.Message); }
            return false;
        }

        private static bool ScanFor(FileStream fs, long start, int count, byte[] needle)
        {
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[count];
            int read = fs.Read(buf, 0, count);
            for (int i = 0; i + needle.Length <= read; i++)
            {
                bool hit = true;
                for (int j = 0; j < needle.Length; j++)
                    if (buf[i + j] != needle[j]) { hit = false; break; }
                if (hit) return true;
            }
            return false;
        }

        /// <summary>用 WinRT 的视频属性读真实时长（我们已经引了 SDK Contracts）。</summary>
        private static double ReadDurationSeconds(string path)
        {
            try
            {
                var task = Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask();
                task.Wait(TimeSpan.FromSeconds(10));
                var file = task.Result;

                var propsTask = file.Properties.GetVideoPropertiesAsync().AsTask();
                propsTask.Wait(TimeSpan.FromSeconds(10));
                return propsTask.Result.Duration.TotalSeconds;
            }
            catch (Exception ex)
            {
                Log.Debug("读取视频时长失败: " + ex.Message);
                return -1;
            }
        }
    }
}
