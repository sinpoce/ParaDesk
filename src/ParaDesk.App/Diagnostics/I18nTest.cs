using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using ParaDesk.Core;

namespace ParaDesk.Diagnostics
{
    /// <summary>
    /// 本地化字典自检（--i18ntest）。
    ///
    /// 这两类错误的共同点是：只在英文界面下才会显形，而日常都在中文下测，
    /// 所以不做静态检查就等于没检查。
    ///   1. 同一个中文键被写了两种译文——后者覆盖前者，导航项变成别处的说法；
    ///   2. 译文里的 {0} 和原文对不上——多一个直接抛 FormatException，
    ///      少一个则静默丢内容。
    /// </summary>
    internal static class I18nTest
    {
        private static readonly Regex Placeholder = new Regex(@"\{(\d+)\}", RegexOptions.Compiled);

        public static int Run()
        {
            var sb = new StringBuilder();
            int problems = 0;

            sb.AppendLine("=== 本地化字典自检 ===");

            var conflicts = L.FindConflicts();
            sb.AppendLine("重复键（译文冲突） = " + conflicts.Count);
            foreach (string c in conflicts) sb.AppendLine("  冲突: " + c);
            problems += conflicts.Count;

            var map = L.Snapshot();
            sb.AppendLine("词条总数           = " + map.Count);

            int mismatched = 0;
            foreach (var kv in map)
            {
                var a = Slots(kv.Key);
                var b = Slots(kv.Value);
                if (SameSlots(a, b)) continue;

                mismatched++;
                sb.AppendLine("  占位符不一致: " + Trim(kv.Key));
                sb.AppendLine("             -> " + Trim(kv.Value));
            }
            sb.AppendLine("占位符不一致       = " + mismatched);
            problems += mismatched;

            // 空译文等于把界面上的字擦掉，比不翻译还糟
            int empty = 0;
            foreach (var kv in map)
            {
                if (kv.Value.Length == 0 && kv.Key.Length > 0)
                {
                    empty++;
                    sb.AppendLine("  空译文: " + Trim(kv.Key));
                }
            }
            sb.AppendLine("空译文             = " + empty);
            problems += empty;

            sb.AppendLine(problems == 0 ? "结果               = 通过" : "结果               = 有问题");

            string text = sb.ToString();
            Console.Write(text);
            Log.Info(text.TrimEnd());
            return problems == 0 ? 0 : 1;
        }

        private static List<int> Slots(string s)
        {
            var list = new List<int>();
            foreach (Match m in Placeholder.Matches(s))
            {
                int n = int.Parse(m.Groups[1].Value);
                if (!list.Contains(n)) list.Add(n);
            }
            list.Sort();
            return list;
        }

        private static bool SameSlots(List<int> a, List<int> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static string Trim(string s)
        {
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 70 ? s.Substring(0, 70) + "…" : s;
        }
    }
}
