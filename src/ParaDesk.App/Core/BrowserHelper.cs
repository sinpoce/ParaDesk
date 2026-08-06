using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace ParaDesk.Core
{
    internal class BrowserInfo
    {
        public string Name;
        public string ExePath;
        /// <summary>该浏览器专供分身桌面使用的用户数据目录。</summary>
        public string ProfileDir;
    }

    /// <summary>
    /// 浏览器隔离助手。
    ///
    /// 解决一个必然会遇到的冲突：Chromium 系浏览器同一份用户数据目录
    /// 无法在两个会话里同时打开——你在主桌面开着 Edge，分身桌面里再点就没反应
    /// （或抢走原窗口）。微软的 Power Automate 用的是同一套解法：
    /// 给分身桌面自动准备一份独立的用户数据目录。
    /// 这里生成一个带 --user-data-dir 的快捷方式放到分身桌面的桌面上。
    /// </summary>
    internal static class BrowserHelper
    {
        /// <summary>探测本机已安装的 Chromium 系浏览器。</summary>
        public static List<BrowserInfo> Detect()
        {
            var list = new List<BrowserInfo>();
            AddIfExists(list, "Microsoft Edge", "msedge.exe", "EdgeParaDesk");
            AddIfExists(list, "Google Chrome", "chrome.exe", "ChromeParaDesk");
            return list;
        }

        private static void AddIfExists(List<BrowserInfo> list, string name, string exeName, string profileName)
        {
            string path = ResolveAppPath(exeName);
            if (string.IsNullOrEmpty(path)) return;

            list.Add(new BrowserInfo
            {
                Name = name,
                ExePath = path,
                ProfileDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    AppInfo.ProductName, profileName),
            });
        }

        /// <summary>用 App Paths 注册表项定位可执行文件，比硬编码安装路径可靠。</summary>
        private static string ResolveAppPath(string exeName)
        {
            string[] roots =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\",
            };
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (string root in roots)
                {
                    try
                    {
                        using (var k = hive.OpenSubKey(root + exeName))
                        {
                            if (k == null) continue;
                            var v = k.GetValue(null) as string;
                            if (string.IsNullOrEmpty(v)) continue;
                            v = v.Trim('"');
                            if (File.Exists(v)) return v;
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>
        /// 在桌面创建带独立用户数据目录的快捷方式。
        /// 返回快捷方式路径；失败返回 null。
        /// </summary>
        public static string CreateIsolatedShortcut(BrowserInfo browser, out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(browser.ProfileDir);

                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string linkPath = Path.Combine(desktop, browser.Name + L.T("（分身桌面）") + ".lnk");

                // 用 WScript.Shell 建快捷方式，避免引入 COM 引用
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) { error = L.T("系统不支持创建快捷方式。"); return null; }

                dynamic shell = Activator.CreateInstance(shellType);
                dynamic link = shell.CreateShortcut(linkPath);
                link.TargetPath = browser.ExePath;
                link.Arguments = "--user-data-dir=\"" + browser.ProfileDir + "\"";
                link.WorkingDirectory = Path.GetDirectoryName(browser.ExePath);
                link.IconLocation = browser.ExePath + ",0";
                link.Description = L.T("在分身桌面里使用的独立浏览器配置，可与主桌面同时运行");
                link.Save();

                Log.Info("已创建浏览器隔离快捷方式: " + linkPath);
                return linkPath;
            }
            catch (Exception ex)
            {
                Log.Error("创建浏览器快捷方式失败", ex);
                error = ex.Message;
                return null;
            }
        }
    }
}
