using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ParaDesk.Core;
using ParaDesk.Native;

namespace ParaDesk.Shell
{
    /// <summary>
    /// 热键录制框：聚焦后直接按组合键即可绑定。
    /// 比让用户从下拉框里挑修饰键+主键直观得多，也是系统设置里的通行做法。
    /// </summary>
    internal class HotkeyBox : global::Wpf.Ui.Controls.TextBox
    {
        private const string DefaultTip = "点击后按下组合键；按 Backspace 或 Delete 清除";

        private const int ModAlt = (int)NativeMethods.MOD_ALT;
        private const int ModCtrl = (int)NativeMethods.MOD_CONTROL;
        private const int ModShift = (int)NativeMethods.MOD_SHIFT;
        private const int ModWin = (int)NativeMethods.MOD_WIN;
        private const int ModMask = ModAlt | ModCtrl | ModShift | ModWin;

        private const int VK_TAB = 0x09;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_SPACE = 0x20;
        private const int VK_DELETE = 0x2E;
        private const int VK_F4 = 0x73;

        private struct ReservedCombo
        {
            public readonly int Modifiers;
            public readonly int Vk;
            public readonly string Reason;
            public ReservedCombo(int modifiers, int vk, string reason)
            {
                Modifiers = modifiers;
                Vk = vk;
                Reason = reason;
            }
        }

        private static readonly ReservedCombo[] Reserved = new ReservedCombo[]
        {
            new ReservedCombo(ModCtrl, 'C', "Ctrl+C 留给复制，请换一个"),
            new ReservedCombo(ModCtrl, 'V', "Ctrl+V 留给粘贴，请换一个"),
            new ReservedCombo(ModCtrl, 'X', "Ctrl+X 留给剪切，请换一个"),
            new ReservedCombo(ModCtrl, 'Z', "Ctrl+Z 留给撤销，请换一个"),
            new ReservedCombo(ModCtrl, 'Y', "Ctrl+Y 留给重做，请换一个"),
            new ReservedCombo(ModCtrl, 'A', "Ctrl+A 留给全选，请换一个"),
            new ReservedCombo(ModCtrl, 'S', "Ctrl+S 留给保存，请换一个"),
            new ReservedCombo(ModCtrl, VK_SPACE, "Ctrl+Space 留给输入法中英文切换，请换一个"),
            new ReservedCombo(ModCtrl, VK_ESCAPE, "Ctrl+Esc 留给开始菜单，请换一个"),
            new ReservedCombo(ModCtrl | ModShift, VK_ESCAPE, "Ctrl+Shift+Esc 留给任务管理器，请换一个"),
            new ReservedCombo(ModCtrl | ModAlt, VK_DELETE, "Ctrl+Alt+Del 留给系统安全选项，请换一个"),
            new ReservedCombo(ModAlt, VK_F4, "Alt+F4 留给关闭窗口，请换一个"),
            new ReservedCombo(ModAlt, VK_TAB, "Alt+Tab 留给切换窗口，请换一个"),
            new ReservedCombo(ModAlt, VK_ESCAPE, "Alt+Esc 留给切换窗口，请换一个"),
            new ReservedCombo(ModAlt, VK_SPACE, "Alt+Space 留给窗口菜单，请换一个"),
            new ReservedCombo(ModWin, 'L', "Win+L 留给锁屏，请换一个"),
            new ReservedCombo(ModWin, 'D', "Win+D 留给显示桌面，请换一个"),
            new ReservedCombo(ModWin, 'E', "Win+E 留给文件资源管理器，请换一个"),
            new ReservedCombo(ModWin, 'R', "Win+R 留给“运行”对话框，请换一个"),
            new ReservedCombo(ModWin, 'S', "Win+S 留给搜索，请换一个"),
            new ReservedCombo(ModWin, 'V', "Win+V 留给剪贴板历史，请换一个"),
            new ReservedCombo(ModWin, VK_TAB, "Win+Tab 留给任务视图，请换一个"),
            new ReservedCombo(ModWin, VK_SPACE, "Win+Space 留给切换输入法，请换一个"),
            new ReservedCombo(ModWin | ModShift, 'S', "Win+Shift+S 留给截图工具，请换一个"),
        };

        private int _modifiers;
        private int _key;
        private bool _suspendedHotkeys;

        /// <summary>绑定发生变化（已是合法组合或已清空）。</summary>
        public event EventHandler BindingChanged;

