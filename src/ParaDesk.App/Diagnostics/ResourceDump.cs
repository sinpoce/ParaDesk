using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using ParaDesk.Core;

namespace ParaDesk.Diagnostics
{
    /// <summary>
    /// 把 WPF 主题字典里的资源键 dump 出来（--dumpres）。
    /// 排查"某个浮层背景是哪个画刷"这类问题时，猜键名极不可靠，直接看实际有什么。
    /// </summary>
    internal static class ResourceDump
    {
        private static void Say(string msg)
        {
            Console.WriteLine(msg);
            Log.Info("[dumpres] " + msg);
        }

        public static int Run()
        {
            if (!Shell.WpfHost.Initialize())
            {
                Say("WPF 宿主初始化失败");
                return 1;
            }

            var keys = new List<string>();
            Collect(Application.Current.Resources, keys, 0);
            keys.Sort(StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            sb.AppendLine("=== 资源键总数: " + keys.Count + " ===");
            sb.AppendLine();

            string[] interesting = { "ComboBox", "Acrylic", "Flyout", "Popup", "Menu", "Dropdown", "DropDown", "Card", "Solid" };
            foreach (string pat in interesting)
            {
                sb.AppendLine("---- 含 \"" + pat + "\" ----");
                foreach (string k in keys)
                    if (k.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0)
                        sb.AppendLine("  " + k + "   [" + TypeOf(k) + "]");
                sb.AppendLine();
            }

            string text = sb.ToString();
            Console.Write(text);
            string path = Path.Combine(Log.Dir, "resources.log");
            try
            {
                File.WriteAllText(path, text, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Say("写入 " + path + " 失败: " + ex.Message);
                return 1;
            }
            Say("已写入 " + path);
            return 0;
        }

        private static string TypeOf(string key)
        {
            try
            {
                object v = Application.Current.TryFindResource(key);
                return v == null ? "null" : v.GetType().Name;
            }
            catch (Exception ex)
            {
                Log.Debug("[dumpres] 取资源 " + key + " 失败: " + ex.Message);
                return "?";
            }
        }

        private static void Collect(ResourceDictionary dict, List<string> into, int depth)
        {
            if (dict == null || depth > 8) return;
            try
            {
                foreach (DictionaryEntry e in dict)
                {
                    var s = e.Key as string;
                    if (s != null && !into.Contains(s)) into.Add(s);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("[dumpres] 枚举资源字典失败（深度 " + depth + "）: " + ex.Message);
            }

            foreach (var child in dict.MergedDictionaries) Collect(child, into, depth + 1);
        }
    }
}
