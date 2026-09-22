using System.Runtime.InteropServices;

namespace DelayStart.App.Interop;

/// <summary>一次实例存活探测的结果。</summary>
/// <remarks>
/// 🔴 分成四态而不是 <see langword="bool"/>，是因为"打不开"与"不存在"在诊断上的价值
/// 完全不同：<see cref="AccessDenied"/> 说明"按只读权限打开"这条路没走通
/// （见 <see cref="InstanceProbe"/> 的说明），而它是**跨完整性级别只读探测**这个前提
/// 唯一能在现场观察到的判据 —— 折成 bool 就永远看不见了。
/// </remarks>
internal enum InstanceProbeResult
{
    /// <summary>对象存在且已按只读权限打开：实例在跑。</summary>
    Alive,

    /// <summary>对象不存在：没有实例在跑（"此刻没跑"的**正常**结论）。</summary>
    NotFound,

    /// <summary>对象存在但访问被拒：只读打开没走通，本探测无法给出结论。</summary>
    AccessDenied,

    /// <summary>其它失败（名字非法、句柄耗尽……）。</summary>
    Failed,
}

/// <summary>
/// 「另一个进程里的管理端实例还活着吗」的原生探测（D82）。
/// </summary>
/// <remarks>
/// <para>
/// 用途：<c>Program.Main</c> 的提权门要回答一个问题 —— 点通知进来的这次调用，
/// 是"交给已在运行的实例"还是"自己申请提权去开一个"？前者零 UAC，后者弹一次。
/// </para>
/// <para>
/// 🔴 <b>为什么不能用 <c>EventWaitHandle.TryOpenExisting(name, out _)</c></b>：那个重载
/// 写死了申请 <c>Synchronize | Modify</c>。Modify 是**写**权限，而 Windows 完整性级别
/// 对高完整性对象的默认策略是 <c>NO_WRITE_UP</c> —— 中完整性进程（Shell 按 <c>delaystart:</c>
/// 协议拉起来的正是它）拿不到，系统返回 <c>ERROR_ACCESS_DENIED</c>，
/// 而 <c>TryOpenExisting</c> 把它**静默折叠成 false**。于是"实例明明在跑"被读成
/// "没有实例"，每次点通知都白弹一次 UAC —— 正是 D82 要修的那个症状。
/// </para>
/// <para>
/// 🔴 <b>为什么不能换个托管重载</b>：托管 API 里没有"只读打开"的入口，
/// <c>EventWaitHandleRights</c> 这个枚举也不在 <c>System.Threading</c>（那是
/// .NET Framework 时代的放置位置，本解决方案引用的框架里没有它）。所以这里回到
/// <c>OpenEventW</c>，只申请 <c>SYNCHRONIZE</c>（读）—— 完整性级别只挡写、不挡读。
/// </para>
/// <para>
/// 探测的**误判方向是安全**的：结论不是 <see cref="InstanceProbeResult.Alive"/> 时，
/// 调用方一律按"没有实例"处理 —— 代价只是白弹一次 UAC（被拉起的子进程在同级别里
/// 必然探测成功，拿到实例就只留请求文件退出，落点不会丢）。
/// </para>
/// </remarks>
internal static partial class InstanceProbe
{
    /// <summary><c>SYNCHRONIZE</c>：打开一个可等待对象所需的最小权限（读）。</summary>
    private const uint Synchronize = 0x00100000;

    /// <summary><c>ERROR_FILE_NOT_FOUND</c>：对象不存在。</summary>
    private const int ErrorFileNotFound = 2;

    /// <summary><c>ERROR_ACCESS_DENIED</c>：对象在，但不给这个权限。</summary>
    private const int ErrorAccessDenied = 5;

    /// <summary>按名打开一个内核事件，只申请读权限。</summary>
    /// <param name="eventName">内核对象名（含 <c>Local\</c> 等前缀）。</param>
    /// <returns>探测结果，取值见 <see cref="InstanceProbeResult"/>。</returns>
    /// <remarks>拿到的句柄立即关闭 —— 探测不做任何等待，不持有对象。</remarks>
    public static InstanceProbeResult Probe(string eventName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        var handle = OpenEventW(Synchronize, inheritHandle: false, eventName);
        if (handle != nint.Zero)
        {
            _ = CloseHandle(handle);
            return InstanceProbeResult.Alive;
        }

        // 🔴 必须紧接着取错误码：中间不能再夹任何可能覆盖 LastError 的调用。
        return Marshal.GetLastWin32Error() switch
        {
            ErrorFileNotFound => InstanceProbeResult.NotFound,
            ErrorAccessDenied => InstanceProbeResult.AccessDenied,
            _ => InstanceProbeResult.Failed,
        };
    }

    /// <remarks>
    /// 🔴 <c>LibraryImport</c> 不会自动封送 <c>bool</c> 参数/返回值，必须显式
    /// <c>[MarshalAs(UnmanagedType.Bool)]</c>（<c>pitfalls.md</c> 坑 9）：
    /// 封送错只会表现成"句柄恒为 0"，即"永远认为没有实例"——
    /// 不报错、不崩，只是每次点通知都多弹一次 UAC。
    /// </remarks>
    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "OpenEventW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenEventW(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
