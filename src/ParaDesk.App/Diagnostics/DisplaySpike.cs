using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using ParaDesk.Core;
using ParaDesk.Rdp;

namespace ParaDesk.Diagnostics
{
    /// <summary>
    /// M1 技术验证：动态分辨率在子会话上到底能不能用。
    ///
    /// 微软文档没有明说 UpdateSessionDisplaySettings 支持子会话，只有
    /// Power Automate 画中画窗口可缩放这一间接证据。这里用一个小窗口真连一次，
    /// 依次试 UpdateSessionDisplaySettings / SyncSessionDisplaySettings /
    /// Reconnect 降级路径，把每一步的实际结果写进报告。
    ///
    /// 用法：ParaDesk.exe --spike
    /// 窗口只有 900×600，跑完自动关闭，不影响正在使用的桌面。
    /// </summary>
    internal static class DisplaySpike
    {
        private const int LoginTimeoutSeconds = 90;

        private const int SettleSeconds = 2;

        private static void Say(string msg)
        {
            Console.WriteLine(msg);
            Log.Info("[spike] " + msg);
        }

        public static int Run()
        {
            var env = SystemStatus.Check();
            if (!env.ReadyToStart)
            {
                Say("环境未就绪，无法验证: " + (env.NextAction ?? ""));
                return 2;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var form = new SpikeForm();
            Application.Run(form);

            string report = form.Report;
            Console.Write(report);
            Log.Info(report.TrimEnd());
            string path = Path.Combine(Log.Dir, "spike-display.log");
            try { File.WriteAllText(path, report, Encoding.UTF8); }
            catch (Exception ex) { Say("写入 " + path + " 失败: " + ex.Message); }
            return form.Success ? 0 : 1;
        }

        private sealed class SpikeForm : Form
        {
            private RdpHostControl _host;
            private RdpEventSink _sink;
            private dynamic _ocx;
            private readonly Timer _timer;
            private readonly StringBuilder _sb = new StringBuilder();
            private readonly List<string> _steps = new List<string>();
            private int _phase;
            private int _ticks;
            private bool _loggedIn;
            private bool _closing;

            public string Report { get { return _sb.ToString(); } }
            public bool Success { get; private set; }

            public SpikeForm()
            {
                Text = "ParaDesk 显示能力验证（跑完自动关闭）";
                FormBorderStyle = FormBorderStyle.FixedSingle;
                MaximizeBox = false;
                StartPosition = FormStartPosition.Manual;
                ClientSize = new Size(900, 600);

                // 放到非主屏，尽量不打扰正在使用的桌面
                var target = MonitorService.DefaultTarget();
                if (target != null)
                    Location = new Point(target.Bounds.X + 80, target.Bounds.Y + 80);

                _sb.AppendLine("=== ParaDesk 动态分辨率验证 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
                _sb.AppendLine("目标：确认子会话是否支持 UpdateSessionDisplaySettings（M1 的载重问题）");
                _sb.AppendLine();

                _timer = new Timer { Interval = 1000 };
                _timer.Tick += OnTick;
                Shown += OnShownFirst;
                FormClosing += delegate
                {
                    _timer.Stop();
                    try { if (_ocx != null) _ocx.Disconnect(); }
                    catch (Exception ex) { Log.Debug("spike: 关闭时断开连接失败: " + Brief(ex)); }
                };
            }

            private void OnShownFirst(object sender, EventArgs e)
            {
                Shown -= OnShownFirst;
                try
                {
                    _sink = new RdpEventSink();
                    _sink.Connected += delegate { Step("OnConnected 事件已收到", true); };
                    _sink.LoginComplete += delegate
                    {
                        Step("OnLoginComplete 事件已收到", true);
                        _loggedIn = true;
                        _ticks = 0;
                        _phase = 1;
                    };
                    _sink.Disconnected += delegate(object s2, RdpDisconnectedEventArgs a)
                    {
                        Step("连接断开 discReason=0x" + a.DiscReason.ToString("X"), _phase >= 5);
                        Finish();
                    };
                    _sink.RemoteSizeChanged += delegate(object s2, RdpSizeChangedEventArgs a)
                    {
                        Step("远端上报新尺寸 " + a.Width + "×" + a.Height, true);
                    };

                    _host = new RdpHostControl { Dock = DockStyle.Fill, Sink = _sink };
                    Controls.Add(_host);
                    _host.CreateControl();
                    _ocx = _host.Ocx;

                    _ocx.Server = "localhost";
                    _ocx.DesktopWidth = 1280;
                    _ocx.DesktopHeight = 800;
                    _ocx.ColorDepth = 32;

                    dynamic adv = null;
                    try { adv = _ocx.AdvancedSettings9; }
                    catch (Exception ex) { Note("取 AdvancedSettings9 失败，以下高级设置全部未生效: " + Brief(ex)); }
                    if (adv != null)
                    {
                        try { adv.EnableCredSspSupport = true; }
                        catch (Exception ex) { Note("EnableCredSspSupport=true 未生效: " + Brief(ex)); }
                        try { adv.AuthenticationLevel = 0; }
                        catch (Exception ex) { Note("AuthenticationLevel=0 未生效: " + Brief(ex)); }
                        try { adv.DisplayConnectionBar = false; }
                        catch (Exception ex) { Note("DisplayConnectionBar=false 未生效: " + Brief(ex)); }
                        try { adv.SmartSizing = true; }
                        catch (Exception ex) { Note("SmartSizing=true 未生效: " + Brief(ex)); }
                    }

                    var ext = (IMsRdpExtendedSettings)_host.Ocx;
                    object vTrue = true;
                    ext.set_Property("ConnectToChildSession", ref vTrue);

                    Step("控件已配置（ConnectToChildSession=true），开始连接", true);
                    _ocx.Connect();
                    _timer.Start();
                }
                catch (Exception ex)
                {
                    Step("初始化失败: " + ex.Message, false);
                    Finish();
                }
            }

            private void OnTick(object sender, EventArgs e)
            {
                _ticks++;

                if (!_loggedIn)
                {
                    if (_ticks > LoginTimeoutSeconds)
                    {
                        Step("等待登录超时（" + LoginTimeoutSeconds + " 秒）", false);
                        Finish();
                    }
                    return;
                }

                if (_phase == 1 && _ticks < SettleSeconds) return;

                switch (_phase)
                {
                    case 1:
                        TryUpdate("测试 A：改分辨率 1600×1000 @100%", 1600, 1000, 100, 100);
                        break;
                    case 2:
                        TryUpdate("测试 B：改分辨率 1920×1200 @150%", 1920, 1200, 150, 140);
                        break;
                    case 3:
                        TryUpdate("测试 C：奇数宽度 1601（应被规整为偶数）", 1601, 900, 100, 100);
                        break;
                    case 4:
                        TrySync();
                        break;
                    case 5:
                        TryReconnectFallback();
                        break;
                    default:
                        Finish();
                        return;
                }
                _phase++;
                _ticks = 0;
            }

            private void TryUpdate(string title, int w, int h, uint desktopScale, uint deviceScale)
            {
                int width = (w % 2 == 0) ? w : w - 1;
                try
                {
                    _ocx.UpdateSessionDisplaySettings(
                        (uint)width, (uint)h, 0u, 0u, 0u, desktopScale, deviceScale);
                    Step(title + " -> UpdateSessionDisplaySettings 成功（实际宽 " + width + "）", true);
                }
                catch (Exception ex)
                {
                    Step(title + " -> 失败: " + Brief(ex), false);
                }
            }

            private void TrySync()
            {
                try
                {
                    _ocx.SyncSessionDisplaySettings();
                    Step("测试 D：SyncSessionDisplaySettings（贴齐窗口）成功", true);
                }
                catch (Exception ex)
                {
                    Step("测试 D：SyncSessionDisplaySettings 失败: " + Brief(ex), false);
                }
            }

            private void TryReconnectFallback()
            {
                try
                {
                    int status = Convert.ToInt32(_ocx.Reconnect(1280u, 800u));
                    // 0 = controlReconnectStarted, 1 = controlReconnectBlocked
                    Step("测试 E：降级路径 Reconnect(1280,800) 返回 " + status +
                         (status == 0 ? "（已开始重连）" : "（被拒绝）"), status == 0);
                }
                catch (Exception ex)
                {
                    Step("测试 E：Reconnect 失败: " + Brief(ex), false);
                }
            }

            private void Step(string text, bool ok)
            {
                string line = (ok ? "[ OK ] " : "[FAIL] ") + text;
                _steps.Add(line);
                _sb.AppendLine(line);
                Log.Info("spike: " + line);
            }

            private void Note(string text)
            {
                string line = "[WARN] " + text;
                _sb.AppendLine(line);
                Log.Warn("spike: " + line);
            }

            private void Finish()
            {
                if (_closing) return;
                _closing = true;
                _timer.Stop();

                int ok = 0, fail = 0;
                foreach (var s in _steps) { if (s.StartsWith("[ OK ]")) ok++; else fail++; }

                _sb.AppendLine();
                _sb.AppendLine("--- 结论 ---");
                _sb.AppendLine("成功 " + ok + " 项，失败 " + fail + " 项");

                bool updateWorks = false;
                foreach (var s in _steps)
                    if (s.StartsWith("[ OK ]") && s.Contains("UpdateSessionDisplaySettings 成功")) updateWorks = true;

                if (updateWorks)
                {
                    _sb.AppendLine("✔ 子会话支持 UpdateSessionDisplaySettings —— M1 可按主路径实现，");
                    _sb.AppendLine("  即不断线动态改分辨率，Reconnect 仅作降级备用。");
                    Success = true;
                }
                else
                {
                    _sb.AppendLine("✘ 子会话不支持 UpdateSessionDisplaySettings —— M1 必须改用");
                    _sb.AppendLine("  IMsRdpClient8::Reconnect(w,h) 作为主路径（会短暂断线重连）。");
                    Success = false;
                }

                try { if (!IsDisposed) Close(); }
                catch (Exception ex) { Log.Debug("spike: 关闭验证窗口失败: " + Brief(ex)); }
            }

            private static string Brief(Exception ex)
            {
                string m = ex.Message;
                int nl = m.IndexOf('\n');
                if (nl > 0) m = m.Substring(0, nl);
                return ex.GetType().Name + ": " + m.Trim();
            }
        }
    }
}
