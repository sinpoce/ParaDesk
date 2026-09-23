using System;
using System.IO;
using System.Security.AccessControl;
using System.Text;

namespace ParaDesk.Core
{
    internal enum LogLevel { Debug, Info, Warn, Error }

    internal static class Log
    {
        private static readonly object Sync = new object();
        private const long MaxBytes = 2 * 1024 * 1024;

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private const int ShareRetries = 2;
        private const int ShareRetryDelayMs = 10;

        private const long ForceTruncateBytes = 2 * MaxBytes;

        /// <summary>提权子进程可能运行在其他账户下，由父进程传入统一日志目录。</summary>
        public static string OverrideDir;

        public static string ProcessTag;

        private static string _ensuredDir;

        private static bool _rollFailureNoted;

        private const int ForceTruncateRetrySeconds = 60;
        private static DateTime _nextForceTruncateUtc = DateTime.MinValue;

        public static string Dir
        {
            get
            {
                string d = OverrideDir;
                if (string.IsNullOrEmpty(d)) return AppInfo.DataDir;
                if (!string.Equals(d, _ensuredDir, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Directory.CreateDirectory(d);
                        _ensuredDir = d;
                    }
                    catch { }
                }
                return d;
            }
        }

        public static string Path0 { get { return Path.Combine(Dir, "paradesk.log"); } }

        public static string Truncate()
        {
            return Truncate(false);
        }

        public static string Truncate(bool includeBackup)
        {
            lock (Sync)
            {
                string path = Path0;
                try
                {
                    using (var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete))
                    {
                        fs.SetLength(0);
                    }
                }
                catch (Exception ex) { return ex.Message; }

                if (!includeBackup) return null;
                try
                {
                    string bak = path + ".1";
                    if (File.Exists(bak)) File.Delete(bak);
                    return null;
                }
                catch (Exception ex) { return ex.Message; }
            }
        }

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
                    string note = Roll(path);
                    string text = FormatLine(level, msg);
                    if (note != null) text = FormatLine(LogLevel.Debug, note) + text;
                    AppendToFile(path, Utf8NoBom.GetBytes(text));
                }
            }
            catch { /* 日志失败绝不影响主流程 */ }
        }

        private static void AppendToFile(string path, byte[] bytes)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Append, FileSystemRights.AppendData,
                        FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None))
                    {
                        fs.Write(bytes, 0, bytes.Length);
                    }
                    return;
                }
                catch (IOException ex)
                {
                    int code = ex.HResult & 0xFFFF;
                    if (attempt >= ShareRetries || (code != 32 && code != 33)) throw;
                    System.Threading.Thread.Sleep(ShareRetryDelayMs);
                }
            }
        }

        private static string FormatLine(LogLevel level, string msg)
        {
            string tag = ProcessTag;
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  [" +
                   level.ToString().ToUpperInvariant() + "]  " +
                   (string.IsNullOrEmpty(tag) ? "" : "[" + tag + "] ") +
                   msg + "\r\n";
        }

        private static string Roll(string path)
        {
            long length;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return null;
                length = fi.Length;
            }
            catch { return null; }
            if (length < MaxBytes) return null;

            string bak = path + ".1";
            string moveError;
            try
            {
                if (File.Exists(bak)) File.Replace(path, bak, null);
                else File.Move(path, bak);
                _rollFailureNoted = false;
                return null;
            }
            catch (Exception ex) { moveError = ex.Message; }

            if (length >= ForceTruncateBytes && DateTime.UtcNow >= _nextForceTruncateUtc)
            {
                try
                {
                    File.Copy(path, bak, true);
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete))
                    {
                        fs.SetLength(0);
                    }
                    _rollFailureNoted = false;
                    return "日志滚动改名一直失败（" + moveError + "），已改为复制到 .1 后截断当前文件";
                }
                catch (Exception ex)
                {
                    _nextForceTruncateUtc = DateTime.UtcNow.AddSeconds(ForceTruncateRetrySeconds);
                    moveError = moveError + "；复制后截断也失败: " + ex.Message;
                }
            }

            if (_rollFailureNoted) return null;
            _rollFailureNoted = true;
            return "日志滚动失败，下次写入时重试: " + moveError;
        }
    }
}
