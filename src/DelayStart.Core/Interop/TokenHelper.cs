using System.Runtime.InteropServices;

namespace DelayStart.Core.Interop;

/// <summary>
/// 降权启动所需的用户令牌与环境块获取（NFR-3.3 / FR-5.6 / FR-5.8）。
/// </summary>
/// <remarks>
/// <para>
/// 调度端以管理员身份运行（D20），直接 <c>Process.Start</c> 出来的子进程会继承提升令牌。
/// 「普通用户身份」条目必须先拿到**当前交互登录用户**的原始令牌，再用它创建进程。
/// </para>
/// <para>
/// 全部走 <c>LibraryImport</c>（AOT 安全）。返回的 <see cref="DeElevationContext"/>
/// 实现 <see cref="IDisposable"/>：令牌与环境块的生命周期由调用方用 <c>using</c> 管理，
/// 忘记释放只会泄漏两块内核对象，不会产生安全问题。
/// </para>
/// </remarks>
public static partial class TokenHelper
{
    [LibraryImport("kernel32.dll")]
    private static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQueryUserToken(uint sessionId, out nint token);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateEnvironmentBlock(out nint environment, nint token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [LibraryImport("userenv.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyEnvironmentBlock(nint environment);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    /// <summary>
    /// 交互用户的令牌 + 环境块。两者一起创建、一起释放，避免"半套"状态。
    /// </summary>
    /// <param name="Token">用户主令牌；<c>0</c> 表示获取失败。</param>
    /// <param name="Environment">用户环境块指针；令牌无效时无意义。</param>
    public readonly record struct DeElevationContext(nint Token, nint Environment) : IDisposable
    {
        /// <summary>令牌是否有效。无效时调用方应回退或判失败（E6）。</summary>
        public bool IsValid => Token != 0;

        /// <inheritdoc />
        public void Dispose()
        {
            if (Environment != 0)
            {
                _ = DestroyEnvironmentBlock(Environment);
            }

            if (Token != 0)
            {
                _ = CloseHandle(Token);
            }
        }
    }

    /// <summary>
    /// 获取当前活动控制台会话的交互用户令牌与环境块。
    /// </summary>
    /// <returns>降权上下文；失败时 <see cref="DeElevationContext.IsValid"/> 为 <see langword="false"/>，**不抛异常**。</returns>
    /// <remarks>
    /// 失败是可预期的运行时状态而不是编程错误：锁屏、断开 RDP、会话切换时
    /// <c>WTSQueryUserToken</c> 都可能拿不到令牌 —— 此时调度端按 FR-5.7 回退。
    /// </remarks>
    public static DeElevationContext AcquireInteractiveUserToken()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == uint.MaxValue)
        {
            return default;
        }

        if (!WTSQueryUserToken(sessionId, out var token) || token == 0)
        {
            return default;
        }

        if (!CreateEnvironmentBlock(out var environment, token, inherit: false))
        {
            _ = CloseHandle(token);
            return default;
        }

        return new DeElevationContext(token, environment);
    }
}
