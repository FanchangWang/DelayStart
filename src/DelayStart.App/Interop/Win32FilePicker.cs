using System.Runtime.InteropServices;

namespace DelayStart.App.Interop;

/// <summary>
/// Win32 通用文件对话框（<c>IFileOpenDialog</c>）封装 ——「选择程序」的真正实现。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 提权进程（D20 全程提权）里 WinRT 的 <c>FileOpenPicker</c> 打不开：选择器的
/// broker 拒绝高完整性令牌，点击按钮后什么也不发生（2026-09-19 用户实测）。
/// COM 版通用对话框没有这个限制 —— 记事本、任务管理器等系统提权程序用的就是它。
/// </para>
/// <para>
/// App 工程不在 AOT 边界内（AOT 守门只在 Core / Scheduler），可以正常用 COM。
/// </para>
/// </remarks>
public static class Win32FilePicker
{
    private const uint FosForceFileSystem = 0x00000040;
    private const uint FosFileMustExist = 0x00001000;
    private const uint FosPathMustExist = 0x00002000;
    private const uint SigdnFilesysPath = 0x80058000;

    /// <summary>弹一个「打开文件」对话框，返回选中的文件完整路径。</summary>
    /// <param name="ownerHwnd">属主窗口句柄；为 <c>0</c> 时无属主弹出。</param>
    /// <param name="title">对话框标题。</param>
    /// <param name="extensions">扩展名白名单（形如 <c>.exe</c>）。</param>
    /// <returns>选中的路径；用户取消或对话框失败时为 <see langword="null"/>。</returns>
    public static string? PickFile(nint ownerHwnd, string title, IReadOnlyList<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(extensions);

        // 🔴 ComImport coclass 不能直接 cast 到接口（CS0030）—— 经 object 中转，
        // castclass 在运行时走 QueryInterface。
        var dialog = (IFileOpenDialog)(object)new FileOpenDialogRcw();
        try
        {
            _ = dialog.GetOptions(out var options);
            _ = dialog.SetOptions(options | FosFileMustExist | FosPathMustExist | FosForceFileSystem);
            _ = dialog.SetTitle(title);

            var specs = BuildFilterSpecs(extensions);
            _ = dialog.SetFileTypes((uint)specs.Length, specs);

            // 0 = 确定；其它（含用户取消 0x800704C7）一律按「没选」处理。
            if (dialog.Show(ownerHwnd) != 0)
            {
                return null;
            }

            dialog.GetResult(out var item);
            if (item is null)
            {
                return null;
            }

            _ = item.GetDisplayName(SigdnFilesysPath, out var path);
            return string.IsNullOrEmpty(path) ? null : path;
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(dialog);
        }
    }

    /// <summary>把扩展名白名单拼成对话框筛选条目。</summary>
    /// <remarks>
    /// 🔴 规格必须写成通配符形态（<c>*.exe</c> 而非 <c>.exe</c>）：<c>.exe</c> 是字面
    /// 匹配，只匹配名字恰好叫「.exe」的文件 —— 表现为列表里所有程序文件都是灰的、
    /// 选不了（2026-09-19 用户实测）。另加「所有文件」兜底档，用户仍可浏览任意文件。
    /// </remarks>
    private static COMDLG_FILTERSPEC[] BuildFilterSpecs(IReadOnlyList<string> extensions) =>
    [
        new COMDLG_FILTERSPEC
        {
            pszName = "程序",
            pszSpec = string.Join(';', extensions.Select(static e => e.StartsWith('*') ? e : "*" + e)),
        },
        new COMDLG_FILTERSPEC
        {
            pszName = "所有文件",
            pszSpec = "*.*",
        },
    ];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COMDLG_FILTERSPEC
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pszName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string pszSpec;
    }

    [ComImport]
    [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private sealed class FileOpenDialogRcw;

    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        // IModalWindow
        [PreserveSig]
        int Show(nint hwndOwner);

        // IFileDialog（vtable 顺序不可动）
        [PreserveSig]
        int SetFileTypes(
            uint cFileTypes,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] COMDLG_FILTERSPEC[] rgFilterSpec);

        [PreserveSig]
        int SetFileTypeIndex(uint iFileType);

        [PreserveSig]
        int GetFileTypeIndex(out uint piFileType);

        [PreserveSig]
        int Advise(nint pfde, out uint pdwCookie);

        [PreserveSig]
        int Unadvise(uint dwCookie);

        [PreserveSig]
        int SetOptions(uint fos);

        [PreserveSig]
        int GetOptions(out uint pfos);

        [PreserveSig]
        int SetDefaultFolder(IShellItem psi);

        [PreserveSig]
        int SetFolder(IShellItem psi);

        [PreserveSig]
        int GetFolder(out IShellItem ppsi);

        [PreserveSig]
        int GetCurrentSelection(out IShellItem ppsi);

        [PreserveSig]
        int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        [PreserveSig]
        int GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);

        [PreserveSig]
        int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);

        [PreserveSig]
        int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);

        [PreserveSig]
        int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);

        [PreserveSig]
        int GetResult(out IShellItem ppsi);

        [PreserveSig]
        int AddPlace(IShellItem psi, int fdap);

        [PreserveSig]
        int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);

        [PreserveSig]
        int Close(int hr);

        [PreserveSig]
        int SetClientGuid(in Guid guid);

        [PreserveSig]
        int ClearClientData();

        [PreserveSig]
        int SetFilter(nint pFilter);

        // IFileOpenDialog
        [PreserveSig]
        int GetResults(out nint ppenum);

        [PreserveSig]
        int GetSelectedItems(out nint ppsai);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppv);

        [PreserveSig]
        int GetParent(out IShellItem ppsi);

        [PreserveSig]
        int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

        [PreserveSig]
        int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        [PreserveSig]
        int Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
