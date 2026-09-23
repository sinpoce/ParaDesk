using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ParaDesk.Core
{
    internal static class CliOutput
    {
        private const int STD_OUTPUT_HANDLE = -11;
        private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
        private const uint FILE_TYPE_DISK = 0x0001;
        private const uint FILE_TYPE_CHAR = 0x0002;
        private const uint FILE_TYPE_PIPE = 0x0003;
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint OPEN_EXISTING = 3;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetFileType(IntPtr hFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", EntryPoint = "WriteConsoleW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WriteConsole(SafeFileHandle hConsoleOutput,
            [MarshalAs(UnmanagedType.LPWStr)] string lpBuffer, uint nNumberOfCharsToWrite,
            out uint lpNumberOfCharsWritten, IntPtr lpReserved);

        private static readonly object Sync = new object();
        private static TextWriter _writer;
        private static bool _attached;
        private static bool _hasChannel;
        private static int _depth;

        public static bool HasChannel
        {
            get { lock (Sync) { return _writer != null && _hasChannel; } }
        }

        public static void Begin()
        {
            lock (Sync)
            {
                _depth++;
                if (_depth > 1) return;

                try { Open(); }
                catch (Exception ex)
                {
                    Log.Debug("打开命令行输出通道失败: " + ex.Message);
                    _writer = TextWriter.Null;
                    _hasChannel = false;
                }

                try
                {
                    Console.SetOut(_writer);
                    Console.SetError(_writer);
                }
                catch (Exception ex) { Log.Debug("接管 Console 输出失败: " + ex.Message); }
            }
        }

        public static void End()
        {
            lock (Sync)
            {
                if (_depth == 0) return;
                _depth--;
                if (_depth > 0) return;

                try { if (_writer != null) _writer.Flush(); }
                catch (Exception ex) { Log.Debug("刷新命令行输出失败: " + ex.Message); }

                var cw = _writer as ConsoleWriter;
                if (cw != null)
                {
                    try { cw.Dispose(); } catch (Exception ex) { Log.Debug("关闭控制台句柄失败: " + ex.Message); }
                }
                if (_attached)
                {
                    try { FreeConsole(); } catch (Exception ex) { Log.Debug("FreeConsole 失败: " + ex.Message); }
                }
                _attached = false;
                _writer = null;
                _hasChannel = false;
            }
        }

        public static void Write(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Current.Write(text); }
            catch (Exception ex) { Log.Debug("命令行输出失败: " + ex.Message); }
        }

        public static void WriteLine(string text)
        {
            try { Current.WriteLine(text ?? ""); }
            catch (Exception ex) { Log.Debug("命令行输出失败: " + ex.Message); }
        }

        public static void WriteLine()
        {
            WriteLine("");
        }

        private static TextWriter Current
        {
            get
            {
                lock (Sync) { return _writer ?? Console.Out; }
            }
        }

        private static void Open()
        {
            _writer = null;
            _attached = false;
            _hasChannel = false;

            IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
            uint type = (h == IntPtr.Zero || h == new IntPtr(-1)) ? 0u : GetFileType(h);

            if (type == FILE_TYPE_DISK || type == FILE_TYPE_PIPE)
            {
                UseStandardStream();
                return;
            }

            bool attached = AttachConsole(ATTACH_PARENT_PROCESS);

            if (type == FILE_TYPE_CHAR)
            {
                uint mode;
                if (attached && GetConsoleMode(h, out mode))
                {
                    _attached = true;
                    UseConsole();
                    return;
                }
                if (attached) FreeConsole();
                UseStandardStream();
                return;
            }

            if (attached)
            {
                _attached = true;
                UseConsole();
                return;
            }

            _writer = TextWriter.Null;
        }

        private static void UseStandardStream()
        {
            var w = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            w.AutoFlush = true;
            _writer = w;
            _hasChannel = true;
        }

        private static void UseConsole()
        {
            SafeFileHandle con = CreateFile("CONOUT$", GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (con == null || con.IsInvalid)
            {
                Log.Debug("打开 CONOUT$ 失败: " + Marshal.GetLastWin32Error());
                if (con != null) con.Dispose();
                _writer = TextWriter.Null;
                return;
            }

            var w = new ConsoleWriter(con);
            w.Write("\r\n");
            _writer = w;
            _hasChannel = true;
        }

        private sealed class ConsoleWriter : TextWriter
        {
            private const int Chunk = 8192;
            private readonly SafeFileHandle _h;

            public ConsoleWriter(SafeFileHandle h)
            {
                _h = h;
                CoreNewLine = "\r\n".ToCharArray();
            }

            public override Encoding Encoding { get { return Encoding.Unicode; } }

            public override void Write(char value)
            {
                Write(new string(value, 1));
            }

            public override void Write(char[] buffer, int index, int count)
            {
                if (buffer == null || count <= 0) return;
                Write(new string(buffer, index, count));
            }

            public override void Write(string value)
            {
                if (string.IsNullOrEmpty(value) || _h.IsInvalid || _h.IsClosed) return;
                int pos = 0;
                while (pos < value.Length)
                {
                    int n = Math.Min(Chunk, value.Length - pos);
                    if (n > 1 && pos + n < value.Length && char.IsHighSurrogate(value[pos + n - 1])) n--;
                    uint written;
                    if (!WriteConsole(_h, value.Substring(pos, n), (uint)n, out written, IntPtr.Zero)) return;
                    pos += n;
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _h.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
