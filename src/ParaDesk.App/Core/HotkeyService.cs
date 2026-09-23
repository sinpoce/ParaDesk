using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
                CreateHandle(new CreateParams());
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

        private const int FirstId = 0xB000;
        private const int LastId = 0xBFFF;

        private const int ModifierMask = 0x000F;

        private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MapVirtualKeyW")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);
        private const uint MAPVK_VK_TO_CHAR = 2;

        private MessageWindow _window;
        private readonly Dictionary<int, string> _idToAction = new Dictionary<int, string>();
        private int _nextId = FirstId;
        private bool _disposed;

        private int _suspendDepth;
        private List<HotkeyBinding> _lastBindings;

        private static HotkeyService _current;

        public event EventHandler<HotkeyPressedEventArgs> Pressed;

        public HotkeyService()
        {
            _window = new MessageWindow(this);
            _current = this;
        }

        internal static HotkeyService Current { get { return _current; } }

        public void Suspend()
        {
            if (_disposed) return;
            if (_suspendDepth++ == 0)
            {
                UnregisterAll();
                Log.Info("录制热键期间暂停全局热键");
            }
        }

        public void Resume()
        {
            if (_suspendDepth == 0) return;
            if (--_suspendDepth == 0 && !_disposed)
            {
                Log.Info("恢复全局热键");
                Apply(_lastBindings);
            }
        }

        /// <summary>按设置重新注册全部热键。返回注册失败的动作列表（多半是被别的程序占用）。</summary>
        public List<string> Apply(IEnumerable<HotkeyBinding> bindings)
        {
            _lastBindings = bindings == null ? null : new List<HotkeyBinding>(bindings);
            UnregisterAll();
            var failed = new List<string>();
            if (bindings == null) return failed;
            if (_disposed || _window == null)
            {
                Log.Warn("热键服务已释放，忽略注册请求");
                return failed;
            }

            var taken = new Dictionary<long, string>();

            foreach (var b in bindings)
            {
                if (b == null || !b.Enabled || b.Key == 0) continue;

                int mods = b.Modifiers & ModifierMask;
                long combo = ((long)mods << 32) | (uint)b.Key;
                string owner;
                if (taken.TryGetValue(combo, out owner))
                {
                    failed.Add(b.Action);
                    Log.Warn("注册热键失败（与动作 " + owner + " 的组合重复）: " + Describe(b) + " -> " + b.Action);
                    continue;
                }

                if (_nextId > LastId || _nextId < FirstId) _nextId = FirstId;
                int id = _nextId++;
                // MOD_NOREPEAT：按住不放只触发一次
                uint fs = (uint)mods | NativeMethods.MOD_NOREPEAT;
                if (NativeMethods.RegisterHotKey(_window.Handle, id, fs, (uint)b.Key))
                {
                    _idToAction[id] = b.Action;
                    taken[combo] = b.Action;
                    Log.Info("注册热键成功: " + Describe(b) + " -> " + b.Action);
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    failed.Add(b.Action);
                    string why = err == ERROR_HOTKEY_ALREADY_REGISTERED
                        ? "已被其它程序占用"
                        : "错误码 " + err;
                    Log.Warn("注册热键失败（" + why + "）: " + Describe(b) + " -> " + b.Action);
                }
            }

            if (_suspendDepth > 0) UnregisterAll();
            return failed;
        }

        public static string Describe(HotkeyBinding b)
        {
            if (b == null) return "";
            return Describe(b.Modifiers, b.Key);
        }

        public static string Describe(int modifiers, int key)
        {
            string s = "";
            if ((modifiers & (int)NativeMethods.MOD_CONTROL) != 0) s += "Ctrl+";
            if ((modifiers & (int)NativeMethods.MOD_ALT) != 0) s += "Alt+";
            if ((modifiers & (int)NativeMethods.MOD_SHIFT) != 0) s += "Shift+";
            if ((modifiers & (int)NativeMethods.MOD_WIN) != 0) s += "Win+";
            return s + KeyName(key);
        }

        private static string KeyName(int key)
        {
            var k = (Keys)key & Keys.KeyCode;
            if (k >= Keys.D0 && k <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
            switch (k)
            {
                case Keys.Return: return "Enter";
                case Keys.Escape: return "Esc";
                case Keys.Back: return "Backspace";
                case Keys.Prior: return "PageUp";
                case Keys.Next: return "PageDown";
                case Keys.Capital: return "CapsLock";
                case Keys.Snapshot: return "PrintScreen";
            }

            int vk = (int)k;
            if ((vk >= 0xBA && vk <= 0xC0) || (vk >= 0xDB && vk <= 0xDF) || vk == 0xE2)
            {
                string ch = LayoutChar(vk);
                if (ch != null) return ch;
            }
            return k.ToString();
        }

        private static string LayoutChar(int vk)
        {
            try
            {
                uint r = MapVirtualKey((uint)vk, MAPVK_VK_TO_CHAR);
                char c = (char)(r & 0xFFFF);
                if (c <= 0x20 || char.IsControl(c)) return null;
                return char.ToUpperInvariant(c).ToString();
            }
            catch (Exception ex)
            {
                Log.Debug("按键盘布局取按键字符失败: vk=0x" + vk.ToString("X") + ": " + ex.Message);
                return null;
            }
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
            if (_window != null)
            {
                foreach (var id in _idToAction.Keys)
                {
                    try
                    {
                        if (!NativeMethods.UnregisterHotKey(_window.Handle, id))
                            Log.Debug("注销热键失败: id=0x" + id.ToString("X") + "，错误码 " + Marshal.GetLastWin32Error());
                    }
                    catch (Exception ex) { Log.Debug("注销热键异常: id=0x" + id.ToString("X") + ": " + ex.Message); }
                }
            }
            _idToAction.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (ReferenceEquals(_current, this)) _current = null;
            UnregisterAll();
            if (_window != null)
            {
                try { _window.DestroyHandle(); }
                catch (Exception ex) { Log.Debug("销毁热键消息窗口失败: " + ex.Message); }
                _window = null;
            }
        }
    }
}
