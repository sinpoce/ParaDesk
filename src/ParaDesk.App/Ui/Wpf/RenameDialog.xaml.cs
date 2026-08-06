using System.Windows;
using ParaDesk.Core;

namespace ParaDesk.Shell
{
    /// <summary>给一块显示器起名。</summary>
    internal partial class RenameDialog
    {
        /// <summary>用户确定后的名字；空字符串表示恢复默认。</summary>
        public string ResultName { get; private set; }

        public RenameDialog(MonitorInfo m)
        {
            InitializeComponent();

            TbSubject.Text = m == null
                ? ""
                : string.Format(L.T("{0}　{1}×{2}{3}"),
                    m.ShortName, m.Bounds.Width, m.Bounds.Height,
                    m.IsPrimary ? L.T("　主屏") : "");

            TbName.Text = m == null ? "" : (MonitorNaming.CustomName(m.DeviceName) ?? "");
            TbName.SelectAll();
            TbName.Focus();

            Localizer.Translate(this);
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            ResultName = (TbName.Text ?? "").Trim();
            DialogResult = true;
        }
    }
}
