using DelayStart.Core.Models;
using DelayStart.Management.Abstractions;

namespace DelayStart.Core.Tests.Fakes;

/// <summary>
/// <see cref="IGuardTaskRegistrar"/> 的测试替身 —— **不在系统里创建任何计划任务**。
/// </summary>
/// <remarks>
/// <para>
/// <c>GuardTaskBootstrap</c> 的"缺失即补建 / 档位同步 / 关闭即删除"三个判据都要能测。
/// 直接依赖真实 <c>TaskService</c> 就得往测试机的任务库里写东西 —— 既要提权，又留垃圾
/// （与 <see cref="FakeSchedulerTaskRegistrar"/> 同款理由）。
/// </para>
/// <para>
/// 除了调用计数，还记下最近一次收到的模式与档位：否则"同步了但同步错了档位"
/// 这种缺陷在测试里看不出来（它是本功能最容易出且最不可见的一类错）。
/// </para>
/// </remarks>
internal sealed class FakeGuardTaskRegistrar : IGuardTaskRegistrar
{
    /// <inheritdoc />
    public string TaskPath => @"\DelayStartGuard";

    /// <summary>当前是否处于"已注册"状态。</summary>
    public bool Registered { get; private set; }

    /// <summary>最近一次 <see cref="RegisterOrUpdate"/> 收到的模式。</summary>
    public GuardMode? LastMode { get; private set; }

    /// <summary>最近一次 <see cref="RegisterOrUpdate"/> 收到的档位（分钟）。</summary>
    public int? LastMinutes { get; private set; }

    /// <summary><see cref="RegisterOrUpdate"/> 被调用的次数。</summary>
    public int RegisterCount { get; private set; }

    /// <summary><see cref="Delete"/> 被调用的次数。</summary>
    public int DeleteCount { get; private set; }

    /// <summary>非空时 <see cref="RegisterOrUpdate"/> 抛出它，用于模拟注册失败。</summary>
    public Exception? RegisterException { get; init; }

    /// <summary>非空时 <see cref="Delete"/> 抛出它，用于模拟删除失败。</summary>
    public Exception? DeleteException { get; init; }

    /// <summary>非空时 <see cref="IsRegistered"/> 抛出它，用于模拟状态查询失败。</summary>
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
    public void RegisterOrUpdate(GuardMode mode, int minutes)
    {
        RegisterCount++;
        if (RegisterException is not null)
        {
            throw RegisterException;
        }

        LastMode = mode;
        LastMinutes = minutes;
        Registered = true;
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

    /// <summary>把调用计数清零（"先预置一条任务"的用例里，预置动作不该计入断言）。</summary>
    public void ResetCounters()
    {
        RegisterCount = 0;
        DeleteCount = 0;
    }
}
