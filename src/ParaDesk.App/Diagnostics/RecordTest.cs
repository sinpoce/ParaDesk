using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ParaDesk.Core;
using ParaDesk.Recording;

namespace ParaDesk.Diagnostics
{
    internal static class RecordTest
    {
        private const int DefaultSeconds = 10;
        private const int MinSeconds = 3;
        private const int MaxSeconds = 120;

        private const int StopTimeoutSeconds = 30;

        private const int WinRtTimeoutSeconds = 10;

        private const double MinRatio = 0.85;
        private const double MaxRatio = 1.15;

        private const int ScanWindowBytes = 2 * 1024 * 1024;

        private static string TempFolder
        {
            get { return Path.Combine(Path.GetTempPath(), "ParaDeskRecTest"); }
        }

        private static void Say(string msg)
        {
            Console.WriteLine(msg);
            Log.Info("[rectest] " + msg);
        }

        public static int Run(string[] args)
        {
            int seconds;
            int monitorIndex;
            string argError = ParseArgs(args, out seconds, out monitorIndex);
            if (argError != null)
            {
                Say(argError);
                Say("用法: --rectest [秒数 " + MinSeconds + ".." + MaxSeconds + "] [--monitor N]");
                return ExitCodes.Usage;
            }

            if (!CaptureItemFactory.IsSupported)
            {
                Say("当前系统不支持屏幕捕获");
                return ExitCodes.NotReady;
            }

            string pickError;
            CaptureTarget target = PickTarget(monitorIndex, out pickError);
            if (target == null)
            {
                Say(pickError);
                return monitorIndex > 0 ? ExitCodes.Usage : ExitCodes.NotReady;
            }

            CleanTempFolder();

            var options = RecordingOptions.CreateDefault();
            options.OutputFolder = TempFolder;
            options.FrameRate = 30;
            options.BitrateMbps = 8;
            options.Audio = AudioSource.System;   // 验证音轨是否真的写进了文件
            options.SegmentMinutes = 0;

            string finishedPath = null;
            string error = null;
            bool hasAudio;
            string audioWarning;
            double wallSeconds;

            var done = new ManualResetEventSlim(false);
            using (var recorder = new ScreenRecorder())
            {
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
                if (err != null) { Say("启动失败: " + err); return ExitCodes.Failed; }

                // 期间制造一段静止：静止时 WGC 不送帧，正是之前误判流结束的场景
                Thread.Sleep(seconds * 1000);

                recorder.Stop();
                if (!done.Wait(TimeSpan.FromSeconds(StopTimeoutSeconds)))
                {
                    Say("停止超时，编码未在 " + StopTimeoutSeconds + " 秒内收尾");
                    return ExitCodes.Failed;
                }
                wall.Stop();
                wallSeconds = wall.Elapsed.TotalSeconds;

                hasAudio = recorder.HasAudio;
                audioWarning = recorder.AudioWarning;
            }

            if (error != null)
            {
                Say("录制报错: " + error);
                KeepForInspection(finishedPath);
                return ExitCodes.Failed;
            }
            if (string.IsNullOrEmpty(finishedPath) || !File.Exists(finishedPath))
            {
                Say("未生成文件");
                return ExitCodes.Failed;
            }

            Say("带音频    : " + (hasAudio ? "是（系统声音）" : "否" +
                (audioWarning != null ? " —— " + audioWarning : "")));

            var fi = new FileInfo(finishedPath);
            double actual = ReadDurationSeconds(finishedPath);
            string how;
            bool? track = HasAudioTrack(finishedPath, out how);
            Say("音轨检测  : " + (track == null ? "无法判断" : (track.Value ? "文件中存在音频轨" : "未检出音频轨")) +
                "（" + how + "）");

            Say("");
            Say("文件      : " + finishedPath);
            Say("大小      : " + (fi.Length / 1024.0 / 1024.0).ToString("N2") + " MB");
            Say("墙钟耗时  : " + wallSeconds.ToString("N1") + " s");
            Say("视频时长  : " + (actual > 0 ? actual.ToString("N1") + " s" : "读取失败"));

            if (actual <= 0)
            {
                Say("结果      : 无法读取时长，请手动播放确认");
                KeepForInspection(finishedPath);
                return ExitCodes.Failed;
            }

            double ratio = actual / seconds;
            Say("时长比    : " + ratio.ToString("N2") + "（1.00 为正确）");
            bool durationOk = ratio > MinRatio && ratio < MaxRatio;

            bool audioExpected = options.Audio != AudioSource.None && hasAudio;
            bool audioOk = !audioExpected || (track.HasValue && track.Value);

            bool ok = durationOk && audioOk;
            string verdict;
            if (ok) verdict = "通过 —— 时间轴与真实时间一致" + (audioExpected ? "，音轨已写入" : "");
            else if (!durationOk) verdict = ratio < 1 ? "偏短 —— 录制被提前结束或丢帧" : "偏长 —— 时间戳推进过快";
            else verdict = "音轨缺失 —— 录制器报告带声音，但文件里" + (track == null ? "无法确认有" : "没有") + "音频轨";
            Say("结果      : " + verdict);

            Log.Info("录制自检: 计划 " + seconds + "s, 实际 " + actual.ToString("N1") +
                     "s, 比值 " + ratio.ToString("N2") + ", 音轨 " + (track == null ? "未知" : track.Value.ToString()) +
                     ", " + (ok ? "通过" : "未通过"));

            if (ok) TryDelete(finishedPath);
            else KeepForInspection(finishedPath);
            return ok ? ExitCodes.Ok : ExitCodes.Failed;
        }

