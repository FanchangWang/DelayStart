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

    /// <summary>非空时 <see cref="IsRegistered"/> 抛出它，用于模拟任务状态查询失败。</summary>
    public Exception? QueryException { get; init; }

    /// <inheritdoc />
    public bool IsRegistered()
    {
        if (QueryException is not null)
        {
            throw QueryException;
        }

        return Registered;
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
        // 模拟真实注册端 IsDefinitionUpToDate 的短路。调度端定义不随设置变化，所以"已存在"
        // 就等价于"定义一致"，无需重复写入。
        if (Registered)
        {
            return false;
        }

        WriteCount++;
        Registered = true;
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
