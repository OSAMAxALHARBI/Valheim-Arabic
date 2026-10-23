// The Explorer-style "choose a folder" dialog (IFileOpenDialog with FOS_PICKFOLDERS). WPF has no folder dialog and
// WinForms' FolderBrowserDialog is the old tree view.
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Taareeb.Installer
{
    static class FolderPicker
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogCom { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr specs);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr sink, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

        const uint PickFolders = 0x20, ForceFileSystem = 0x40, PathMustExist = 0x800;
        const uint FileSysPath = 0x80058000;

        // returns null when the user cancels
        public static string Pick(IntPtr owner, string title, string start)
        {
            var dlg = (IFileOpenDialog)new FileOpenDialogCom();
            try
            {
                dlg.SetOptions(PickFolders | ForceFileSystem | PathMustExist);
                dlg.SetTitle(title);
                if (!string.IsNullOrEmpty(start) && Directory.Exists(start))
                {
                    IShellItem folder;
                    SHCreateItemFromParsingName(start, IntPtr.Zero, typeof(IShellItem).GUID, out folder);
                    dlg.SetFolder(folder);
                }
                if (dlg.Show(owner) != 0) return null;
                IShellItem result;
                dlg.GetResult(out result);
                string path;
                result.GetDisplayName(FileSysPath, out path);
                return path;
            }
            finally { Marshal.ReleaseComObject(dlg); }
        }
    }
}
