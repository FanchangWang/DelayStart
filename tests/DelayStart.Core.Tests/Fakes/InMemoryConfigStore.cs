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

    /// <inheritdoc />
    public AppConfig Load() => Copy(_config);

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

    /// <summary>取当前存储内容的浅拷贝副本，供断言读取。</summary>
    /// <returns>配置副本。</returns>
    public AppConfig Snapshot() => Copy(_config);

    private static AppConfig Copy(AppConfig source) => new()
    {
        Version = source.Version,
        Items = [.. source.Items],
        Settings = source.Settings,
    };
}
