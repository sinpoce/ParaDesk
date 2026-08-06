using System;
using System.IO;
using System.Text;

namespace ParaDesk.Core
{
    internal enum LogLevel { Debug, Info, Warn, Error }

    /// <summary>轻量文件日志：带级别、自动滚动、线程安全。诊断包直接打包该文件。</summary>
    internal static class Log
    {
        private static readonly object Sync = new object();
        private const long MaxBytes = 2 * 1024 * 1024;

        /// <summary>提权子进程可能运行在其他账户下，由父进程传入统一日志目录。</summary>
        public static string OverrideDir;

        public static string Dir
        {
            get
            {
                string d = OverrideDir;
                if (string.IsNullOrEmpty(d)) return AppInfo.DataDir;
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        public static string Path0 { get { return Path.Combine(Dir, "paradesk.log"); } }

        public static void Debug(string msg) { Write(LogLevel.Debug, msg); }
        public static void Info(string msg) { Write(LogLevel.Info, msg); }
        public static void Warn(string msg) { Write(LogLevel.Warn, msg); }
        public static void Error(string msg) { Write(LogLevel.Error, msg); }

        public static void Error(string msg, Exception ex)
        {
            Write(LogLevel.Error, msg + " :: " + (ex == null ? "(null)" : ex.ToString()));
        }

        public static void Write(LogLevel level, string msg)
        {
            try
            {
                lock (Sync)
                {
                    string path = Path0;
                    Roll(path);
                    File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  [" +
                        level.ToString().ToUpperInvariant() + "]  " + msg + "\r\n",
                        Encoding.UTF8);
                }
            }
            catch { /* 日志失败绝不影响主流程 */ }
        }

        private static void Roll(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < MaxBytes) return;
                string bak = path + ".1";
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(path, bak);
            }
            catch { }
        }
    }
}
