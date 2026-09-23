using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using ParaDesk.Core;

namespace ParaDesk.Shell
{
    internal static class Localizer
    {
        private static readonly object CacheSync = new object();

        private static readonly Dictionary<Type, PropertyInfo> HeaderProps = new Dictionary<Type, PropertyInfo>();

        private static readonly Dictionary<Type, PropertyInfo[]> TextProps = new Dictionary<Type, PropertyInfo[]>();

        private static readonly PropertyInfo[] NoProps = new PropertyInfo[0];

        /// <summary>翻译整棵界面树。中文界面下直接跳过，零开销。</summary>
        public static void Translate(DependencyObject root)
        {
            if (root == null || L.Current == "zh") return;
            var visited = new HashSet<DependencyObject>();
            try { Walk(root, 0, visited); }
            catch (Exception ex) { Log.Debug("界面翻译失败: " + ex.Message); }
        }

        private static void Walk(DependencyObject node, int depth, HashSet<DependencyObject> visited)
        {
            if (node == null || depth > 40) return;
            if (!visited.Add(node)) return;

            try { Apply(node); }
            catch (Exception ex) { Log.Debug("翻译节点失败（" + node.GetType().Name + "）: " + ex.Message); }

            WalkHeader(node, depth, visited);

            // 逻辑树能覆盖尚未渲染的部分（折叠的页面），视觉树覆盖模板生成的内容，
            // 两边都走一遍才不会漏。
            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                var dep = child as DependencyObject;
                if (dep != null) Walk(dep, depth + 1, visited);
            }

            if (node is Visual || node is Visual3D)
            {
                int count = 0;
                try { count = VisualTreeHelper.GetChildrenCount(node); }
                catch (Exception ex)
                {
                    Log.Debug("取视觉子节点失败（" + node.GetType().Name + "）: " + ex.Message);
                    count = 0;
                }
                for (int i = 0; i < count; i++)
                {
                    Walk(VisualTreeHelper.GetChild(node, i), depth + 1, visited);
                }
            }
        }

        /// <summary>
        /// CardControl / HeaderedContentControl 的 Header 既不在逻辑树里，
        /// 页面折叠时也没有视觉树——两种遍历都够不着，得单独取一次。
        /// 界面上大部分说明文字恰恰都在 Header 里。
        /// </summary>
        private static void WalkHeader(DependencyObject node, int depth, HashSet<DependencyObject> visited)
        {
            var pi = GetHeaderProp(node.GetType());
            if (pi == null) return;
            try
            {
                var v = pi.GetValue(node, null);

                var s = v as string;
                if (s != null)
                {
                    string t = L.T(s);
                    if (!string.Equals(t, s, StringComparison.Ordinal)) pi.SetValue(node, t, null);
                    return;
                }

                var dep = v as DependencyObject;
                if (dep != null) Walk(dep, depth + 1, visited);
            }
            catch (Exception ex)
            {
                Log.Debug("翻译 Header 失败（" + node.GetType().Name + "）: " + ex.Message);
            }
        }

        private static void Apply(DependencyObject node)
        {
            var tb = node as TextBlock;
            if (tb != null)
            {
                if (IsExpression(tb, TextBlock.TextProperty)) return;
                string s = tb.Text;
                string t = L.T(s);
                if (!string.Equals(t, s, StringComparison.Ordinal)) tb.Text = t;
                return;
            }

            var win = node as Window;
            if (win != null && !IsExpression(win, Window.TitleProperty))
            {
                string t = L.T(win.Title);
                if (!string.Equals(t, win.Title, StringComparison.Ordinal)) win.Title = t;
            }

            // ContentControl 覆盖 Button / ComboBoxItem / CardControl 等一大类
            var cc = node as ContentControl;
            if (cc != null)
            {
                var s = cc.Content as string;
                if (s != null && !IsExpression(cc, ContentControl.ContentProperty))
                {
                    string t = L.T(s);
                    if (!string.Equals(t, s, StringComparison.Ordinal)) cc.Content = t;
                }
            }

            var fe = node as FrameworkElement;
            if (fe != null)
            {
                var tip = fe.ToolTip as string;
                if (tip != null && !IsExpression(fe, FrameworkElement.ToolTipProperty))
                {
                    string t = L.T(tip);
                    if (!string.Equals(t, tip, StringComparison.Ordinal)) fe.ToolTip = t;
                }
            }

            TranslateWpfUi(node);
        }

        private static bool IsExpression(DependencyObject d, DependencyProperty p)
        {
            try { return DependencyPropertyHelper.GetValueSource(d, p).IsExpression; }
            catch (Exception ex)
            {
                Log.Debug("读取属性来源失败（" + d.GetType().Name + "." + p.Name + "）: " + ex.Message);
                return false;
            }
        }

        private static void TranslateWpfUi(DependencyObject node)
        {
            var props = GetTextProps(node.GetType());
            if (props.Length == 0) return;

            foreach (var pi in props)
            {
                try
                {
                    var v = pi.GetValue(node, null) as string;
                    if (string.IsNullOrEmpty(v)) continue;
                    string t = L.T(v);
                    if (!string.Equals(t, v, StringComparison.Ordinal)) pi.SetValue(node, t, null);
                }
                catch (Exception ex)
                {
                    Log.Debug("翻译 " + node.GetType().Name + "." + pi.Name + " 失败: " + ex.Message);
                }
            }
        }

        private static PropertyInfo GetHeaderProp(Type t)
        {
            lock (CacheSync)
            {
                PropertyInfo pi;
                if (HeaderProps.TryGetValue(t, out pi)) return pi;
                pi = FindProperty(t, "Header", false);
                HeaderProps[t] = pi;
                return pi;
            }
        }

        private static PropertyInfo[] GetTextProps(Type t)
        {
            lock (CacheSync)
            {
                PropertyInfo[] props;
                if (TextProps.TryGetValue(t, out props)) return props;

                var list = new List<PropertyInfo>();
                if (t.Name == "InfoBar")
                {
                    AddIfString(list, FindProperty(t, "Title", true));
                    AddIfString(list, FindProperty(t, "Message", true));
                }
                else if (t.Name == "TitleBar")
                {
                    AddIfString(list, FindProperty(t, "Title", true));
                }
                AddIfString(list, FindProperty(t, "PlaceholderText", true));

                props = list.Count == 0 ? NoProps : list.ToArray();
                TextProps[t] = props;
                return props;
            }
        }

        private static void AddIfString(List<PropertyInfo> list, PropertyInfo pi)
        {
            if (pi != null && pi.PropertyType == typeof(string)) list.Add(pi);
        }

        private static PropertyInfo FindProperty(Type t, string name, bool needWrite)
        {
            PropertyInfo pi = null;
            try
            {
                pi = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            }
            catch (AmbiguousMatchException)
            {
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.Name != name) continue;
                    if (pi == null || p.DeclaringType.IsSubclassOf(pi.DeclaringType)) pi = p;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("反射 " + t.Name + "." + name + " 失败: " + ex.Message);
                return null;
            }

            if (pi == null || !pi.CanRead || pi.GetIndexParameters().Length != 0) return null;
            if (needWrite && !pi.CanWrite) return null;
            return pi;
        }
    }
}
