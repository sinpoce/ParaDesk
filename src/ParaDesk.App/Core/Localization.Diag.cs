using System;

namespace ParaDesk.Core
{
    internal static partial class L
    {
        static partial void AddDiag(Action<string, string> Add)
        {
            Add("诊断包的保存位置无效：{0}", "The location for the diagnostic bundle isn't valid: {0}");
            Add("目标位置已有同名的文件或文件夹，没有覆盖：{0}",
                "Something with that name already exists there, so it wasn't overwritten: {0}");
            Add("无法创建输出目录：{0}", "Couldn't create the output folder: {0}");
            Add("写入诊断包失败：{0}", "Couldn't write the diagnostic bundle: {0}");
            Add("生成诊断包失败：{0}", "Couldn't create the diagnostic bundle: {0}");
            Add("用法：ParaDesk.exe --diagbundle [<路径>]", "Usage: ParaDesk.exe --diagbundle [<path>]");

            Add("检查更新时出错：{0}", "Something went wrong while checking for updates: {0}");
            Add("无法解析 GitHub 的返回内容。", "Couldn't understand GitHub's response.");
            Add("连接 GitHub 超时。", "Timed out connecting to GitHub.");
            Add("还没有发布任何版本。", "No release has been published yet.");
            Add("GitHub 暂时拒绝了请求（可能是请求过于频繁），请稍后再试。",
                "GitHub turned the request down for now (possibly too many requests). Try again later.");
            Add("GitHub 返回了错误：HTTP {0}", "GitHub returned an error: HTTP {0}");
            Add("无法连接 GitHub：{0}", "Couldn't reach GitHub: {0}");
        }
    }
}
