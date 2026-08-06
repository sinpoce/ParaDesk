using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ParaDesk.Core;

namespace ParaDesk.Shell
{
    /// <summary>
    /// 应用内日志查看器。
    /// 出问题时让用户自己去翻 %LOCALAPPDATA% 里的文件太苛刻；
    /// 这里直接看、能筛、能一键复制或打包。
    /// </summary>
    internal partial class LogViewer
    {
        private readonly DispatcherTimer _timer;
        private long _lastLength;
        private List<string> _lines = new List<string>();

        public LogViewer()
        {
            InitializeComponent();
            LblPath.Text = Log.Path0;
            CbLevel.SelectedIndex = 0;

            Reload(true);

            // 跟随文件增长；只在长度变化时才重读，避免无谓 IO
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += delegate { Reload(false); };
            _timer.Start();

            Closed += delegate { _timer.Stop(); };
        }

        private void Reload(bool force)
        {
            try
            {
                string path = Log.Path0;
                if (!File.Exists(path)) { TbLog.Text = L.T("（暂无日志）"); return; }

                var fi = new FileInfo(path);
                if (!force && fi.Length == _lastLength) return;
                _lastLength = fi.Length;

                // 共享读写打开：日志正被写入时也能读
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    var all = new List<string>();
                    string line;
                    while ((line = sr.ReadLine()) != null) all.Add(line);

                    // 只保留最后 3000 行，避免长期运行后界面卡顿
                    if (all.Count > 3000) all.RemoveRange(0, all.Count - 3000);
                    _lines = all;
                }
                ApplyFilter();
            }
            catch (Exception ex)
            {
                TbLog.Text = L.T("读取日志失败：") + ex.Message;
            }
        }

        private void ApplyFilter()
        {
            string kw = TbFilter.Text != null ? TbFilter.Text.Trim() : "";
            int level = CbLevel.SelectedIndex;

            var sb = new StringBuilder();
            int shown = 0;
            foreach (string line in _lines)
            {
                if (!LevelPasses(line, level)) continue;
                if (kw.Length > 0 && line.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) continue;
                sb.AppendLine(line);
                shown++;
            }

            TbLog.Text = sb.Length > 0 ? sb.ToString() : L.T("（无匹配内容）");
            LblCount.Text = string.Format(L.T("{0} / {1} 行"), shown, _lines.Count);

            if (SwAutoScroll.IsChecked == true) Scroller.ScrollToEnd();
        }

        private static bool LevelPasses(string line, int level)
        {
            switch (level)
            {
                case 1: return !line.Contains("[DEBUG]");
                case 2: return line.Contains("[WARN]") || line.Contains("[ERROR]");
                case 3: return line.Contains("[ERROR]");
                default: return true;
            }
        }

        private void OnFilterChanged(object sender, EventArgs e)
        {
            if (!IsLoaded) return;
            ApplyFilter();
        }

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(TbLog.Text ?? "");
                LblCount.Text = L.T("已复制到剪贴板");
            }
            catch (Exception ex) { Log.Error("复制日志失败", ex); }
        }

        private void OnClear(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, L.T("确定清空日志文件吗？"), AppInfo.ProductName,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                File.WriteAllText(Log.Path0, "", Encoding.UTF8);
                _lastLength = 0;
                Reload(true);
                Log.Info("用户清空了日志");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.T("清空失败：") + ex.Message, AppInfo.ProductName,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnClose(object sender, RoutedEventArgs e) { Close(); }
    }
}
