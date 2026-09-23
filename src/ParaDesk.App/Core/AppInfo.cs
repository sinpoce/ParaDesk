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

        public static string DisplayTitle
        {
            get { return L.T(Title); }
        }

        private static string _dataDir;

        public static string DataDir
        {
            get
            {
                string d = _dataDir;
                if (d != null) return d;

                d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    ProductName);
                try
                {
                    Directory.CreateDirectory(d);
                    _dataDir = d;
                }
                catch (Exception)
                {
                }
                return d;
            }
        }

        public static string SettingsPath { get { return Path.Combine(DataDir, "settings.json"); } }

        public static string LogPath { get { return Log.Path0; } }

        public static string ExecutablePath
        {
            get { return Assembly.GetEntryAssembly().Location; }
        }
    }
}
