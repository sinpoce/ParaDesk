using System;
using System.Collections.Generic;
using System.Windows.Forms;
using ParaDesk.Native;

namespace ParaDesk.Core
{
    internal class HotkeyPressedEventArgs : EventArgs
    {
        public string Action { get; private set; }
        public HotkeyPressedEventArgs(string action) { Action = action; }
    }

    /// <summary>
    /// 全局热键。用隐藏消息窗口而非主窗体：主窗体可能被最小化到托盘甚至尚未创建，
    /// 热键必须始终有效。
    /// </summary>
    internal class HotkeyService : IDisposable
    {
        private sealed class MessageWindow : NativeWindow
        {
            private readonly HotkeyService _owner;
            public MessageWindow(HotkeyService owner)
            {
                _owner = owner;
                CreateHandle(new CreateParams());   // message-only 风格的隐藏窗口
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == NativeMethods.WM_HOTKEY)
                {
                    _owner.OnHotkey((int)m.WParam);
                }
                base.WndProc(ref m);
            }
        }

        private MessageWindow _window;
        private readonly Dictionary<int, string> _idToAction = new Dictionary<int, string>();
        private int _nextId = 0xB000;   // 避开常见占用区间
        private bool _disposed;

        public event EventHandler<HotkeyPressedEventArgs> Pressed;

        public HotkeyService()
        {
            _window = new MessageWindow(this);
        }

        /// <summary>按设置重新注册全部热键。返回注册失败的动作列表（多半是被别的程序占用）。</summary>
        public List<string> Apply(IEnumerable<HotkeyBinding> bindings)
        {
            UnregisterAll();
            var failed = new List<string>();
            if (bindings == null) return failed;

            foreach (var b in bindings)
            {
                if (b == null || !b.Enabled || b.Key == 0) continue;

                int id = _nextId++;
                // MOD_NOREPEAT：按住不放只触发一次
                uint mods = (uint)b.Modifiers | NativeMethods.MOD_NOREPEAT;
                if (NativeMethods.RegisterHotKey(_window.Handle, id, mods, (uint)b.Key))
                {
                    _idToAction[id] = b.Action;
                    Log.Info("注册热键成功: " + Describe(b) + " -> " + b.Action);
                }
                else
                {
                    failed.Add(b.Action);
                    Log.Warn("注册热键失败（可能被占用）: " + Describe(b) + " -> " + b.Action);
                }
            }
            return failed;
        }

        public static string Describe(HotkeyBinding b)
        {
            if (b == null) return "";
            string s = "";
            if ((b.Modifiers & (int)NativeMethods.MOD_CONTROL) != 0) s += "Ctrl+";
            if ((b.Modifiers & (int)NativeMethods.MOD_ALT) != 0) s += "Alt+";
            if ((b.Modifiers & (int)NativeMethods.MOD_SHIFT) != 0) s += "Shift+";
            if ((b.Modifiers & (int)NativeMethods.MOD_WIN) != 0) s += "Win+";
            return s + ((Keys)b.Key);
        }

        private void OnHotkey(int id)
        {
            string action;
            if (!_idToAction.TryGetValue(id, out action)) return;
            var h = Pressed;
            if (h != null)
            {
                try { h(this, new HotkeyPressedEventArgs(action)); }
                catch (Exception ex) { Log.Error("热键处理异常: " + action, ex); }
            }
        }

        private void UnregisterAll()
        {
            if (_window == null) return;
            foreach (var id in _idToAction.Keys)
            {
                try { NativeMethods.UnregisterHotKey(_window.Handle, id); }
                catch { }
            }
            _idToAction.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            UnregisterAll();
            if (_window != null)
            {
                try { _window.DestroyHandle(); }
                catch { }
                _window = null;
            }
        }
    }
}
