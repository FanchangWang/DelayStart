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
    /// <summary>当前程序支持的配置版本。</summary>
    public const int CurrentVersion = 2;

    /// <summary>
    /// 配置格式版本。默认值刻意是 <c>0</c> 而不是 <see cref="CurrentVersion"/>：
    /// 只有"本程序自己写出去"或"经 <c>Normalize</c> 补齐"的值才等于当前版本，
    /// 从磁盘读到的配置若**缺这个字段**就会落到 0，从而被识别为"不是本程序的格式"
    /// 而拒绝加载（默认给 2 等于替一份来路不明的文件背书）。
    /// </summary>
    public int Version { get; set; }

    /// <summary>全部延时条目。</summary>
    public List<DelayedItem> Items { get; set; } = [];

    /// <summary>
    /// 用户自定义的**调度周期**（FR-15.10）。条目通过 <c>Items[].scheduleCycleId</c> 引用这里的表项。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 内置五档（每天 / 周一至周五 / 周六日 / 法定工作日 / 法定节假日）**不进这张表** ——
    /// 它们由代码给出（<see cref="BuiltinCycleIds"/>），不可改不可删，写进表反而给了手改配置制造矛盾的机会。
    /// </para>
    /// <para>
    /// 删除策略见 FR-15.14：被引用中的周期不允许删除（管理端拦不住的外部手改会导致引用失效，
    /// 由 <see cref="Services.ScheduleCycleResolver"/> 兜底为「每天」，见 FR-15.15）。
    /// </para>
    /// </remarks>
    public List<ScheduleCycle> Cycles { get; set; } = [];

    /// <summary>全局设置。</summary>
    public Settings Settings { get; set; } = new();
}
