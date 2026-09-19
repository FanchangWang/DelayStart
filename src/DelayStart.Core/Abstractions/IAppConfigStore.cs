using DelayStart.Core.Models;

namespace DelayStart.Core.Abstractions;

/// <summary>
/// 配置读写抽象（NFR-4.2）。默认实现是 <c>ConfigService</c>，
/// 单元测试用假实现替换，避免触碰真实文件系统。
/// </summary>
public interface IAppConfigStore
{
    /// <summary>配置文件完整路径，供设置页「打开配置目录」与诊断使用。</summary>
    string ConfigFilePath { get; }

    /// <summary>
    /// 加载配置。文件不存在时返回默认配置；文件损坏时保留副本并重建（E10 / FR-12.3）；
    /// 低版本时自动迁移（FR-12.1）。
    /// </summary>
    /// <returns>可直接使用的配置对象，保证非空且内部集合已规范化。</returns>
    /// <exception cref="StartupOperationException">
    /// 配置版本高于本程序支持的版本时抛出（<see cref="StartupFailureReason.ConfigVersionUnsupported"/>）——
    /// 此时**拒绝加载**而不是尽力解析，以免把用户配置写坏。
    /// </exception>
    AppConfig Load();

    /// <summary>
    /// 原子保存配置（写 <c>.tmp</c> → <c>File.Replace</c>），保证断电不会留下半截文件
    /// （机制 8 / NFR-2.2）。
    /// </summary>
    /// <param name="config">要保存的配置。</param>
    void Save(AppConfig config);
}
