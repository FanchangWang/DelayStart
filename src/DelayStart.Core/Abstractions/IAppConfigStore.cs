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
    /// 加载配置。文件**不存在**时返回默认配置（首次运行的正常路径）；文件**存在但读不了或解析不了**时
    /// **抛异常**（E10 / FR-12.3）—— 不返回"看起来正常"的空配置。
    /// </summary>
    /// <returns>配置对象，保证非空且内部集合已规范化。</returns>
    /// <remarks>
    /// 🔴 为什么"读不了"必须抛异常而不是降级：配置里存着"哪些系统自启动项正被本程序软禁用"，
    /// 它是**唯一的还原依据**。损坏后返回一个空配置，等于让程序在"自己什么都不知道"的状态下继续工作 ——
    /// 守卫会安静地什么都不纠正、界面会把所有接管项显示成"未被接管"、任何一次写操作都会把
    /// 仅存的损坏副本覆盖掉。宁可整体拒绝工作（调用方据此中止并提示用户），也不要这种静默的破坏。
    /// </remarks>
    /// <exception cref="StartupOperationException">
    /// 配置损坏（<see cref="StartupFailureReason.ConfigCorrupted"/>，已先保留副本）、
    /// 不可读（<see cref="StartupFailureReason.AccessDenied"/>）或版本高于本程序
    /// （<see cref="StartupFailureReason.ConfigVersionUnsupported"/>）时抛出。
    /// </exception>
    AppConfig Load();

    /// <summary>
    /// 原子保存配置（写 <c>.tmp</c> → <c>File.Replace</c>），保证断电不会留下半截文件
    /// （机制 8 / NFR-2.2）。
    /// </summary>
    /// <param name="config">要保存的配置。</param>
    void Save(AppConfig config);
}
