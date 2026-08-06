using System.Windows;
using ParaDesk.Core;

namespace ParaDesk.Shell
{
    /// <summary>
    /// 保存分身桌面登录凭据。
    ///
    /// 界面上明说"多数情况下不需要存密码"：首选方案是让配置好的凭据委派策略生效，
    /// 而不是让用户把密码落盘。
    /// </summary>
    internal partial class CredentialDialog
    {
        public CredentialDialog()
        {
            InitializeComponent();

            ChkEnable.IsChecked = CredentialStore.Exists;
            TbUser.Text = CredentialStore.CurrentUserName;
            UpdateEnabled();

            Localizer.Translate(this);
        }

        private void OnToggle(object sender, RoutedEventArgs e)
        {
            UpdateEnabled();
        }

        private void UpdateEnabled()
        {
            bool on = ChkEnable.IsChecked == true;
            TbUser.IsEnabled = on;
            TbPwd.IsEnabled = on;
            BtnClear.IsEnabled = CredentialStore.Exists;
        }

        private void OnClear(object sender, RoutedEventArgs e)
        {
            CredentialStore.Clear();
            ChkEnable.IsChecked = false;
            TbUser.Text = CredentialStore.CurrentUserName;
            TbPwd.Password = "";
            UpdateEnabled();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            if (ChkEnable.IsChecked != true)
            {
                CredentialStore.Clear();
                DialogResult = true;
                return;
            }

            if (string.IsNullOrEmpty(TbUser.Text))
            {
                MessageBox.Show(this, L.T("请填写账户名。"), AppInfo.ProductName,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!CredentialStore.Save(TbUser.Text, TbPwd.Password))
            {
                MessageBox.Show(this, L.T("保存失败，详见日志。"), AppInfo.ProductName,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
        }
    }
}
