using System;
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
        private bool _hasSaved;

        private string _savedUser;

        public CredentialDialog()
        {
            InitializeComponent();

            string user, savedPwd;
            bool loaded = CredentialStore.TryLoad(out user, out savedPwd);

            _hasSaved = loaded && !string.IsNullOrEmpty(savedPwd);
            _savedUser = _hasSaved ? user : null;

            ChkEnable.IsChecked = loaded;
            TbUser.Text = loaded ? user : CredentialStore.CurrentUserName;
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
            LblKeepPwd.Visibility = on && _hasSaved ? Visibility.Visible : Visibility.Collapsed;
            BtnClear.IsEnabled = CredentialStore.Exists;
        }

        private void OnClear(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, L.T("确定清除已保存的登录凭据吗？清除后立即生效，无法撤销。"),
                    AppInfo.ProductName, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            CredentialStore.Clear();
            _hasSaved = false;
            _savedUser = null;
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
                if (CredentialStore.Exists) CredentialStore.Clear();
                DialogResult = true;
                return;
            }

            string user = (TbUser.Text ?? "").Trim();
            if (user.Length == 0)
            {
                MessageBox.Show(this, L.T("请填写账户名。"), AppInfo.ProductName,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string pwd = TbPwd.Password ?? "";
            if (pwd.Length == 0)
            {
                if (!_hasSaved)
                {
                    MessageBox.Show(this, L.T("请填写密码。"), AppInfo.ProductName,
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (string.Equals(user, _savedUser, StringComparison.Ordinal))
                {
                    DialogResult = true;
                    return;
                }

                string oldUser, oldPwd;
                if (!CredentialStore.TryLoad(out oldUser, out oldPwd))
                {
                    MessageBox.Show(this, L.T("读取已保存的密码失败，请重新输入密码。"), AppInfo.ProductName,
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (string.IsNullOrEmpty(oldPwd))
                {
                    MessageBox.Show(this, L.T("请填写密码。"), AppInfo.ProductName,
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                pwd = oldPwd;
            }

            if (!CredentialStore.Save(user, pwd))
            {
                MessageBox.Show(this, L.T("保存失败，详见日志。"), AppInfo.ProductName,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
        }
    }
}
