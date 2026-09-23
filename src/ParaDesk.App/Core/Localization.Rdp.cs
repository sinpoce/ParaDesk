using System;

namespace ParaDesk.Core
{
    internal static partial class L
    {
        static partial void AddRdp(Action<string, string> Add)
        {
            Add("连接超时：可能停在了登录或证书确认，查看日志了解详情。",
                "Connection timed out: it may be stuck at sign-in or a certificate prompt. See the log for details.");

            Add("登录分身桌面失败（错误码 {0}）：{1}", "Couldn't sign in to the parallel desktop (error {0}): {1}");
            Add("用户名或密码不正确。", "The user name or password is incorrect.");
            Add("账户密码已过期，请先在主桌面修改密码。",
                "The account password has expired. Change it on your main desktop first.");
            Add("登录未完成，分身桌面停在了登录界面。",
                "Sign-in didn't complete; the parallel desktop is waiting at the sign-in screen.");
            Add("账户受限制（例如登录时段限制，或不允许空密码登录）。",
                "The account is restricted (for example by logon hours, or blank passwords aren't allowed).");
            Add("账户要求先修改密码才能登录。", "The account must change its password before it can sign in.");
            Add("登录被系统的会话仲裁打断。", "Sign-in was interrupted by Windows session arbitration.");
            Add("登录失败。", "Sign-in failed.");
        }
    }
}
