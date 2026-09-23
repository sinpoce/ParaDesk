using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ParaDesk.Core;

namespace ParaDesk.Shell
{
    internal partial class LogViewer
    {
        private const int MaxLines = 3000;

        private const int RebuildSlack = 1000;

        private const int MaxReadBytes = 8 * 1024 * 1024;

        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private static readonly string[] LevelTags = { "[DEBUG]", "[INFO]", "[WARN]", "[ERROR]" };

        private sealed class LogRecord
        {
            public int Level;
            public readonly List<string> Lines = new List<string>(1);
        }

        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _filterDebounce;
        private readonly List<LogRecord> _records = new List<LogRecord>();
        private int _lineCount;

        private long _offset;
        private long _backupLength = -1;
        private DateTime _backupWrite;

        private string _filterKw = "";
        private int _filterLevel;

        private int _shownLines;
        private int _matchedLines;
        private bool _placeholder;

        private bool _closed;
        private bool _exporting;

        public LogViewer()
        {
            InitializeComponent();

            Localizer.Translate(this);

            LblPath.Text = Log.Path0;
            CbLevel.SelectedIndex = 0;

            _filterDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _filterDebounce.Tick += delegate
            {
                _filterDebounce.Stop();
                ApplyFilter();
            };

            FullReload();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += delegate { Poll(); };
            _timer.Start();

            Closed += delegate
            {
                _closed = true;
                _timer.Stop();
                _filterDebounce.Stop();
            };
        }

        private static string BackupPath { get { return Log.Path0 + ".1"; } }

        private void Poll()
        {
            if (_closed) return;
            try
            {
                var fi = new FileInfo(Log.Path0);
                long len = fi.Exists ? fi.Length : 0;

                if (len < _offset || BackupChanged()) { FullReload(); return; }
                if (len == _offset) return;

                var fresh = new List<string>();
                long next = ReadLines(Log.Path0, _offset, fresh);
                if (next < 0) { FullReload(); return; }
                _offset = next;
                if (fresh.Count > 0) AppendLines(fresh);
            }
            catch (Exception ex)
            {
                LblCount.Text = L.T("读取日志失败：") + ex.Message;
            }
        }

        private void FullReload()
        {
            try
            {
                SnapshotBackup();

                var current = new List<string>();
                long offset = 0;
                if (File.Exists(Log.Path0)) offset = ReadLines(Log.Path0, 0, current);

                var all = new List<string>();
                if (current.Count < MaxLines && _backupLength >= 0)
                {
                    try { ReadLines(BackupPath, 0, all); }
                    catch (Exception ex) { Log.Debug("读取滚动日志失败: " + ex.Message); }
                }
                all.AddRange(current);

                if (all.Count > MaxLines)
                {
                    all.RemoveRange(0, all.Count - MaxLines);
                    int firstHeader = all.FindIndex(IsRecordStart);
                    if (firstHeader > 0) all.RemoveRange(0, firstHeader);
                }

                _offset = offset;
                _records.Clear();
                _lineCount = 0;
                foreach (string line in all) AddLine(line);

                RebuildView();
            }
            catch (Exception ex)
            {
                TbLog.Text = L.T("读取日志失败：") + ex.Message;
                _placeholder = true;
                _shownLines = 0;
            }
        }

        private void SnapshotBackup()
        {
            var fi = new FileInfo(BackupPath);
            _backupLength = fi.Exists ? fi.Length : -1;
            _backupWrite = fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue;
        }

        private bool BackupChanged()
        {
            var fi = new FileInfo(BackupPath);
            long len = fi.Exists ? fi.Length : -1;
            DateTime w = fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue;
            return len != _backupLength || w != _backupWrite;
        }

        private static long ReadLines(string path, long from, List<string> into)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                long len = fs.Length;
                if (len < from) return -1;
                if (len == from) return from;

                bool skipPartial = false;
                long start = from;
                if (len - start > MaxReadBytes)
                {
                    start = len - MaxReadBytes;
                    skipPartial = true;
                }

