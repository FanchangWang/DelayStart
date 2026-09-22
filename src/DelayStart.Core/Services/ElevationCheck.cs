using System.Runtime.InteropServices;

namespace DelayStart.Core.Services;

/// <summary>
/// 当前进程是否以提升后的令牌运行（D78，2026-09-22 用户批复）。
/// </summary>
/// <remarks>
/// <para>
/// 用途：调度端与守卫端的**唯一合法入口都是计划任务**（<c>RunLevel = Highest</c>）。
/// 用户手动双击 exe 时进程以未提权令牌启动 —— 那会产生第二条不受管理的运行路径
/// （调度端会多出一个"抢着启动条目"的进程），因此两者都在入口处检测并静默退出。
/// </para>
/// <para>
/// 检测方式必须是"查当前令牌的 <c>TokenElevation</c>"，**不能**用
/// <c>WindowsPrincipal.IsInRole(Administrator)</c>：后者在 UAC 下对未提权的受限管理员
/// 同样返回 <see langword="true"/>，与实际是否提权无关。
/// </para>
/// <para>
/// 从 <c>DelayStart.Scheduler/NativeMethods</c> 下沉到这里，供调度端与守卫端共用 ——
/// 两份实现会漂移，而漂移的表现是"某一个入口的门禁失效"，不报错、也看不出来。
/// </para>
/// </remarks>
public static partial class ElevationCheck
{
    private const int TokenElevation = 20;
    private const uint TokenQuery = 0x0008;

    /// <summary>当前进程令牌是否已提升。</summary>
    /// <returns>已提升（管理端批准 / 计划任务 Highest）时为 <see langword="true"/>。</returns>
    public static unsafe bool IsElevated()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
        {
            // 连自己的令牌都打不开：按"未提权"处理（保守方向 —— 拒绝运行）。
            return false;
        }

        try
        {
            var value = default(TokenElevationValue);
            var ok = GetTokenInformation(
                token,
                TokenElevation,
                &value,
                (uint)sizeof(TokenElevationValue),
                out _);

            return ok && value.IsElevated != 0;
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevationValue
    {
        public uint IsElevated;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetTokenInformation(
        nint token,
        int infoClass,
        TokenElevationValue* info,
        uint length,
        out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
