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
    /// 加载配置并保留加载状态。只读调用方可以据此区分“确实没有配置”和“配置暂时不可用”；
    /// 写操作应使用 <see cref="LoadForMutation"/>。
    /// </summary>
    /// <returns>非空配置快照及其加载状态。</returns>
    /// <exception cref="StartupOperationException">配置版本高于本程序支持的版本时抛出。</exception>
    ConfigLoadResult LoadResult();

    /// <summary>
    /// 加载配置的只读兼容入口。文件不存在或正常时返回配置；损坏 / 暂不可用时返回
    /// 一个空快照，调用方不得据此执行写入或恢复操作。
    /// </summary>
    /// <returns>可直接用于读取的配置对象，保证非空且内部集合已规范化。</returns>
    AppConfig Load();

    /// <summary>
    /// 为写入、删除或系统恢复加载配置。配置缺失是合法的空配置；配置损坏或暂不可用时
    /// 必须抛出语义异常，禁止把“不知道”当成“没有”。
    /// </summary>
    /// <returns>可安全用于变更的配置快照。</returns>
    /// <exception cref="StartupOperationException">配置损坏、暂不可用或版本不受支持时抛出。</exception>
    AppConfig LoadForMutation();

    /// <summary>
    /// 原子保存配置（写 <c>.tmp</c> → <c>File.Replace</c>），保证断电不会留下半截文件
    /// （机制 8 / NFR-2.2）。
    /// </summary>
    /// <param name="config">要保存的配置。</param>
    void Save(AppConfig config);
}
