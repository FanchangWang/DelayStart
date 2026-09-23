using System.Runtime.InteropServices;

namespace DelayStart.App.Interop;

/// <summary>
/// Win32 通用文件对话框（<c>IFileDialog</c>）封装 ——「选择程序 / 导入 / 导出」的真正实现。
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
/// <para>
/// 🔴 **「打开」与「另存为」是两个 coclass，只有 <c>IFileDialog</c> 这一个共同接口**
/// （2026-09-23 实测，见 <c>IFileDialog</c> 的 remarks）—— 本类里两处 cast 都写
/// <c>(IFileDialog)</c>，不要"顺手"改成 <c>IFileOpenDialog</c>。
/// </para>
/// </remarks>
public static class Win32FilePicker
{
    private const uint FosForceFileSystem = 0x00000040;
    private const uint FosFileMustExist = 0x00001000;
    private const uint FosPathMustExist = 0x00002000;
    private const uint FosOverwritePrompt = 0x00000002;
    private const uint SigdnFilesysPath = 0x80058000;

    /// <summary>弹一个「打开文件」对话框，返回选中的文件完整路径。</summary>
    /// <param name="ownerHwnd">属主窗口句柄；为 <c>0</c> 时无属主弹出。</param>
    /// <param name="title">对话框标题。</param>
    /// <param name="extensions">扩展名白名单（形如 <c>.exe</c>）。</param>
    /// <param name="filterName">筛选器显示名（如「程序」「节假日数据」）。</param>
    /// <returns>选中的路径；用户取消或对话框失败时为 <see langword="null"/>。</returns>
    public static string? PickFile(
        nint ownerHwnd,
        string title,
        IReadOnlyList<string> extensions,
        string filterName = "程序")
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(extensions);

