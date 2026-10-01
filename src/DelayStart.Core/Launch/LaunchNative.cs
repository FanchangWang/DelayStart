using System.Runtime.InteropServices;

namespace DelayStart.Core.Launch;

/// <summary>
/// 降权启动链用到的 Win32 声明（v0.6.1 从调度端下沉到 Core）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>为什么住在 Core 而不是 Management</b>：降权启动这条链现在有<b>两个</b>调用方 ——
/// 调度端（每条计划）与管计端（延时启动页每行的「启动」按钮）。两者必须走<b>同一份</b>实现：
/// 手写第二份的话，两边的 UWP / 快捷方式 / uiAccess 判定迟早分叉，而症状是
/// "登录时启动得好好的，手动点一下就不对"，极难归因。
/// </para>
/// <para>
/// 而 <c>DelayStart.Scheduler</c> 与 <c>DelayStart.Management</c> 都只能依赖 Core ——
/// 降权链放在任何一个上层都会被另一个上层排斥（硬约束 1），所以它<b>只能</b>住在 Core。
/// </para>
/// <para>
/// 🔴 <b>AOT 兼容</b>（硬约束 2）：全部走 <see cref="LibraryImportAttribute"/> + blittable 结构体，
/// 零 COM、零反射，结构体布局与原生一一对应。降权时要交给系统的是<b>可写</b>缓冲区，
/// 所以 <c>CreateProcessWithTokenW</c> 的 commandLine 是 <c>char*</c> 而非托管字符串。
/// </para>
/// <para>
/// 关键约束（demo2 七方案真机实测，本机结论）：
/// 把 explorer 的<b>进程令牌</b>直接交给 CreateProcessWithTokenW 会必报 Win32Error=5；
/// 先 DuplicateTokenEx 成主令牌再交给它则 0x2000 Medium 降权成功；
/// CreateProcessAsUserW 走不通（1314，SeAssignPrimaryToken 只有 SYSTEM 有）；
/// COM Shell.Application.ShellExecute <b>不降权</b>（子进程仍是 0x3000 High），禁用。
/// </para>
/// </remarks>
internal static unsafe partial class LaunchNative
{
    public const uint ProcessQueryLimitedInformation = 0x1000;

    public const uint TokenAssignPrimary    = 0x0001;
    public const uint TokenDuplicate        = 0x0002;
    public const uint TokenQuery            = 0x0008;
    public const uint TokenAdjustPrivileges = 0x0020;
    public const uint TokenAdjustDefault    = 0x0080;
    public const uint TokenAdjustSessionId  = 0x0100;

    public const uint SePrivilegeEnabled = 0x00000002;

    public const int SecurityImpersonation = 2;
    public const int TokenPrimaryType = 1;

    /// <summary>STARTUPINFOW。Desktop 用 <see cref="nint"/> 而非 string —— 降权时它必须是 NULL
    /// （指定 winsta0\default 是失败诱因之一），同时让结构体保持 blittable，AOT 下零封送。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfoW
    {
        public uint CbSize;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short Reserved2;
        public nint Reserved2Pointer;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    /// <summary>TOKEN_PRIVILEGES（单元素）。AdjustTokenPrivileges 只需要一条。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [LibraryImport("user32.dll")]
    public static partial nint GetShellWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateTokenEx(
        nint existingToken,
        uint desiredAccess,
        nint tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out nint newToken);

    /// <summary>
    /// 以指定主令牌创建进程（真正的降权点）。
    /// <c>commandLine</c> 走 <c>char*</c>：该参数对系统是可写缓冲区，不能传只读托管字符串。
    /// </summary>
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool CreateProcessWithTokenW(
        nint token,
        uint logonFlags,
        string applicationName,
        char* commandLine,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfoW startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupPrivilegeValueW(nint systemName, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustTokenPrivileges(
        nint tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges newState,
        uint bufferLength,
        nint previousState,
        nint returnLength);
}