        private static string ParseArgs(string[] args, out int seconds, out int monitorIndex)
        {
            seconds = DefaultSeconds;
            monitorIndex = 0;
            bool gotSeconds = false;
            if (args == null) return null;

            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i] ?? "";
                if (string.Equals(a, "--monitor", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out monitorIndex) || monitorIndex < 1)
                        return "--monitor 后面要跟显示器序号（1 起，与界面上的「显示器 N」一致）";
                    i++;
                    continue;
                }
                if (a.StartsWith("--", StringComparison.Ordinal)) return "不认识的参数: " + a;

                int n;
                if (gotSeconds || !int.TryParse(a, out n)) return "无法识别的秒数: " + a;
                seconds = n;
                gotSeconds = true;
            }

            if (seconds < MinSeconds) seconds = MinSeconds;
            if (seconds > MaxSeconds) seconds = MaxSeconds;
            return null;
        }

        private static CaptureTarget PickTarget(int monitorIndex, out string error)
        {
            error = null;
            List<MonitorInfo> monitors = MonitorService.Enumerate();
            MonitorInfo want = null;
            foreach (var m in monitors)
            {
                if (monitorIndex > 0 ? m.Index == monitorIndex : m.IsPrimary) { want = m; break; }
            }
            if (want == null)
            {
                if (monitorIndex > 0)
                {
                    error = "找不到显示器 " + monitorIndex + "（共 " + monitors.Count + " 块）";
                    return null;
                }
                if (monitors.Count > 0) want = monitors[0];
            }
            if (want == null)
            {
                error = "找不到可录制的显示器";
                return null;
            }

            foreach (var t in CaptureTargetEnumerator.Enumerate(null))
            {
                if (t.Kind == CaptureTargetKind.Monitor &&
                    string.Equals(t.DeviceName, want.DeviceName, StringComparison.OrdinalIgnoreCase))
                    return t;
            }
            error = "无法为显示器 " + want.DeviceName + " 创建录制目标";
            return null;
        }

        private static void CleanTempFolder()
        {
            try
            {
                if (!Directory.Exists(TempFolder)) return;
                int n = 0;
                foreach (string f in Directory.GetFiles(TempFolder))
                {
                    if (TryDelete(f)) n++;
                }
                if (n > 0) Log.Info("[rectest] 已清理上次留下的 " + n + " 个文件");
            }
            catch (Exception ex) { Log.Debug("[rectest] 清理临时目录失败: " + ex.Message); }
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) { File.Delete(path); return true; }
            }
            catch (Exception ex) { Log.Debug("[rectest] 删除失败 " + path + ": " + ex.Message); }
            return false;
        }

        private static void KeepForInspection(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                Say("已保留录像供排查: " + path);
        }

        private static bool? HasAudioTrack(string path, out string method)
        {
            try
            {
                var fileTask = Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask();
                if (!fileTask.Wait(TimeSpan.FromSeconds(WinRtTimeoutSeconds))) throw new TimeoutException("打开文件超时");

                var profileTask = Windows.Media.MediaProperties.MediaEncodingProfile
                    .CreateFromFileAsync(fileTask.Result).AsTask();
                if (!profileTask.Wait(TimeSpan.FromSeconds(WinRtTimeoutSeconds))) throw new TimeoutException("读取编码信息超时");

                var profile = profileTask.Result;
                method = "WinRT MediaEncodingProfile";
                return profile != null && profile.Audio != null;
            }
            catch (Exception ex)
            {
                Log.Debug("[rectest] WinRT 读取音轨信息失败，改用字节扫描: " + Brief(ex));
            }

            method = "字节扫描回退";
            try
            {
                byte[] needle = { (byte)'s', (byte)'o', (byte)'u', (byte)'n' };
                using (var fs = File.OpenRead(path))
                {
                    long len = fs.Length;
                    if (ScanFor(fs, 0, (int)Math.Min(ScanWindowBytes, len), needle)) return true;
                    if (len > ScanWindowBytes)
                        return ScanFor(fs, len - ScanWindowBytes, ScanWindowBytes, needle);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("[rectest] 字节扫描音轨失败: " + ex.Message);
                method = "两种方法均失败";
                return null;
            }
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
                if (!task.Wait(TimeSpan.FromSeconds(WinRtTimeoutSeconds))) throw new TimeoutException("打开文件超时");
                var file = task.Result;

                var propsTask = file.Properties.GetVideoPropertiesAsync().AsTask();
                if (!propsTask.Wait(TimeSpan.FromSeconds(WinRtTimeoutSeconds))) throw new TimeoutException("读取视频属性超时");
                return propsTask.Result.Duration.TotalSeconds;
            }
            catch (Exception ex)
            {
                Log.Debug("读取视频时长失败: " + Brief(ex));
                return -1;
            }
        }

        private static string Brief(Exception ex)
        {
            var agg = ex as AggregateException;
            if (agg != null && agg.InnerException != null) ex = agg.InnerException;
            return ex.GetType().Name + ": " + ex.Message;
        }
    }
}
