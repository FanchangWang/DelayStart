using System.Runtime.InteropServices;
using System.Text;

namespace DelayStart.Management.Interop;

/// <summary>
/// 快捷方式 COM 接口声明（FR-1.7 / D9）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 这些声明**只能**住在 Management 层：<c>[ComImport]</c> 依赖 built-in COM，
/// 在 NativeAOT 下不可用（IL3052 / R12）。Core 开了 <c>IsAotCompatible=true</c>，
/// 同样的代码搬过去会被构建期直接拦下 —— 这是刻意的护栏，不是巧合。
/// </para>
/// <para>
/// 只声明 <see cref="IShellLinkW.GetPath"/> 与 <see cref="IShellLinkW.GetArguments"/> 真正用到的部分，
/// 但**方法顺序不能删**：COM 接口是按 vtable 槽位调用的，漏掉中间的方法会让后面所有调用错位。
/// </para>
/// </remarks>
[ComImport]
[Guid("000214F9-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellLinkW
{
    void GetPath(
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
        int cchMaxPath,
        IntPtr pfd,
        uint fFlags);

    void GetIDList(out IntPtr ppidl);

    void SetIDList(IntPtr pidl);

    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);

    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);

    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);

    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

    void GetHotkey(out short pwHotkey);

    void SetHotkey(short wHotkey);

    void GetShowCmd(out int piShowCmd);

    void SetShowCmd(int iShowCmd);

    void GetIconLocation(
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
        int cchIconPath,
        out int piIcon);

    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

    void Resolve(IntPtr hwnd, uint fFlags);

    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
}

/// <summary>
/// <c>IPersistFile</c>，用于把 <c>.lnk</c> 文件加载进 <see cref="IShellLinkW"/>。
/// </summary>
/// <remarks>
/// vtable 顺序同样不能改：<c>GetClassID</c> → <c>IsDirty</c> → <c>Load</c> …
/// 其中 <c>IsDirty</c> 在原生声明里返回 <c>HRESULT</c>，若不加 <see cref="PreserveSigAttribute"/>
/// 会让后续槽位整体偏移一位。
/// </remarks>
[ComImport]
[Guid("0000010B-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersistFile
{
    void GetClassID(out Guid pClassID);

    [PreserveSig]
    int IsDirty();

    void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

    void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

    void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
}

/// <summary><c>CLSID_ShellLink</c>，配合 <see cref="ComFactory"/> 实例化。</summary>
/// <remarks>
/// 这里刻意**不**声明 <c>[ComImport] class ShellLink</c>：那种写法在现代 C# 里过不了编译 ——
/// <c>(IShellLinkW)new ShellLink()</c> 会报 <c>CS0030</c>，因为编译器看不到
/// "COM 组件类 → 接口"之间存在任何可用的引用转换。改走
/// <c>CoCreateInstance</c> + <c>Marshal.GetObjectForIUnknown</c>，路径更显式也更可靠。
/// </remarks>
internal static class ShellLinkClassId
{
    /// <summary><c>{00021401-0000-0000-C000-000000000046}</c>。</summary>
    public static readonly Guid Value = new("00021401-0000-0000-C000-000000000046");
}

/// <summary><c>IShellLinkW.GetPath</c> 的行为标志。</summary>
internal static class ShellLinkFlags
{
    /// <summary>原始路径，不展开环境变量。</summary>
    public const uint RawPath = 0x00000004;

    /// <summary>不弹"找不到目标"的修复对话框（后台进程里必须给，否则可能挂起）。</summary>
    public const uint NoUi = 0x00000001;

    /// <summary><c>IPersistFile.Load</c> 的只读模式。</summary>
    public const uint StgmRead = 0x00000000;
}
