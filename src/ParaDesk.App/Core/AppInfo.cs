using System;
using System.IO;
using System.Reflection;

namespace ParaDesk.Core
{
    /// <summary>产品级常量与路径。改名只需改这里。</summary>
    internal static class AppInfo
    {
        public const string ProductName = "ParaDesk";
        public const string ProductNameZh = "分身桌面";
        public const string Tagline = "给你的电脑开分身";

        /// <summary>单实例互斥体名。用 Global 前缀，使子会话内再次启动也会被拦下。</summary>
        public const string MutexNameGlobal = "Global\\ParaDeskSingleInstance";
        public const string MutexNameLocal = "Local\\ParaDeskSingleInstance";

        public static string Version
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "1.0.0" : v.ToString(3);
            }
        }

        public static string Title
        {
            get { return ProductName + " " + ProductNameZh; }
        }

        /// <summary>用户数据目录（日志、设置）。</summary>
        public static string DataDir
        {
            get
            {
                string d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    ProductName);
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        public static string SettingsPath { get { return Path.Combine(DataDir, "settings.json"); } }
        public static string LogPath { get { return Path.Combine(DataDir, "paradesk.log"); } }

        public static string ExecutablePath
        {
            get { return Assembly.GetEntryAssembly().Location; }
        }
    }
}
