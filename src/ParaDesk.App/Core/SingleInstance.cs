using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using ParaDesk.Core;

namespace ParaDesk.Core
{
    /// <summary>
    /// 单实例协调。
    ///
    /// 之前的做法是发现已有实例就弹一句"已经在运行"然后退出——这其实是把
    /// 用户的意图（我要打开这个程序）当成错误来处理。正确行为是把已有窗口唤到前台，
    /// 就像所有正经的单实例应用那样。这里用命名管道向已有实例发一条指令。
    /// </summary>
    internal static class SingleInstance
    {
        private const string PipeName = "ParaDesk.Activate.v1";
        private static Thread _listener;
        private static volatile bool _running;

        /// <summary>已有实例请求激活。</summary>
        public static event Action ActivateRequested;

        /// <summary>作为主实例开始监听激活请求。</summary>
        public static void StartListener()
        {
            if (_running) return;
            _running = true;

            _listener = new Thread(ListenLoop);
            _listener.IsBackground = true;
            _listener.Name = "ParaDesk-SingleInstance";
            _listener.Start();
        }

        private static void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    // 每次只服务一个连接，处理完重建——比长连接简单且不会卡住
                    using (var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        server.WaitForConnection();
                        if (!_running) return;

                        using (var reader = new StreamReader(server, Encoding.UTF8))
                        {
                            string cmd = reader.ReadLine();
                            if (cmd == "activate")
                            {
                                var h = ActivateRequested;
                                if (h != null) h();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!_running) return;
                    Log.Debug("单实例监听异常: " + ex.Message);
                    Thread.Sleep(500);
                }
            }
        }

        /// <summary>
        /// 通知已有实例把窗口唤到前台。
        /// 返回 true 表示送达；false 表示对方没在监听（可能正在退出）。
        /// </summary>
        public static bool NotifyExisting()
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    client.Connect(2000);
                    using (var w = new StreamWriter(client, new UTF8Encoding(false)))
                    {
                        w.WriteLine("activate");
                        w.Flush();
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("通知已有实例失败: " + ex.Message);
                return false;
            }
        }

        public static void Stop()
        {
            _running = false;
            try
            {
                // 自连一次把 WaitForConnection 唤醒，让监听线程干净退出
                using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                    c.Connect(200);
            }
            catch { }
        }
    }
}
