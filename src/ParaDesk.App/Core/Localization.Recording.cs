using System;

namespace ParaDesk.Core
{
    internal static partial class L
    {
        static partial void AddRecording(Action<string, string> Add)
        {
            Add("音频设备不可用。", "No audio device is available.");
            Add("中途失去声音：", "Sound was lost partway through: ");

            Add("该录制器已经用过一次，请新建一个再录制。",
                "This recorder has already been used; create a new one to record again.");
            Add("录制目录所在磁盘只剩 {0}（至少需要 {1}），无法开始录制。",
                "Only {0} is free on the recording drive (at least {1} is needed), so recording can't start.");

            Add("录制目标没有产生画面（可能已最小化或被系统停止绘制）",
                "The capture target produced no frames (it may be minimized, or Windows stopped drawing it)");
            Add("录制中断：连续无法获取画面（显卡设备可能已丢失）。",
                "Recording stopped: frames repeatedly couldn't be captured (the graphics device may have been lost).");
            Add("录制中断：目标尺寸变化后无法继续捕获画面。",
                "Recording stopped: capture couldn't continue after the target was resized.");

            Add("硬件编码器不接受该配置，已改用软件编码",
                "The hardware encoder rejected this configuration, so software encoding was used instead");

            Add("保存截图超时，请稍后重试。", "Timed out saving the screenshot — try again.");
            Add("截图保存路径无效：", "Invalid screenshot path: ");
            Add("无法覆盖截图文件 {0}（可能正被其它程序打开，或是只读文件）：",
                "Couldn't replace the screenshot file {0} (it may be open in another program, or read-only): ");
        }
    }
}
