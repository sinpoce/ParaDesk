using System;
using System.Windows;
using System.Windows.Input;
using ParaDesk.Core;

namespace ParaDesk.Shell
{
    /// <summary>给一块显示器起名。</summary>
    internal partial class RenameDialog
    {
        /// <summary>用户确定后的名字；空字符串表示恢复默认。</summary>
        public string ResultName { get; private set; }

        private readonly string _device;

        public RenameDialog(MonitorInfo m)
        {
            InitializeComponent();

            _device = m == null ? null : m.DeviceName;

            TbName.MaxLength = MonitorNaming.MaxNameLength;

            TbSubject.Text = m == null
                ? ""
                : string.Format(L.T("{0}　{1}×{2}{3}"),
                    m.ShortName, m.Bounds.Width, m.Bounds.Height,
                    m.IsPrimary ? L.T("　主屏") : "");

            TbName.Text = m == null ? "" : (MonitorNaming.CustomName(m.DeviceName) ?? "");

            Loaded += delegate { FocusName(); };

            Localizer.Translate(this);
        }

        private void FocusName()
        {
            TbName.SelectAll();
            TbName.Focus();
            Keyboard.Focus(TbName);
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            string name = (TbName.Text ?? "").Trim();

            if (name.Length > 0 && IsTakenByOther(name))
            {
                MessageBox.Show(this, string.Format(L.T("「{0}」已经是另一块显示器的名字，请换一个。"), name),
                    AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
                FocusName();
                return;
            }

            ResultName = name;
            DialogResult = true;
        }

        private bool IsTakenByOther(string name)
        {
            try
            {
                foreach (var other in MonitorService.Enumerate())
                {
                    if (other == null || string.Equals(other.DeviceName, _device, StringComparison.Ordinal)) continue;
                    if (string.Equals(MonitorNaming.NameOf(other), name, StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Debug("改名查重时枚举显示器失败: " + ex.Message);
                string owner = MonitorNaming.FindDeviceByName(name);
                return owner != null && !string.Equals(owner, _device, StringComparison.Ordinal);
            }
        }
    }
}
