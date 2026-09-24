using DelayStart.Management.Abstractions;

namespace DelayStart.Core.Tests.Fakes;

/// <summary>
/// <see cref="ISchedulerTaskRegistrar"/> 的测试替身 —— **不在系统里创建任何计划任务**。
/// </summary>
/// <remarks>
/// 接管流程的第 4 步是"确保计划任务存在"，而 <c>D33</c> 要求验证这一步失败时前几步会被回滚。
/// 用真实现做这件事就等于在测试里往测试机的任务库写东西，既需要提权又留下垃圾。
/// </remarks>
internal sealed class FakeSchedulerTaskRegistrar : ISchedulerTaskRegistrar
{
    /// <inheritdoc />
    public string TaskPath => @"\DelayStartScheduler";

    /// <summary>当前是否处于"已注册"状态。</summary>
    public bool Registered { get; private set; }

    /// <summary>ExecAction 是否指向期望的 exe；用于区分 Exists 与 Matches。</summary>
    public bool ExecutableMatches { get; set; } = true;

    /// <summary>当前定义是否与期望定义一致；用于模拟任务被外部改坏。</summary>
    public bool DefinitionUpToDate { get; set; } = true;

    /// <summary><see cref="RegisterOrUpdate"/> 被调用的次数（含被跳过的那次）。</summary>
    public int RegisterCount { get; private set; }

    /// <summary>真正执行了写入（跳过的那次不计入）的次数，用于判定"只注册一次"。</summary>
    public int WriteCount { get; private set; }

    /// <summary><see cref="Delete"/> 被调用的次数。</summary>
    public int DeleteCount { get; private set; }

    /// <summary>非空时 <see cref="RegisterOrUpdate"/> 抛出它，用于模拟计划任务注册失败。</summary>
    public Exception? RegisterException { get; init; }

    /// <summary>非空时 <see cref="Delete"/> 抛出它。</summary>
    public Exception? DeleteException { get; init; }

    /// <summary>非空时 Exists / Matches 查询抛出它，用于模拟任务状态查询失败。</summary>
    public Exception? QueryException { get; init; }

    /// <inheritdoc />
    public bool Exists()
    {
        if (QueryException is not null)
        {
            throw QueryException;
        }

        return Registered;
    }

    /// <inheritdoc />
    public bool Matches()
    {
        if (QueryException is not null)
        {
            throw QueryException;
        }

        return Registered && ExecutableMatches;
    }

    /// <inheritdoc />
    public bool RegisterOrUpdate()
    {
        RegisterCount++;
        if (RegisterException is not null)
        {
            throw RegisterException;
        }

        // F1 调度端镜像（D113）：已经写过分"一模一样"的固定定义时，跳过重写（返回 false），
        // 模拟真实注册端 IsDefinitionUpToDate 的短路。调度端定义不随设置变化，所以"定义一致"
        // 就不重复写入；DefinitionUpToDate 可显式设为 false 模拟任务被外部改坏。
        if (Registered && DefinitionUpToDate && ExecutableMatches)
        {
            return false;
        }

        WriteCount++;
        Registered = true;
        ExecutableMatches = true;
        DefinitionUpToDate = true;
        return true;
    }

    /// <inheritdoc />
    public void Delete()
    {
        DeleteCount++;
        if (DeleteException is not null)
        {
            throw DeleteException;
        }

        Registered = false;
    }
}
