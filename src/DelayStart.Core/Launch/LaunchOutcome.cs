namespace DelayStart.Core.Launch;

/// <summary>
/// 一次「发起进程」动作的即时结果 —— 只回答"进程有没有被创建出来"，
/// **不回答"程序有没有真的启动成功"**。
/// </summary>
/// <remarks>
/// <para>
/// 这两件事必须分开：<c>Process.Start</c> 返回成功 ≠ 目标程序启动成功。
/// 真正的成败判定要等 1.5 秒后复查进程状态，由 <c>LaunchResultEvaluator</c> 完成
/// （机制 7 / FR-5.9）。
/// </para>
/// <para>
/// 分开的另一个好处：本模型不依赖任何 P/Invoke，因此 Core 层可以在不引用
/// Windows API 的前提下先把"结果判定"这条纯逻辑测干净。
/// </para>
/// </remarks>
public sealed class LaunchOutcome
{
    /// <summary>进程是否被成功创建。</summary>
    public required bool Created { get; init; }

    /// <summary>
    /// 目标进程 ID。为 <see langword="null"/> 表示拿不到句柄，无法做后续复查 ——
    /// UWP 经 shell 激活时属于这种情形（见 D28 / R12）。
    /// </summary>
    public int? ProcessId { get; init; }

    /// <summary>创建失败的原因描述。成功时为 <see langword="null"/>。</summary>
    public string? FailureMessage { get; init; }

    /// <summary>
    /// 普通用户条目降权启动失败后已回退为直接启动（E6 / FR-5.7）。
    /// 记入结果是为了让用户知情："它起来了，但是以管理员身份起来的"。
    /// </summary>
    public bool DeElevationFellBack { get; init; }

    /// <summary>构造一个创建成功的结果。</summary>
    /// <param name="processId">目标进程 ID；拿不到时为 <see langword="null"/>。</param>
    /// <param name="deElevationFellBack">是否发生过降权回退。</param>
    /// <returns>成功的启动结果。</returns>
    public static LaunchOutcome Success(int? processId, bool deElevationFellBack = false)
        => new() { Created = true, ProcessId = processId, DeElevationFellBack = deElevationFellBack };

    /// <summary>构造一个创建失败的结果。</summary>
    /// <param name="failureMessage">失败原因描述，中文。</param>
    /// <returns>失败的启动结果。</returns>
    public static LaunchOutcome Failure(string failureMessage)
        => new() { Created = false, FailureMessage = failureMessage };
}
