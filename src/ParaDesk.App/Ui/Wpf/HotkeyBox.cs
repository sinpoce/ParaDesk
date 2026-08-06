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
        private int _modifiers;
        private int _key;

        /// <summary>绑定发生变化（已是合法组合或已清空）。</summary>
        public event EventHandler BindingChanged;

        public HotkeyBox()
        {
            IsReadOnly = true;
            IsReadOnlyCaretVisible = false;
            IsTextSelectionEnabled = false;   // 选中高亮在只读展示框里只会让人以为能编辑
            ClearButtonEnabled = false;       // 清除用 Backspace，模板里的 × 会挤掉组合键文字
            Cursor = Cursors.Hand;
            ToolTip = "点击后按下组合键；按 Backspace 或 Delete 清除";
            Text = L.T("未设置");
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
        public int Key2 { get { return _key; } }

        public void SetBinding(int modifiers, int key)
        {
            _modifiers = modifiers;
            _key = key;
            UpdateText();
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            e.Handled = true;   // 全部自己消化，避免触发 TextBox 默认行为

            var key = e.Key == Key.System ? e.SystemKey : e.Key;

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
                key == Key.System || key == Key.None)
            {
                return;
            }

            int mods = 0;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods |= (int)NativeMethods.MOD_CONTROL;
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods |= (int)NativeMethods.MOD_ALT;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods |= (int)NativeMethods.MOD_SHIFT;
            if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) mods |= (int)NativeMethods.MOD_WIN;

            // 不带修饰键的单键会抢占全局输入，禁止
            if (mods == 0)
            {
                Text = L.T("请至少配合 Ctrl / Alt / Shift / Win");
                return;
            }

            _modifiers = mods;
            _key = KeyInterop.VirtualKeyFromKey(key);
            UpdateText();
            Raise();
        }

        private void Raise()
        {
            var h = BindingChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        private void UpdateText()
        {
            if (_key == 0) { Text = L.T("未设置"); return; }
            Text = HotkeyService.Describe(new HotkeyBinding
            {
                Modifiers = _modifiers,
                Key = _key,
                Enabled = true,
            });
        }
    }
}
