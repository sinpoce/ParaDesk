using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace ParaDesk.Core
{
    internal static class ExitCodes
    {
        public const int Ok = 0;
        public const int Failed = 1;
        public const int NotReady = 2;
        public const int NotRunning = 3;
        public const int Usage = 64;
    }

    [DataContract]
    internal class ControlRequest
    {
        [DataMember(Name = "cmd")] public string Command { get; set; }

        [DataMember(Name = "args")] public List<string> Args { get; set; }

        [DataMember(Name = "cwd")] public string WorkingDirectory { get; set; }

        public string Get(string name)
        {
            if (Args == null) return null;
            string key = "--" + name;
            for (int i = 0; i < Args.Count; i++)
            {
                string a = Args[i];
                if (a == null || !a.StartsWith("--", StringComparison.Ordinal) || IsBooleanFlag(a)) continue;
                if (string.Equals(a, key, StringComparison.OrdinalIgnoreCase))
                    return i + 1 < Args.Count ? Args[i + 1] : null;
                i++;
            }
            return null;
        }

        public bool Has(string flag)
        {
            if (Args == null) return false;
            string key = "--" + flag;
            for (int i = 0; i < Args.Count; i++)
            {
                string a = Args[i];
                if (a == null || !a.StartsWith("--", StringComparison.Ordinal)) continue;
                if (string.Equals(a, key, StringComparison.OrdinalIgnoreCase)) return true;
                if (!IsBooleanFlag(a)) i++;
            }
            return false;
        }

        public string Positional(int index)
        {
            if (Args == null) return null;
            int n = 0;
            for (int i = 0; i < Args.Count; i++)
            {
                string a = Args[i];
                if (a != null && a.StartsWith("--", StringComparison.Ordinal))
                {
                    if (!IsBooleanFlag(a) && i + 1 < Args.Count) i++;
                    continue;
                }
                if (n == index) return a;
                n++;
            }
            return null;
        }

        internal static bool IsBooleanFlag(string arg)
        {
            switch ((arg ?? "").ToLowerInvariant())
            {
                case "--json":
                case "--sound":
                case "--force":
                case "--wait":
                case "--copy":
                    return true;
                default:
                    return false;
            }
        }
    }

    [DataContract]
    internal class ControlResponse
    {
        [DataMember(Name = "ok")] public bool Ok { get; set; }

        [DataMember(Name = "exitCode")] public int ExitCode { get; set; }

        [DataMember(Name = "message")] public string Message { get; set; }

        [DataMember(Name = "json")] public string Json { get; set; }

        public static ControlResponse Success(string message, string json)
        {
            return new ControlResponse { Ok = true, ExitCode = ExitCodes.Ok, Message = message, Json = json };
        }

        public static ControlResponse Fail(int exitCode, string message)
        {
            return new ControlResponse { Ok = false, ExitCode = exitCode, Message = message };
        }
    }

    internal static class ControlProtocol
    {
        public static string ToJson<T>(T obj)
        {
            var ser = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream())
            {
                ser.WriteObject(ms, obj);
                return EscapeNonAscii(Encoding.UTF8.GetString(ms.ToArray()));
            }
        }

        public static T FromJson<T>(string json) where T : class
        {
            if (string.IsNullOrEmpty(json)) return null;
            var ser = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return ser.ReadObject(ms) as T;
        }

        public static string EscapeNonAscii(string s)
        {
            if (s == null) return null;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c < 0x80) sb.Append(c);
                else sb.Append("\\u").Append(((int)c).ToString("x4"));
            }
            return sb.ToString();
        }
    }
}