                int count = (int)(len - start);
                var buf = new byte[count];
                fs.Seek(start, SeekOrigin.Begin);
                int read = 0;
                while (read < count)
                {
                    int n = fs.Read(buf, read, count - read);
                    if (n <= 0) break;
                    read += n;
                }

                if (read <= 0) return from;
                int end = Array.LastIndexOf(buf, (byte)'\n', read - 1, read);
                if (end < 0) return from;

                int begin = 0;
                if (skipPartial)
                {
                    int nl = Array.IndexOf(buf, (byte)'\n', 0, end + 1);
                    begin = nl + 1;
                }
                else if (start == 0 && read >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF)
                {
                    begin = 3;
                }

                if (end + 1 > begin)
                {
                    string text = Utf8.GetString(buf, begin, end + 1 - begin);
                    string[] parts = text.Split('\n');
                    for (int i = 0; i < parts.Length - 1; i++) into.Add(parts[i].TrimEnd('\r'));
                }
                return start + end + 1;
            }
        }

        private static bool IsRecordStart(string s)
        {
            if (s == null || s.Length < 19) return false;
            for (int i = 0; i < 19; i++)
            {
                char c = s[i];
                switch (i)
                {
                    case 4:
                    case 7:
                        if (c != '-') return false;
                        break;
                    case 10:
                        if (c != ' ') return false;
                        break;
                    case 13:
                    case 16:
                        if (c != ':') return false;
                        break;
                    default:
                        if (c < '0' || c > '9') return false;
                        break;
                }
            }
            return true;
        }

        private static int ParseLevel(string header)
        {
            int best = -1, level = 1;
            if (header == null || header.Length <= 19) return level;
            for (int k = 0; k < LevelTags.Length; k++)
            {
                int idx = header.IndexOf(LevelTags[k], 19, StringComparison.Ordinal);
                if (idx >= 0 && (best < 0 || idx < best)) { best = idx; level = k; }
            }
            return level;
        }

        private void AddLine(string line)
        {
            if (_records.Count == 0 || IsRecordStart(line))
            {
                var r = new LogRecord { Level = ParseLevel(line) };
                r.Lines.Add(line);
                _records.Add(r);
            }
            else
            {
                _records[_records.Count - 1].Lines.Add(line);
            }
            _lineCount++;
        }

        private bool Passes(LogRecord r)
        {
            if (r.Level < _filterLevel) return false;
            if (_filterKw.Length == 0) return true;
            foreach (string line in r.Lines)
                if (line.IndexOf(_filterKw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private void TrimFront()
        {
            int drop = 0, dropLines = 0, dropMatched = 0;
            while (drop < _records.Count - 1 && _lineCount - dropLines > MaxLines)
            {
                var r = _records[drop];
                dropLines += r.Lines.Count;
                if (Passes(r)) dropMatched += r.Lines.Count;
                drop++;
            }
            if (drop == 0) return;
            _records.RemoveRange(0, drop);
            _lineCount -= dropLines;
            _matchedLines -= dropMatched;
        }

        private void AppendLines(List<string> lines)
        {
            var sb = new StringBuilder();
            int appended = 0;
            bool rebuild = false;
            int i = 0;

            if (_records.Count > 0)
            {
                var last = _records[_records.Count - 1];
                bool passedBefore = Passes(last);
                while (i < lines.Count && !IsRecordStart(lines[i]))
                {
                    last.Lines.Add(lines[i]);
                    _lineCount++;
                    if (passedBefore)
                    {
                        sb.Append(lines[i]).Append("\r\n");
                        appended++;
                    }
                    i++;
                }
                if (!passedBefore && Passes(last)) rebuild = true;
            }

            int firstNew = _records.Count;
            for (; i < lines.Count; i++) AddLine(lines[i]);
            for (int k = firstNew; k < _records.Count; k++)
            {
                var r = _records[k];
                if (!Passes(r)) continue;
                foreach (string line in r.Lines) sb.Append(line).Append("\r\n");
                appended += r.Lines.Count;
            }

            _matchedLines += appended;
            TrimFront();

            if (rebuild || (appended > 0 && (_placeholder || _shownLines + appended > MaxLines + RebuildSlack)))
            {
                RebuildView();
                return;
            }

            if (appended > 0)
            {
                TbLog.AppendText(sb.ToString());
                _shownLines += appended;
                if (SwAutoScroll.IsChecked == true) Scroller.ScrollToEnd();
            }
            UpdateCount();
        }

        private void RebuildView()
        {
            var sb = new StringBuilder();
            int shown = 0;
            foreach (var r in _records)
            {
                if (!Passes(r)) continue;
                foreach (string line in r.Lines) sb.Append(line).Append("\r\n");
                shown += r.Lines.Count;
            }

            _matchedLines = shown;
            _shownLines = shown;
            _placeholder = shown == 0;
            TbLog.Text = shown > 0 ? sb.ToString()
                : (_records.Count == 0 ? L.T("（暂无日志）") : L.T("（无匹配内容）"));
            UpdateCount();

            if (SwAutoScroll.IsChecked == true) Scroller.ScrollToEnd();
        }

        private void UpdateCount()
        {
            LblCount.Text = string.Format(L.T("{0} / {1} 行"), _matchedLines, _lineCount);
        }

        private void ApplyFilter()
        {
            _filterKw = TbFilter.Text != null ? TbFilter.Text.Trim() : "";
            _filterLevel = Math.Max(0, CbLevel.SelectedIndex);
            RebuildView();
        }

        private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            _filterDebounce.Stop();
            _filterDebounce.Start();
        }

        private void OnLevelChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            _filterDebounce.Stop();
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
            if (MessageBox.Show(this, L.T("确定清空日志吗？当前日志和上一份滚动日志（paradesk.log.1）都会被删除。"),
                    AppInfo.ProductName, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            string err = Log.Truncate();
            if (err != null)
            {
                MessageBox.Show(this, L.T("清空失败：") + err, AppInfo.ProductName,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                if (File.Exists(BackupPath)) File.Delete(BackupPath);
            }
            catch (Exception ex) { Log.Warn("删除滚动日志失败: " + ex.Message); }

            Log.Info("用户清空了日志");
            FullReload();
        }

        private void OnExport(object sender, RoutedEventArgs e)
        {
            if (_exporting) return;
            _exporting = true;
            BtnExport.IsEnabled = false;
            LblCount.Text = L.T("正在生成诊断包…");

            var dispatcher = Dispatcher;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string path = null, error = null;
                try
                {
                    path = ParaDesk.Diagnostics.DiagBundle.Create(null, out error);
                }
                catch (Exception ex)
                {
                    Log.Error("生成诊断包失败", ex);
                    error = string.Format(L.T("生成诊断包失败：{0}"), ex.Message);
                }

                try
                {
                    dispatcher.BeginInvoke(new Action(delegate { OnExportDone(path, error); }));
                }
                catch (Exception ex) { Log.Debug("诊断包结果回到界面线程失败: " + ex.Message); }
            });
        }

        private void OnExportDone(string path, string error)
        {
            _exporting = false;
            if (!_closed) BtnExport.IsEnabled = true;

            if (!string.IsNullOrEmpty(path))
            {
                Log.Info("已导出诊断包: " + path);
                if (!_closed) LblCount.Text = L.T("已生成诊断包");
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                }
                catch (Exception ex)
                {
                    Log.Warn("在资源管理器中定位诊断包失败: " + ex.Message);
                    ShowMessage(L.T("已生成诊断包") + "\r\n" + path, MessageBoxImage.Information);
                }
                return;
            }

            if (!_closed) UpdateCount();
            string msg = string.IsNullOrEmpty(error)
                ? string.Format(L.T("生成诊断包失败：{0}"), "?")
                : error;
            ShowMessage(msg, MessageBoxImage.Warning);
        }

        private void ShowMessage(string text, MessageBoxImage icon)
        {
            if (_closed)
                MessageBox.Show(text, AppInfo.ProductName, MessageBoxButton.OK, icon);
            else
                MessageBox.Show(this, text, AppInfo.ProductName, MessageBoxButton.OK, icon);
        }

        private void OnClose(object sender, RoutedEventArgs e) { Close(); }
    }
}
