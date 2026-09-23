using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Media.Editing;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.Streams;
using ParaDesk.Core;
using ParaDesk.Recording;

namespace ParaDesk.Diagnostics
{
    /// <summary>
    /// 录制内容校验（--contenttest）。
    ///
    /// 为什么需要它：时长、文件大小、音轨是否存在这些宏观指标全部正常，
    /// 画面内容却可能是错的——之前"样本直接引用帧池纹理、纹理被 WGC 回收后
    /// 编码到后来的画面"这个严重缺陷，就是因为只测宏观指标而漏掉的。
    ///
    /// 做法：录一个按已知时间切换纯色的窗口，再从产出的 MP4 里按时间抽帧，
    /// 比对像素颜色是否与当时应有的颜色一致。颜色错位即说明帧被串了。
    /// </summary>
    internal static class ContentTest
    {
        private const double PhaseSeconds = 3.0;

        private const int ColorTolerance = 150;

        private const int ThumbWidth = 160;
        private const int ThumbHeight = 90;

        private const int RedrawIntervalMs = 30;

        private const int StopTimeoutSeconds = 30;

        private const int WinRtTimeoutSeconds = 10;

        private const int WinRtDecodeTimeoutSeconds = 20;

        private static void Say(string msg)
        {
            Console.WriteLine(msg);
            Log.Info("[contenttest] " + msg);
        }

        private sealed class Phase
        {
            public string Name;
            public Color Color;
            public double StartSec;
            public double EndSec;
        }

