using System.Windows;

namespace ParaDesk.Shell
{
    /// <summary>
    /// 侧边导航项的图标。
    ///
    /// 做成附加属性而不是把图标塞进 Content：Content 必须保持纯字符串，
    /// 运行时切换语言的树遍历（<see cref="Localizer"/>）只翻译字符串型 Content，
    /// 一旦换成 StackPanel，导航文字就再也翻不动了。
    /// </summary>
    internal static class Nav
    {
        public static readonly DependencyProperty IconProperty =
            DependencyProperty.RegisterAttached(
                "Icon",
                typeof(global::Wpf.Ui.Controls.SymbolRegular),
                typeof(Nav),
                new PropertyMetadata(global::Wpf.Ui.Controls.SymbolRegular.Empty));

        public static void SetIcon(DependencyObject o, global::Wpf.Ui.Controls.SymbolRegular v)
        {
            o.SetValue(IconProperty, v);
        }

        public static global::Wpf.Ui.Controls.SymbolRegular GetIcon(DependencyObject o)
        {
            return (global::Wpf.Ui.Controls.SymbolRegular)o.GetValue(IconProperty);
        }
    }
}
