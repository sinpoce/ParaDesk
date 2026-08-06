using System;
using System.Runtime.InteropServices;
using ParaDesk.Core;

namespace ParaDesk.Ui
{
    /// <summary>
    /// 现代版（Vista+）文件夹选择对话框。
    /// 不用 WinForms 的 FolderBrowserDialog：那是 XP 时代的小树形控件，
    /// 没有地址栏、不能粘贴路径、也没有收藏夹，放在这套 Fluent 界面里很突兀。
    /// </summary>
    internal static class FolderPicker
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRcw { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            // 下面这些方法我们不用，但必须原样声明以保持 COM 虚表顺序
            [PreserveSig] int Show([In] IntPtr parent);
            void SetFileTypes();
            void SetFileTypeIndex();
            void GetFileTypeIndex();
            void Advise();
            void Unadvise();
            void SetOptions([In] uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem si);
            void SetFolder(IShellItem si);
            void GetFolder(out IShellItem si);
            void GetCurrentSelection(out IShellItem si);
            void SetFileName([In, MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([In, MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([In, MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([In, MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem si);
            void AddPlace(IShellItem si, int alignment);
            void SetDefaultExtension([In, MarshalAs(UnmanagedType.LPWStr)] string ext);
            void Close([MarshalAs(UnmanagedType.Error)] int hr);
            void SetClientGuid();
            void ClearClientData();
            void SetFilter([MarshalAs(UnmanagedType.Interface)] object filter);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler();
            void GetParent(out IShellItem parent);
            void GetDisplayName([In] uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes();
            void Compare();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr bc,
            [In] ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

        private const uint FOS_PICKFOLDERS = 0x00000020;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        private const uint FOS_PATHMUSTEXIST = 0x00000800;
        private const uint SIGDN_FILESYSPATH = 0x80058000;
        private const int ERROR_CANCELLED = unchecked((int)0x800704C7);

        /// <summary>返回所选文件夹；取消或失败返回 null。</summary>
        public static string Pick(IntPtr owner, string title, string initialPath)
        {
            IFileDialog dialog = null;
            try
            {
                dialog = (IFileDialog)new FileOpenDialogRcw();
                dialog.SetOptions(FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
                if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);

                if (!string.IsNullOrEmpty(initialPath))
                {
                    try
                    {
                        Guid iid = typeof(IShellItem).GUID;
                        IShellItem start;
                        SHCreateItemFromParsingName(initialPath, IntPtr.Zero, ref iid, out start);
                        if (start != null) dialog.SetFolder(start);
                    }
                    catch { /* 初始目录无效不影响打开对话框 */ }
                }

                int hr = dialog.Show(owner);
                if (hr == ERROR_CANCELLED) return null;
                if (hr != 0) { Log.Warn("文件夹选择对话框返回 0x" + hr.ToString("X8")); return null; }

                IShellItem result;
                dialog.GetResult(out result);
                if (result == null) return null;

                string path;
                result.GetDisplayName(SIGDN_FILESYSPATH, out path);
                return path;
            }
            catch (Exception ex)
            {
                Log.Error("打开文件夹选择对话框失败", ex);
                return null;
            }
            finally
            {
                if (dialog != null) Marshal.ReleaseComObject(dialog);
            }
        }
    }
}