        public static int Run()
        {
            try { return RunCore(); }
            catch (Exception ex)
            {
                Log.Error("内容校验崩溃", ex);
                Say("内容校验崩溃: " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        private static int RunCore()
        {
            if (!CaptureItemFactory.IsSupported) { Say("当前系统不支持屏幕捕获"); return 2; }
            if (!Shell.WpfHost.Initialize()) { Say("WPF 宿主初始化失败"); return 2; }
            Say("宿主就绪，准备测试窗口…");

            var phases = new List<Phase>
            {
                new Phase { Name = "红", Color = Color.FromRgb(220, 30, 30),  StartSec = 0,                EndSec = PhaseSeconds },
                new Phase { Name = "绿", Color = Color.FromRgb(30, 190, 60),  StartSec = PhaseSeconds,     EndSec = PhaseSeconds * 2 },
                new Phase { Name = "蓝", Color = Color.FromRgb(40, 90, 220),  StartSec = PhaseSeconds * 2, EndSec = PhaseSeconds * 3 },
            };
            double total = phases[phases.Count - 1].EndSec;

            int minGap = MinPhaseDistance(phases);
            if (minGap <= ColorTolerance)
            {
                Say("自检配置错误：两段颜色的最小距离 " + minGap + " 不大于容差 " + ColorTolerance + "，无法区分串帧");
                return 1;
            }

            var target = MonitorService.DefaultTarget();
            if (target == null) { Say("找不到显示器"); return 2; }

            // 一个铺满目标屏的纯色窗口作为已知画面源
            var win = new Window
            {
                // Style = null：退出 WPF-UI 的隐式 Window 样式。
                // 那个样式会在 Show() 之后再去设 AllowsTransparency，
                // 而 WPF 禁止窗口显示后修改该属性，会直接抛异常。
                Style = null,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                Background = new SolidColorBrush(phases[0].Color),
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 0, Top = 0, Width = 100, Height = 100,
            };
            win.Show();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            ParaDesk.Native.NativeMethods.SetWindowPos(hwnd,
                ParaDesk.Native.NativeMethods.HWND_TOPMOST,
                target.Bounds.X, target.Bounds.Y, target.Bounds.Width, target.Bounds.Height,
                ParaDesk.Native.NativeMethods.SWP_NOACTIVATE);

            var options = RecordingOptions.CreateDefault();
            options.OutputFolder = Path.Combine(Path.GetTempPath(), "ParaDeskContentTest");
            options.FrameRate = 30;
            options.BitrateMbps = 16;
            options.Audio = AudioSource.None;   // 本测试只关心画面
            options.CaptureCursor = false;

            var captureTarget = new CaptureTarget
            {
                Kind = CaptureTargetKind.Window,
                Handle = hwnd,
                Title = "内容校验",
            };

            string file = null, error = null;
            bool finished;
            var done = new ManualResetEventSlim(false);
            using (var recorder = new ScreenRecorder())
            {
                recorder.Stopped += delegate(object s, RecordingStoppedEventArgs e)
                {
                    file = e.FilePath; error = e.Error; done.Set();
                };

                Say("录制 " + total + " 秒的三段纯色（红→绿→蓝）…");
                string err = recorder.Start(captureTarget, options);
                if (err != null) { Say("启动失败: " + err); win.Close(); return 1; }

                // 按计划切换颜色；用 WPF 调度器切换才能真正触发重绘
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int current = 0;
                while (sw.Elapsed.TotalSeconds < total)
                {
                    double t = sw.Elapsed.TotalSeconds;
                    int want = 0;
                    for (int i = 0; i < phases.Count; i++)
                        if (t >= phases[i].StartSec) want = i;

                    if (want != current)
                    {
                        current = want;
                        var c = phases[want].Color;
                        win.Dispatcher.Invoke((Action)delegate
                        {
                            win.Background = new SolidColorBrush(c);
                        });
                    }
                    // 让窗口持续重绘，确保 WGC 有帧可送
                    win.Dispatcher.Invoke(DispatcherPriority.Render, (Action)delegate { win.InvalidateVisual(); });
                    Thread.Sleep(RedrawIntervalMs);
                }

                recorder.Stop();
                finished = done.Wait(TimeSpan.FromSeconds(StopTimeoutSeconds));
            }
            win.Dispatcher.Invoke((Action)delegate { win.Close(); });

            if (!finished) { Say("停止超时"); return 1; }
            if (error != null) { Say("录制报错: " + error); return 1; }
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) { Say("未生成文件"); return 1; }

            Say("文件: " + file);
            return Verify(file, phases);
        }

        /// <summary>在每一段的中点抽一帧，比对主色调。</summary>
        private static int Verify(string path, List<Phase> phases)
        {
            try
            {
                var fileTask = StorageFile.GetFileFromPathAsync(path).AsTask();
                fileTask.Wait(TimeSpan.FromSeconds(WinRtTimeoutSeconds));

                var clipTask = MediaClip.CreateFromFileAsync(fileTask.Result).AsTask();
                clipTask.Wait(TimeSpan.FromSeconds(WinRtDecodeTimeoutSeconds));

                var composition = new MediaComposition();
                composition.Clips.Add(clipTask.Result);

                int pass = 0, fail = 0;
                foreach (var ph in phases)
                {
                    double mid = (ph.StartSec + ph.EndSec) / 2.0;
                    Color? got = GrabColor(composition, mid);
                    if (got == null)
                    {
                        Say(string.Format("  {0,-4} @{1:N1}s  抽帧失败", ph.Name, mid));
                        fail++;
                        continue;
                    }

                    int d = Distance(got.Value, ph.Color);
                    bool ok = d < ColorTolerance;
                    Say(string.Format("  {0,-4} @{1:N1}s  期望 #{2:X2}{3:X2}{4:X2}  实际 #{5:X2}{6:X2}{7:X2}  距离 {8}  {9}",
                        ph.Name, mid, ph.Color.R, ph.Color.G, ph.Color.B,
                        got.Value.R, got.Value.G, got.Value.B, d, ok ? "通过" : "不符"));
                    if (ok) pass++; else fail++;
                }

                Say("");
                Say("结果: " + pass + " 通过 / " + fail + " 不符");
                if (fail == 0)
                {
                    Say("画面内容与时间轴一致 —— 未发现帧错位或纹理被回收覆盖的迹象。");
                    return 0;
                }
                Say("画面内容与预期不符：帧可能被串位，或纹理在编码前已被回收。");
                return 1;
            }
            catch (Exception ex)
            {
                Log.Error("内容校验失败", ex);
                Say("校验过程出错: " + ex.Message);
                return 1;
            }
        }

        private static int Distance(Color a, Color b)
        {
            return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
        }

        private static int MinPhaseDistance(List<Phase> phases)
        {
            int min = int.MaxValue;
            for (int i = 0; i < phases.Count; i++)
                for (int j = i + 1; j < phases.Count; j++)
                    min = Math.Min(min, Distance(phases[i].Color, phases[j].Color));
            return min;
        }

        /// <summary>取指定时刻的缩略图并读取中心区域的平均色。</summary>
        private static Color? GrabColor(MediaComposition composition, double seconds)
        {
            try
            {
                var thumbTask = composition.GetThumbnailAsync(
                    TimeSpan.FromSeconds(seconds), ThumbWidth, ThumbHeight,
                    VideoFramePrecision.NearestFrame).AsTask();
                thumbTask.Wait(TimeSpan.FromSeconds(WinRtDecodeTimeoutSeconds));

                using (var stream = thumbTask.Result)
                {
                    var decoderTask = Windows.Graphics.Imaging.BitmapDecoder
                        .CreateAsync(stream).AsTask();
                    decoderTask.Wait(TimeSpan.FromSeconds(WinRtTimeoutSeconds));

                    var pixelTask = decoderTask.Result.GetPixelDataAsync().AsTask();
                    pixelTask.Wait(TimeSpan.FromSeconds(WinRtTimeoutSeconds));

                    byte[] pixels = pixelTask.Result.DetachPixelData();
                    int w = (int)decoderTask.Result.PixelWidth;
                    int h = (int)decoderTask.Result.PixelHeight;
                    if (w <= 0 || h <= 0 || pixels.Length < w * h * 4) return null;

                    // 只取中心 1/4 区域，避开可能的边框与缩放插值
                    long r = 0, g = 0, b = 0; int n = 0;
                    for (int y = h / 4; y < h * 3 / 4; y++)
                    {
                        for (int x = w / 4; x < w * 3 / 4; x++)
                        {
                            int i = (y * w + x) * 4;
                            b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2];
                            n++;
                        }
                    }
                    if (n == 0) return null;
                    return Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
                }
            }
            catch (Exception ex)
            {
                Log.Debug("抽帧失败 @" + seconds + "s: " + ex.Message);
                return null;
            }
        }
    }
}
