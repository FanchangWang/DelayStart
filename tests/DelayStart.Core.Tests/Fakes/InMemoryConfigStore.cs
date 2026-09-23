using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;

namespace DelayStart.Core.Tests.Fakes;

/// <summary>
/// <see cref="IAppConfigStore"/> 的内存实现 —— 不碰文件系统。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Load"/> 刻意返回**新的对象与新的列表**，而不是把内部实例直接交出去。
/// 真实的 <c>ConfigService</c> 每次都从磁盘重新解析，调用方拿到的永远是一份独立快照；
/// 替身若共享同一个列表，接管服务的回滚逻辑（"重新 Load 一遍再删条目"）就会因为
/// 看到的是同一份被改过的数据而**假通过**。
/// </para>
/// </remarks>
internal sealed class InMemoryConfigStore : IAppConfigStore
{
    private AppConfig _config = new();

    /// <inheritdoc />
    public string ConfigFilePath => "(内存)";

    /// <summary><see cref="Save"/> 被调用的次数。</summary>
    public int SaveCount { get; private set; }

    /// <summary>非空时 <see cref="Save"/> 抛出它，用于模拟配置写入失败。</summary>
    public Exception? SaveException { get; init; }

    /// <summary>用给定配置初始化（会做一次深拷贝，避免测试持有同一份引用）。</summary>
    /// <param name="config">初始配置。</param>
    public void Seed(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = Copy(config);
    }

    /// <summary>非空时 <see cref="Load"/> 抛出它（模拟"配置版本高于本程序"这类拒绝加载的场景）。</summary>
    public Exception? LoadException { get; init; }

    /// <inheritdoc />
    public AppConfig Load()
    {
        if (LoadException is not null)
        {
            throw LoadException;
        }

        return Copy(_config);
    }

    /// <inheritdoc />
    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        SaveCount++;

        if (SaveException is not null)
        {
            throw SaveException;
        }

        _config = Copy(config);
    }

    /// <summary>取当前存储内容的副本，供断言读取。</summary>
    /// <returns>配置副本。</returns>
    public AppConfig Snapshot() => Copy(_config);

    private static AppConfig Copy(AppConfig source) => new()
    {
        Version = source.Version,
        Items = [.. source.Items],
        Cycles = CopyCycles(source.Cycles),
        Settings = CopySettings(source.Settings),
    };

    /// <summary>深拷贝 <c>cycles</c> 节点。</summary>
    /// <remarks>
    /// 🔴 与 <see cref="Settings"/> 同理：<see cref="ScheduleCycle"/> 是**可变类**（有 setter），
    /// 浅拷贝列表会让"调用方手里的周期"与"存储里的周期"变成同一个对象。
    /// <para>
    /// ⚠️ **2026-09-23 补**：FR-15 给 <see cref="AppConfig"/> 加 <c>Cycles</c> 字段时，
    /// 这个替身漏了同步。后果不是"少测一个字段"，而是**所有经替身的周期用例都跑在空周期表上** ——
    /// 表现为"重名校验拦不住、AddCycle 写进去的周期在 Save 时凭空消失"，而判据本身的单元测试
    /// 全是绿的（它不经过替身），很容易误判成被测代码有问题。
    /// ⇒ 凡 <see cref="AppConfig"/> 新增集合 / 引用字段，<see cref="Copy"/> 里必须跟着加一行。
    /// </para>
    /// </remarks>
    private static List<ScheduleCycle> CopyCycles(List<ScheduleCycle> source)
    {
        var copy = new List<ScheduleCycle>(source.Count);
        foreach (var cycle in source)
        {
            // 忠实保留 null 元素：配置是外部可改的文件，"表里有半个对象"是真实可能出现的情形，
            // 替身不该替被测代码把它抹平（被测代码自己会判断）。
            copy.Add(cycle is null
                ? null!
                : new ScheduleCycle { Id = cycle.Id, Name = cycle.Name, Days = cycle.Days });
        }

        return copy;
    }

    /// <summary>深拷贝 <c>settings</c> 节点。</summary>
    /// <remarks>
    /// 🔴 <see cref="AppConfig.Items"/> 的元素是不可变记录，浅拷贝列表就够；
    /// 但 <see cref="Settings"/> 是**可变类**，共享引用会让"调用方改了手里的配置"
    /// 与"存储里的配置被改"变成同一件事 —— 于是「Save 抛异常时状态不该变化」
    /// 这类断言会假通过（2026-09-21 实测踩到）。
    /// 新增 Settings 字段时必须同步补在这里。
    /// </remarks>
    private static Settings CopySettings(Settings source) => new()
    {
        DelayPresets = [.. source.DelayPresets],
        DefaultPreset = source.DefaultPreset,
        NotifyMode = source.NotifyMode,
        RetryCount = source.RetryCount,
        Theme = source.Theme,
        LastRunId = source.LastRunId,
        GuardMode = source.GuardMode,
        GuardMinutes = source.GuardMinutes,
        GuardNotifyMode = source.GuardNotifyMode,
    };
}
