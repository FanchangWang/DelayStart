namespace DelayStart.Core.Models;

/// <summary>
/// 配置文件根对象，对应 <c>%APPDATA%\DelayStart\config.json</c>（D23 / FR-4.8）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Version"/> 是迁移的唯一依据。读到比自己新的版本必须**拒绝加载而不是尽力解析**
/// —— 硬解析未知结构会把用户的配置写坏，而配置里存着"哪些系统自启动项被接管了"，
/// 丢了它就等于丢失了全部还原路径。
/// </para>
/// </remarks>
public sealed class AppConfig
{
    /// <summary>当前程序支持的配置版本。v1 是 demo 格式，v2 是引入 <c>scope</c> 之后的格式。</summary>
    public const int CurrentVersion = 2;

    /// <summary>配置格式版本。</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>全部延时条目。</summary>
    public List<DelayedItem> Items { get; set; } = [];

    /// <summary>全局设置。</summary>
    public Settings Settings { get; set; } = new();
}