        // 🔴 ComImport coclass 不能直接 cast 到接口（CS0030）—— 经 object 中转，
        // castclass 在运行时走 QueryInterface。
        var dialog = (IFileDialog)(object)new FileOpenDialogRcw();
        try
        {
            _ = dialog.GetOptions(out var options);
            _ = dialog.SetOptions(options | FosFileMustExist | FosPathMustExist | FosForceFileSystem);
            _ = dialog.SetTitle(title);

            var specs = BuildFilterSpecs(extensions, filterName);
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

    /// <summary>
    /// 弹一个「另存为」对话框，返回用户选定的目标路径。
    /// </summary>
    /// <param name="ownerHwnd">属主窗口句柄；为 <c>0</c> 时无属主弹出。</param>
    /// <param name="title">对话框标题。</param>
    /// <param name="defaultFileName">预填的文件名（含扩展名）。</param>
    /// <param name="extensions">扩展名白名单（形如 <c>.json</c>）。</param>
    /// <param name="filterName">筛选器显示名。</param>
    /// <returns>目标路径；用户取消时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 🔴 用 <c>FileSaveDialog</c>（另一个 coclass）而不是给打开对话框改选项：
    /// <c>FOS_FILEMUSTEXIST</c> 与"另存为"语义直接冲突（目标文件本来就不存在）。
    /// </para>
    /// <para>
    /// 🔴 **cast 的目标接口必须是 <c>IFileDialog</c>，不能是 <c>IFileOpenDialog</c>**
    /// （2026-09-23 用户报"导出点击闪退"的根因）：<c>FileSaveDialog</c> 不实现
    /// <c>IFileOpenDialog</c>，QueryInterface 返回 <c>E_NOINTERFACE</c>，
    /// CLR 据此抛 <c>InvalidCastException</c> —— 而它抛在事件处理器里没人接，
    /// 结果是**整个进程消失**，连一句提示都留不下。
    /// </para>
    /// <para>
    /// 🔴 这里**只声明到 <c>IFileDialog::GetResult</c> 为止**，不声明 <c>IFileSaveDialog</c>
    /// 特有的那几个方法 —— 我们一个都不调，而 COM 接口是按 vtable 顺序调用的，
    /// 多声明就要多保证顺序正确，少声明则没有这个风险（少声明不会让前面的方法错位）。
    /// </para>
    /// </remarks>
    public static string? PickSaveFile(
        nint ownerHwnd,
        string title,
        string defaultFileName,
        IReadOnlyList<string> extensions,
        string filterName)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(defaultFileName);
        ArgumentNullException.ThrowIfNull(extensions);

        var dialog = (IFileDialog)(object)new FileSaveDialogRcw();
        try
        {
            _ = dialog.GetOptions(out var options);
            _ = dialog.SetOptions(options | FosOverwritePrompt | FosPathMustExist | FosForceFileSystem);
            _ = dialog.SetTitle(title);

            var specs = BuildFilterSpecs(extensions, filterName);
            _ = dialog.SetFileTypes((uint)specs.Length, specs);

            _ = dialog.SetFileName(defaultFileName);

            var extension = extensions.Count > 0 ? extensions[0].TrimStart('*') : string.Empty;
            if (extension.Length > 0)
            {
                _ = dialog.SetDefaultExtension(extension);
            }

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
    /// <param name="extensions">扩展名白名单。</param>
    /// <param name="filterName">筛选器显示名。</param>
    /// <remarks>
    /// 🔴 规格必须写成通配符形态（<c>*.exe</c> 而非 <c>.exe</c>）：<c>.exe</c> 是字面
    /// 匹配，只匹配名字恰好叫「.exe」的文件 —— 表现为列表里所有程序文件都是灰的、
    /// 选不了（2026-09-19 用户实测）。另加「所有文件」兜底档，用户仍可浏览任意文件。
    /// </remarks>
    private static COMDLG_FILTERSPEC[] BuildFilterSpecs(IReadOnlyList<string> extensions, string filterName) =>
    [
        new COMDLG_FILTERSPEC
        {
            pszName = filterName,
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

    /// <summary>「打开」对话框的 coclass（<c>FileOpenDialog</c>）。</summary>
    [ComImport]
    [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private sealed class FileOpenDialogRcw;

    /// <summary>「另存为」对话框的 coclass（<c>FileSaveDialog</c>）。</summary>
    [ComImport]
    [Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B")]
    private sealed class FileSaveDialogRcw;

    /// <summary>
    /// 两个对话框 coclass 的**唯一共同接口**（IID <c>42F85136-DB7E-439C-85F1-E4075D135FC8</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <c>IFileOpenDialog</c> 与 <c>IFileSaveDialog</c> 是**平行接口** —— 两者都只继承
    /// <c>IFileDialog</c>，彼此之间没有继承关系，所以拿不到对方的 IID。
    /// 2026-09-23 在本机用 <c>Marshal.QueryInterface</c> 实测：
    /// </para>
    /// <code>
    /// FileSaveDialog → IFileOpenDialog (D57C7288-D4AD-4768-BE02-9D969532D960)  HR=0x80004002 E_NOINTERFACE
    /// FileSaveDialog → IFileDialog     (42F85136-DB7E-439C-85F1-E4075D135FC8)  HR=0x00000000 OK
    /// FileSaveDialog → IFileSaveDialog (84BCCD23-5FDE-4CDB-AEA4-AF64B83D78AB)  HR=0x00000000 OK
    /// FileOpenDialog → IFileOpenDialog                                          HR=0x00000000 OK
    /// FileOpenDialog → IFileDialog                                              HR=0x00000000 OK
    /// </code>
    /// <para>
    /// 教训：**接口 cast 失败 = QueryInterface 失败 = <c>InvalidCastException</c>**，
    /// 不是"返回 null 然后走兜底"。在事件处理器里没人接住就会静默崩掉整个进程
    /// （用户看到的就是"点一下闪退"）。所以要 cast 就 cast 到两个 coclass 都支持的
    /// <c>IFileDialog</c>，那也是我们实际用到的全部方法所在。
    /// </para>
    /// <para>
    /// 方法列到 <c>SetFilter</c> 为止即 <c>IFileDialog</c> 的完整 vtable；
    /// <c>IFileOpenDialog</c> 特有的 <c>GetResults</c> / <c>GetSelectedItems</c> 不在此声明 ——
    /// 我们用不到，多声明反而要保证 vtable 顺序继续正确。
    /// </para>
    /// </remarks>
    [ComImport]
    [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
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
