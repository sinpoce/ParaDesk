using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace ParaDesk.Core
{
    internal static class SingleInstance
    {
        private const string PipeName = "ParaDesk.Activate.v1";

        private const int MaxInstances = 8;

        private const int HandlerTimeoutMs = 125000;

        private const int RequestReadTimeoutMs = 10000;

        private const int MaxLineBytes = 1024 * 1024;

        private const int DrainOnStopMs = 2000;

        private const int HandlerAttachWaitMs = 10000;

        private const int WriteTimeoutMs = 10000;

        private const int BusyConnectWaitMs = 10000;

        private const int ConnectSliceMs = 500;

        private const int PollIntervalMs = 100;

        private const int BusyRetryMs = 200;

        private const int ERROR_FILE_NOT_FOUND = 2;

        private const int HR_ERROR_PIPE_BUSY = unchecked((int)0x800700E7);

        [DllImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WaitNamedPipe(string lpNamedPipeName, uint nTimeOut);

        private static Thread _listener;
        private static volatile bool _running;

        private static volatile NamedPipeServerStream _waiting;

        private static int _unsentReplies;

        private static readonly object _handlerGate = new object();
        private static bool _pendingActivate;

        private static bool _ownerReadFailureLogged;

        public static event Action ActivateRequested;

        public static event Action<ControlRequest, Action<ControlResponse>> CommandReceived;

        public static void StartListener()
        {
            if (_running) return;
            _running = true;

            var t = new Thread(ListenLoop);
            t.IsBackground = true;
            t.Name = "ParaDesk-SingleInstance";
            _listener = t;
            t.Start();
        }

        public static void AttachActivate(Action handler)
        {
            if (handler == null) return;
            bool pending;
            lock (_handlerGate)
            {
                ActivateRequested += handler;
                pending = _pendingActivate;
                _pendingActivate = false;
            }
            if (pending)
            {
                Log.Info("补发启动期间收到的激活请求");
                handler();
            }
        }

        private static void ListenLoop()
        {
            PipeSecurity security = null;
            try { security = CreateSecurity(); }
            catch (Exception ex) { Log.Warn("构造管道访问控制失败，改用系统默认权限: " + ex.Message); }

            string lastError = null;
            int failures = 0;
            bool squatWarned = false;
            while (_running)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, MaxInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);

                    string owner;
                    if (!ServerOwnerAcceptable(server, out owner))
                    {
                        server.Dispose();
                        server = null;
                        if (!squatWarned)
                        {
                            Log.Warn("命令管道名已被其他账户的程序占用（所有者 " + owner + "），本实例不在上面接收命令");
                            squatWarned = true;
                        }
                        Thread.Sleep(5000);
                        continue;
                    }

                    _waiting = server;
                    server.WaitForConnection();
                    _waiting = null;
                    if (!_running)
                    {
                        server.Dispose();
                        return;
                    }

                    failures = 0;
                    lastError = null;
                    NamedPipeServerStream accepted = server;
                    server = null;
                    StartServeThread(accepted);
                }
                catch (Exception ex)
                {
                    _waiting = null;
                    if (server != null)
                    {
                        try { server.Dispose(); } catch (Exception ex2) { Log.Debug("关闭管道实例失败: " + ex2.Message); }
                    }
                    if (!_running) return;

                    bool busy = ex is IOException && ex.HResult == HR_ERROR_PIPE_BUSY;
                    if (!busy) failures++;

                    string msg = (ex.Message ?? "").Trim();
                    if (msg != lastError)
                    {
                        Log.Debug("单实例监听异常: " + msg);
                        lastError = msg;
                    }
                    Thread.Sleep(busy ? BusyRetryMs : (failures < 20 ? 500 : 5000));
                }
            }
        }

        private static void StartServeThread(NamedPipeServerStream accepted)
        {
            try
            {
                var t = new Thread(() => Serve(accepted));
                t.IsBackground = true;
                t.Name = "ParaDesk-Command";
                t.Start();
            }
            catch
            {
                accepted.Dispose();
                throw;
            }
        }

        private static PipeSecurity CreateSecurity()
        {
            var ps = new PipeSecurity();
            using (var id = WindowsIdentity.GetCurrent())
            {
                ps.AddAccessRule(new PipeAccessRule(id.User, PipeAccessRights.FullControl, AccessControlType.Allow));
            }
            ps.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            return ps;
        }

        private static bool ServerOwnerAcceptable(PipeStream server, out string owner)
        {
            owner = "?";
            try { return IsTrustedOwner(server, out owner); }
            catch (Exception ex)
            {
                if (!_ownerReadFailureLogged)
                {
                    _ownerReadFailureLogged = true;
                    Log.Debug("读取命令管道所有者失败，照常服务: " + ex.Message);
                }
                return true;
            }
        }

        private static void Serve(NamedPipeServerStream server)
        {
            bool counted = false;
            try
            {
                using (server)
                {
                    bool timedOut;
                    string line = ReadLine(server, RequestReadTimeoutMs, out timedOut);
                    if (line == null)
                    {
                        if (timedOut) Log.Debug("单实例连接在限定时间内没有发来指令，已断开");
                        return;
                    }
                    line = line.Trim().TrimStart('\uFEFF');

                    if (line == "activate")
                    {
                        RaiseActivate();
                        return;
                    }
                    if (!line.StartsWith("{", StringComparison.Ordinal))
                    {
                        Log.Debug("忽略无法识别的单实例指令");
                        return;
                    }

                    ControlResponse resp = Dispatch(line, out counted);
                    WriteLine(server, ControlProtocol.ToJson(resp), WriteTimeoutMs);
                    try { server.WaitForPipeDrain(); }
                    catch (Exception ex) { Log.Debug("等待客户端读取应答失败: " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("处理单实例连接失败: " + ex.Message);
            }
            finally
            {
                if (counted) Interlocked.Decrement(ref _unsentReplies);
            }
        }

        private static void RaiseActivate()
        {
            Action h;
            lock (_handlerGate)
            {
                h = ActivateRequested;
                if (h == null) _pendingActivate = true;
            }
            if (h != null) h();
            else Log.Info("主实例还在启动，激活请求留到启动完成后处理");
        }

        private static ControlResponse Dispatch(string line, out bool counted)
        {
            counted = false;

            ControlRequest req = null;
            try { req = ControlProtocol.FromJson<ControlRequest>(line); }
            catch (Exception ex) { Log.Debug("解析控制命令失败: " + ex.Message); }
            if (req == null || string.IsNullOrEmpty(req.Command))
                return ControlResponse.Fail(ExitCodes.Usage, L.T("命令格式无法识别（命令行与正在运行的程序版本可能不一致）。"));

            if (!_running)
                return ControlResponse.Fail(ExitCodes.Failed, L.T("ParaDesk 正在退出，没有执行这条命令。"));

            var h = WaitForCommandHandler();
            if (!_running)
                return ControlResponse.Fail(ExitCodes.Failed, L.T("ParaDesk 正在退出，没有执行这条命令。"));
            if (h == null)
            {
                Log.Info("主实例还没准备好接收命令（启动未完成），已拒绝: " + req.Command);
                return ControlResponse.Fail(ExitCodes.Failed, L.T("ParaDesk 还没准备好接收命令，请稍后再试。"));
            }

            Log.Info("收到命令行控制命令: " + req.Command);
            var pending = new PendingReply();
            try { h(req, pending.Reply); }
            catch (Exception ex)
            {
                Log.Error("处理命令行控制命令出错: " + req.Command, ex);
                pending.Reply(ControlResponse.Fail(ExitCodes.Failed, ex.Message));
            }

            ControlResponse r = pending.Wait(HandlerTimeoutMs, out counted);
            if (r == null)
            {
                Log.Warn("命令行控制命令超时未应答: " + req.Command);
                return ControlResponse.Fail(ExitCodes.Failed, L.T("ParaDesk 处理这条命令超时。"));
            }
            return r;
        }

        private static Action<ControlRequest, Action<ControlResponse>> WaitForCommandHandler()
        {
            var h = Volatile.Read(ref CommandReceived);
            int waited = 0;
            while (h == null && _running && waited < HandlerAttachWaitMs)
            {
                Thread.Sleep(PollIntervalMs);
                waited += PollIntervalMs;
                h = Volatile.Read(ref CommandReceived);
            }
            return h;
        }

        private sealed class PendingReply
        {
            private readonly object _gate = new object();
            private readonly ManualResetEvent _done = new ManualResetEvent(false);
            private ControlResponse _result;
            private bool _closed;

            public void Reply(ControlResponse r)
            {
                lock (_gate)
                {
                    if (_closed || _result != null) return;
                    _result = r ?? ControlResponse.Fail(ExitCodes.Failed, L.T("ParaDesk 没有给出结果。"));
                    Interlocked.Increment(ref _unsentReplies);
                    _done.Set();
                }
            }

            public ControlResponse Wait(int timeoutMs, out bool counted)
            {
                _done.WaitOne(timeoutMs);
                lock (_gate)
                {
                    _closed = true;
                    counted = _result != null;
                    _done.Close();
                    return _result;
                }
            }
        }

        public static void Stop()
        {
            _running = false;
            Thread t = Interlocked.Exchange(ref _listener, null);
            if (t == null) return;

            bool woke = false;
            try
            {
                // 自连一次把 WaitForConnection 唤醒，让监听线程干净退出
                using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.None,
                    TokenImpersonationLevel.Identification))
                {
                    c.Connect(200);
                    woke = true;
                }
            }
            catch (Exception ex) { Log.Debug("唤醒单实例监听线程失败: " + ex.Message); }

            if (!woke)
            {
                var w = _waiting;
                if (w != null)
                {
                    try { w.Dispose(); } catch (Exception ex) { Log.Debug("关闭等待中的管道实例失败: " + ex.Message); }
                }
            }

            if (t != Thread.CurrentThread)
            {
                try { t.Join(1000); } catch (Exception ex) { Log.Debug("等待监听线程退出失败: " + ex.Message); }
            }

            int waited = 0;
            while (Thread.VolatileRead(ref _unsentReplies) > 0 && waited < DrainOnStopMs)
            {
                Thread.Sleep(20);
                waited += 20;
            }
        }

        public static bool NotifyExisting()
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous,
                    TokenImpersonationLevel.Identification))
                {
                    client.Connect(2000);
                    string owner;
                    if (!IsTrustedOwner(client, out owner))
                    {
                        Log.Warn("命令管道的所有者不是当前用户（" + owner + "），不发送激活请求");
                        return false;
                    }
                    WriteLine(client, "activate", WriteTimeoutMs);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("通知已有实例失败: " + ex.Message);
                return false;
            }
        }

        public static ControlResponse SendCommand(ControlRequest req, int connectTimeoutMs, int replyTimeoutMs, out bool connected)
        {
            connected = false;
            if (req == null) throw new ArgumentNullException("req");

            NamedPipeClientStream client = null;
            try
            {
                ControlResponse early;
                client = ConnectForCommand(connectTimeoutMs, out early);
                if (client == null)
                {
                    connected = early != null;
                    return early;
                }
                connected = true;

                string owner;
                bool trusted;
                try { trusted = IsTrustedOwner(client, out owner); }
                catch (Exception ex)
                {
                    trusted = false;
                    owner = "?（" + ex.Message + "）";
                }
                if (!trusted)
                {
                    Log.Warn("命令管道的所有者不是当前用户（" + owner + "），拒绝发送命令: " + req.Command);
                    return ControlResponse.Fail(ExitCodes.Failed, L.T("命令管道被其他账户的程序占用，已拒绝发送命令。"));
                }

                try
                {
                    WriteLine(client, ControlProtocol.ToJson(req), WriteTimeoutMs);
                }
                catch (TimeoutException)
                {
                    Log.Debug("向主实例写命令超时: " + req.Command);
                    return ControlResponse.Fail(ExitCodes.Failed, L.T("等待 ParaDesk 应答超时。"));
                }

                bool timedOut;
                string line = ReadLine(client, replyTimeoutMs, out timedOut);
                if (timedOut)
                    return ControlResponse.Fail(ExitCodes.Failed, L.T("等待 ParaDesk 应答超时。"));
                if (line == null)
                {
                    Log.Debug("主实例没有应答就断开了连接: " + req.Command);
                    return ControlResponse.Fail(ExitCodes.Failed,
                        L.T("ParaDesk 在处理这条命令时断开了连接（可能已退出或崩溃），详见日志。"));
                }

                ControlResponse resp = ControlProtocol.FromJson<ControlResponse>(line.Trim());
                if (resp == null)
                    return ControlResponse.Fail(ExitCodes.Failed, L.T("无法解析 ParaDesk 的应答。"));
                return resp;
            }
            catch (Exception ex)
            {
                Log.Debug("发送命令行控制命令失败: " + ex.Message);
                if (!connected) return null;
                return ControlResponse.Fail(ExitCodes.Failed, L.T("与 ParaDesk 通信失败：") + ex.Message);
            }
            finally
            {
                if (client != null) client.Dispose();
            }
        }

        private static NamedPipeClientStream ConnectForCommand(int connectTimeoutMs, out ControlResponse early)
        {
            early = null;
            int start = Environment.TickCount;
            string lastError = null;

            while (true)
            {
                bool mutex = InstanceMutexExists();
                bool pipe = PipeExists();
                if (!pipe && !mutex) return null;

                int budget = mutex ? Math.Max(connectTimeoutMs, BusyConnectWaitMs) : connectTimeoutMs;
                int left = budget - unchecked(Environment.TickCount - start);
                if (left <= 0) break;

                if (!pipe)
                {
                    Thread.Sleep(Math.Min(PollIntervalMs, left));
                    continue;
                }

                var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                    TokenImpersonationLevel.Identification);
                try
                {
                    client.Connect(Math.Min(left, ConnectSliceMs));
                    return client;
                }
                catch (TimeoutException)
                {
                    client.Dispose();
                    lastError = "连接超时";
                }
                catch (UnauthorizedAccessException ex)
                {
                    client.Dispose();
                    if (CanConnectWriteOnly())
                    {
                        early = ControlResponse.Fail(ExitCodes.Failed, OldVersionMessage());
                        return null;
                    }
                    Log.Debug("连接主实例被拒绝: " + ex.Message);
                    return null;
                }
                catch (IOException ex)
                {
                    client.Dispose();
                    lastError = (ex.Message ?? "").Trim();
                }
            }

            if (InstanceMutexExists())
            {
                Log.Debug("主实例在运行但连不上命令管道: " + (lastError ?? "管道不存在"));
                early = ControlResponse.Fail(ExitCodes.Failed,
                    L.T("ParaDesk 正在运行，但暂时接收不了命令（正忙、正在启动或正在退出），请稍后重试。"));
            }
            else if (lastError != null)
            {
                Log.Debug("连接主实例失败: " + lastError);
            }
            return null;
        }

        private static bool IsTrustedOwner(PipeStream s, out string owner)
        {
            owner = "?";
            var sid = s.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (sid == null) return false;
            owner = sid.Value;
            using (var me = WindowsIdentity.GetCurrent())
            {
                if (sid.Equals(me.User)) return true;
            }
            return sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
                   sid.IsWellKnown(WellKnownSidType.LocalSystemSid);
        }

        private static string OldVersionMessage()
        {
            return L.T("正在运行的 ParaDesk 是不支持命令行控制的旧版本，请先退出它（托盘图标 → 退出）再重新打开。");
        }

        private static bool PipeExists()
        {
            try
            {
                if (WaitNamedPipe(@"\\.\pipe\" + PipeName, 1)) return true;
                return Marshal.GetLastWin32Error() != ERROR_FILE_NOT_FOUND;
            }
            catch (Exception ex)
            {
                Log.Debug("探测管道失败: " + ex.Message);
                return true;
            }
        }

        internal static bool WaitForInstanceExit(int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (InstanceMutexExists())
            {
                if (sw.ElapsedMilliseconds >= timeoutMs) return false;
                Thread.Sleep(100);
            }
            return true;
        }

        private static bool InstanceMutexExists()
        {
            foreach (string name in new[] { AppInfo.MutexNameGlobal, AppInfo.MutexNameLocal })
            {
                try
                {
                    Mutex m;
                    if (Mutex.TryOpenExisting(name, MutexRights.Synchronize, out m))
                    {
                        m.Dispose();
                        return true;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Debug("探测单实例互斥量失败: " + ex.Message);
                }
            }
            return false;
        }

        private static bool CanConnectWriteOnly()
        {
            try
            {
                using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.None,
                    TokenImpersonationLevel.Identification))
                {
                    c.Connect(500);
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void WriteLine(PipeStream s, string line, int timeoutMs)
        {
            byte[] data = new UTF8Encoding(false).GetBytes((line ?? "") + "\n");
            IAsyncResult ar = s.BeginWrite(data, 0, data.Length, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) throw new TimeoutException();
            s.EndWrite(ar);
        }

        private static string ReadLine(PipeStream s, int timeoutMs, out bool timedOut)
        {
            timedOut = false;
            var acc = new MemoryStream();
            var buf = new byte[4096];
            bool any = false;
            int start = Environment.TickCount;

            while (true)
            {
                int left = timeoutMs - unchecked(Environment.TickCount - start);
                if (left <= 0)
                {
                    timedOut = true;
                    return null;
                }

                IAsyncResult ar = s.BeginRead(buf, 0, buf.Length, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(left))
                {
                    timedOut = true;
                    return null;
                }
                int n = s.EndRead(ar);
                if (n <= 0) break;

                any = true;
                int nl = Array.IndexOf(buf, (byte)'\n', 0, n);
                if (nl >= 0)
                {
                    acc.Write(buf, 0, nl);
                    break;
                }
                acc.Write(buf, 0, n);
                if (acc.Length > MaxLineBytes) throw new InvalidDataException("line too long");
            }

            if (!any) return null;
            return Encoding.UTF8.GetString(acc.ToArray()).TrimEnd('\r');
        }
    }
}