        public HotkeyBox()
        {
            IsReadOnly = true;
            IsReadOnlyCaretVisible = false;
            IsTextSelectionEnabled = false;   // 选中高亮在只读展示框里只会让人以为能编辑
            ClearButtonEnabled = false;       // 清除用 Backspace，模板里的 × 会挤掉组合键文字
            Cursor = Cursors.Hand;
            InputMethod.SetIsInputMethodEnabled(this, false);
            ToolTip = L.T(DefaultTip);
            Text = L.T("未设置");
            Unloaded += delegate { ResumeHotkeys(); };
        }

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);

            // WPF-UI 的控件样式放在资源字典里（隐式样式，按精确类型匹配），
            // 不是 generic.xaml 主题样式——所以派生类既拿不到样式也拿不到模板，
            // 不显式指定的话整个控件会渲染成一片空白。DefaultStyleKey 在这里没用。
            if (ReadLocalValue(StyleProperty) == DependencyProperty.UnsetValue)
            {
                var key = typeof(global::Wpf.Ui.Controls.TextBox);
                var style = TryFindResource(key) as Style;
                if (style == null && Application.Current != null)
                    style = Application.Current.TryFindResource(key) as Style;
                if (style != null) Style = style;
            }
        }

        public int Modifiers { get { return _modifiers; } }

        public int VirtualKey { get { return _key; } }

        public void SetHotkey(int modifiers, int vk)
        {
            _modifiers = modifiers;
            _key = vk;
            UpdateText();
        }

        public static string ReservedReason(int modifiers, int vk)
        {
            int m = modifiers & ModMask;
            int k = vk & 0xFFFF;
            for (int i = 0; i < Reserved.Length; i++)
            {
                if (Reserved[i].Modifiers == m && Reserved[i].Vk == k)
                    return L.T(Reserved[i].Reason);
            }

            if (m == ModShift && ProducesCharacter(k))
                return string.Format(L.T("{0} 会影响正常打字，请再配合 Ctrl / Alt / Win"),
                    HotkeyService.Describe(m, k));

            return null;
        }

        private static bool ProducesCharacter(int vk)
        {
            if (vk >= 'A' && vk <= 'Z') return true;
            if (vk >= '0' && vk <= '9') return true;
            if (vk == VK_SPACE) return true;
            if (vk >= 0x60 && vk <= 0x6F && vk != 0x6C) return true;
            if (vk >= 0xBA && vk <= 0xC0) return true;
            if (vk >= 0xDB && vk <= 0xDF) return true;
            if (vk == 0xE2) return true;
            return false;
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            var key = e.Key;
            if (key == Key.System) key = e.SystemKey;
            if (key == Key.ImeProcessed) key = e.ImeProcessedKey;
            if (key == Key.DeadCharProcessed) key = e.DeadCharProcessedKey;

            var held = Keyboard.Modifiers;

            if (key == Key.Tab && (held & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
                return;

            e.Handled = true;

            if (key == Key.Back || key == Key.Delete)
            {
                _modifiers = 0; _key = 0;
                UpdateText();
                Raise();
                return;
            }

            // 只按修饰键不算完整绑定，等主键
            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LWin || key == Key.RWin ||
                key == Key.System || key == Key.ImeProcessed || key == Key.DeadCharProcessed ||
                key == Key.None)
            {
                return;
            }

            int mods = 0;
            if ((held & ModifierKeys.Control) != 0) mods |= ModCtrl;
            if ((held & ModifierKeys.Alt) != 0) mods |= ModAlt;
            if ((held & ModifierKeys.Shift) != 0) mods |= ModShift;
            if ((held & ModifierKeys.Windows) != 0) mods |= ModWin;

            if (mods == 0)
            {
                if (key == Key.Escape) { UpdateText(); return; }
                // 不带修饰键的单键会抢占全局输入，禁止
                ShowHint(L.T("请至少配合 Ctrl / Alt / Shift / Win"));
                return;
            }

            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) return;

            string reserved = ReservedReason(mods, vk);
            if (reserved != null)
            {
                ShowHint(reserved);
                return;
            }

            _modifiers = mods;
            _key = vk;
            UpdateText();
            Raise();
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            UpdateText();
            ResumeHotkeys();
        }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            if (_suspendedHotkeys) return;
            var svc = HotkeyService.Current;
            if (svc == null) return;
            svc.Suspend();
            _suspendedHotkeys = true;
        }

        private void ResumeHotkeys()
        {
            if (!_suspendedHotkeys) return;
            _suspendedHotkeys = false;
            var svc = HotkeyService.Current;
            if (svc != null) svc.Resume();
        }

        private void Raise()
        {
            var h = BindingChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        private void ShowHint(string message)
        {
            Text = message;
            ToolTip = message;
        }

        private void UpdateText()
        {
            ToolTip = L.T(DefaultTip);
            Text = _key == 0 ? L.T("未设置") : HotkeyService.Describe(_modifiers, _key);
        }
    }
}
