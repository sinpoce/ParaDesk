using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace ParaDesk.Core
{
    /// <summary>
    /// 影响分身桌面画面流畅度的系统级开关。
    /// 这些都写在 HKLM，需要管理员权限，因此由提权子进程统一执行。
    /// </summary>
    internal static class PerformanceSettings
    {
        private const string WinStationsKey =
            @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations";

        /// <summary>
        /// 远程会话默认把帧率压在 30 FPS。DWMFRAMEINTERVAL 是帧间隔（毫秒），
        /// 15 对应 ~60 FPS。微软支持文档给出的官方做法，需重启生效。
        /// 返回 null 表示未设置（即系统默认 30 FPS）。
        /// </summary>
        public static int? GetFrameInterval()
        {
            try
            {
                object v = Registry.GetValue(WinStationsKey, "DWMFRAMEINTERVAL", null);
                if (v == null) return null;
                return Convert.ToInt32(v);
            }
            catch { return null; }
        }

        /// <summary>把帧间隔换算成大致的帧率上限，用于界面显示。</summary>
        public static int FrameIntervalToFps(int? interval)
        {
            if (interval == null || interval.Value <= 0) return 30;
            return (int)Math.Round(1000.0 / interval.Value);
        }

        public static int FpsToInterval(int fps)
        {
            if (fps <= 30) return 0;      // 0 表示删除该值，回到系统默认
            return Math.Max(1, (int)Math.Round(1000.0 / fps));
        }

        /// <summary>
        /// 找出与注册表当前值**精确对应**的那一档，找不到返回 -1。
        ///
        /// 必须按 interval 比、不能按 FPS 比：FpsToInterval 与 FrameIntervalToFps
        /// 不是互逆的（60 → 17ms → 读回 59），按 FPS 比会把本来命中的档判成没命中。
        ///
        /// 之前这里是"吸附到最近的一档"，那会让界面撒谎——注册表存着 144，
        /// 而目标屏只到 60 时，下拉会显示"60 FPS"，用户以为设的就是 60。
        /// 现在返回 -1，由调用方插一条带真实值的占位项，
        /// 与"分辨率"那边"（该屏未报告支持）"的既有做法一致。
        /// </summary>
        public static int ExactChoice(int? interval, List<int> choices)
        {
            if (choices == null) return -1;
            int have = interval.HasValue ? interval.Value : 0;
            if (have < 0) have = 0;

            // 帧间隔是整毫秒，多个帧率会落到同一个值（119/120/125 都是 8ms）。
            // 命中的挑一个离标称帧率最近的，否则会显示成 119 这种莫名其妙的数。
            int nominal = FrameIntervalToFps(interval);
            int best = -1, bestDiff = int.MaxValue;
            foreach (int c in choices)
            {
                if (FpsToInterval(c) != have) continue;
                int d = Math.Abs(c - nominal);
                if (d >= bestDiff) continue;
                bestDiff = d;
                best = c;
            }
            return best;
        }

        /// <summary>
        /// 所有显示器刷新率的并集，作为帧率下拉的候选。
        ///
        /// 刻意不按单块屏给候选：这个值写在 HKLM，是整机单值。
        /// 若候选跟着目标显示器变，用户换一块屏就看到列表变了，
        /// 会理所当然地以为"帧率是按屏保存的"——而那是做不到的。
        /// 列表纹丝不动，才是它是全局设置的最直接证据。
        /// 超出当前目标屏能力的档由界面另行标注，那是选值的建议，不是保存范围。
        /// </summary>
        public static List<int> GetFpsChoicesForAll(IEnumerable<string> devices)
        {
            var result = new List<int> { 30 };
            if (devices == null) return result;

            foreach (string dev in devices)
            {
                try
                {
                    foreach (int hz in DisplayCapabilities.GetRefreshRates(dev))
                    {
                        if (hz <= 30 || result.Contains(hz)) continue;
                        result.Add(hz);
                    }
                }
                catch (Exception ex) { Log.Error("获取可选帧率失败: " + dev, ex); }
            }

            result.Sort();
            return result;
        }

        /// <summary>写入帧间隔（需要管理员）。interval<=0 表示恢复默认。</summary>
        public static bool ApplyFrameInterval(int interval)
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations", true))
                {
                    if (k == null) return false;
                    if (interval <= 0)
                    {
                        if (k.GetValue("DWMFRAMEINTERVAL") != null) k.DeleteValue("DWMFRAMEINTERVAL", false);
                        Log.Info("已恢复默认帧率上限（30 FPS）");
                    }
                    else
                    {
                        k.SetValue("DWMFRAMEINTERVAL", interval, RegistryValueKind.DWord);
                        Log.Info("已设置帧间隔 " + interval + " ms（约 " + FrameIntervalToFps(interval) + " FPS）");
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error("写入帧率设置失败", ex);
                return false;
            }
        }

        /// <summary>这项改动是否需要重启才生效。</summary>
        public const string FrameIntervalNote = "修改后需重启电脑生效";   // 调用处包 L.T
    }
}
