using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ParaDesk.Core;

namespace ParaDesk.Shell
{
    /// <summary>
    /// 运行时界面翻译。
    ///
    /// 为什么用遍历而不是把每条字符串改成资源绑定：界面里有 140 多条文案，
    /// 逐条改 XAML 既繁琐又极易漏，而且以后每加一个控件都得记着绑定。
    /// 这里在界面树上走一遍、按"中文原文"查表替换——查不到就原样保留，
    /// 因此未翻译的部分自然是中文，不会出现开发者才看得懂的键名。
    ///
    /// 代价：动态赋值的文本会覆盖翻译结果，所以刷新界面后需要重新调用。
    /// </summary>
    internal static class Localizer
    {
        /// <summary>翻译整棵界面树。中文界面下直接跳过，零开销。</summary>
        public static void Translate(DependencyObject root)
        {
            if (root == null || L.Current == "zh") return;
            try { Walk(root, 0); }
            catch (Exception ex) { Log.Debug("界面翻译失败: " + ex.Message); }
        }

        private static void Walk(DependencyObject node, int depth)
        {
            if (node == null || depth > 40) return;

            Apply(node);

            // 逻辑树能覆盖尚未渲染的部分（折叠的页面），视觉树覆盖模板生成的内容，
            // 两边都走一遍才不会漏。
            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                var dep = child as DependencyObject;
                if (dep != null) Walk(dep, depth + 1);
            }

            int count = 0;
            try { count = VisualTreeHelper.GetChildrenCount(node); }
            catch { count = 0; }
            for (int i = 0; i < count; i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i), depth + 1);
            }

            WalkHeader(node, depth);
        }

        /// <summary>
        /// CardControl / HeaderedContentControl 的 Header 既不在逻辑树里，
        /// 页面折叠时也没有视觉树——两种遍历都够不着，得单独取一次。
        /// 界面上大部分说明文字恰恰都在 Header 里。
        /// </summary>
        private static void WalkHeader(DependencyObject node, int depth)
        {
            try
            {
                var pi = node.GetType().GetProperty("Header");
                if (pi == null) return;

                var v = pi.GetValue(node, null);

                var s = v as string;
                if (s != null)
                {
                    string t = L.T(s);
                    if (!ReferenceEquals(t, s)) pi.SetValue(node, t, null);
                    return;
                }

                var dep = v as DependencyObject;
                if (dep != null) Walk(dep, depth + 1);
            }
            catch { }
        }

        private static void Apply(DependencyObject node)
        {
            var tb = node as TextBlock;
            if (tb != null) { tb.Text = L.T(tb.Text); return; }

            var win = node as Window;
            if (win != null) { win.Title = L.T(win.Title); }

            // ContentControl 覆盖 Button / ComboBoxItem / CardControl 等一大类
            var cc = node as ContentControl;
            if (cc != null)
            {
                var s = cc.Content as string;
                if (s != null) cc.Content = L.T(s);
            }

            var tip = node as FrameworkElement;
            if (tip != null)
            {
                var t = tip.ToolTip as string;
                if (t != null) tip.ToolTip = L.T(t);
            }

            TranslateWpfUi(node);
        }

        /// <summary>
        /// WPF-UI 自带的文本属性不是 WPF 标准属性，遍历不到，用反射按控件名单独处理。
        /// 限定控件类型而不是见到 Title 就翻：别的控件的 Title 可能是用户数据。
        /// </summary>
        private static void TranslateWpfUi(DependencyObject node)
        {
            var t = node.GetType();

            string[] props;
            if (t.Name == "InfoBar") props = new[] { "Title", "Message" };
            else if (t.Name == "TitleBar") props = new[] { "Title" };
            else return;

            foreach (string prop in props)
            {
                try
                {
                    var pi = t.GetProperty(prop);
                    if (pi == null || pi.PropertyType != typeof(string)) continue;
                    var v = pi.GetValue(node, null) as string;
                    if (!string.IsNullOrEmpty(v)) pi.SetValue(node, L.T(v), null);
                }
                catch { }
            }
        }
    }
}
