using System;

namespace ParaDesk.Ui
{
    internal class NoticeInfo
    {
        public const string LevelInfo = "info";
        public const string LevelWarn = "warn";
        public const string LevelError = "error";

        public string Title { get; set; }
        public string Body { get; set; }
        public string Level { get; set; }
        public DateTime Time { get; set; }

        public bool IsWarning
        {
            get { return Level == LevelWarn || Level == LevelError; }
        }

        public static string NormalizeLevel(string level)
        {
            string v = (level ?? "").Trim().ToLowerInvariant();
            switch (v)
            {
                case "":
                case "info":
                case "information":
                    return LevelInfo;
                case "warn":
                case "warning":
                    return LevelWarn;
                case "error":
                case "err":
                    return LevelError;
                default:
                    return null;
            }
        }
    }
}
