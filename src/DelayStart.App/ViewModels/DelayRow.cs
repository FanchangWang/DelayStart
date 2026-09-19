using DelayStart.Core.Models;

namespace DelayStart.App.ViewModels;

/// <summary>
/// 「延时启动」页一行。<see cref="DelayedItem"/> 的只读展示包装。
/// </summary>
/// <remarks>
/// 与 <see cref="StartupEntryRow"/> 同样的思路：把文案拼装从 XAML 里挪出来。
/// 区别在于这一页的数据来自 <c>config.json</c>（用户配置）而不是系统扫描结果，
/// 所以行里多了一样东西 —— <see cref="IsStale"/>，"这条配置指向的系统项已经不存在了"。
/// </remarks>
public sealed class DelayRow
{
    /// <summary>构造行。</summary>
    /// <param name="item">配置里的延时条目。</param>
    /// <param name="isStale">该条目对应的系统自启动项是否已不存在（失效）。</param>
    public DelayRow(DelayedItem item, bool isStale)
    {
        ArgumentNullException.ThrowIfNull(item);
        Item = item;
        IsStale = isStale;
    }

    /// <summary>原始条目，供移除 / 编辑取用。</summary>
    public DelayedItem Item { get; }

    /// <summary>显示名。</summary>
    public string Name => Item.Name;

    /// <summary>路径 + 参数拼成的命令行。</summary>
    public string CommandLine => string.IsNullOrWhiteSpace(Item.Arguments)
        ? Item.Path
        : $"{Item.Path}  {Item.Arguments}";

    /// <summary>延时文案，如 <c>登录后 30 秒</c>。</summary>
    public string DelayText => DisplayText.DelayOf(Item.DelaySeconds);

    /// <summary>启动身份文案。</summary>
    public string IdentityText => DisplayText.IdentityOf(Item.RunAsAdmin);

    /// <summary>来源文案。手动条目显示「手动添加」，其余显示系统来源。</summary>
    public string SourceText => DisplayText.SourceOf(Item.Source);

    /// <summary>
    /// 该条目对应的系统项是否已失效（路径不存在 / 任务被删）。
    /// </summary>
    /// <remarks>
    /// 失效条目**仍留在列表里**，只是标注出来 —— 配置里留着它，用户才能看见
    /// "我配过这个，但它没了"并且主动清理。静默丢弃会让用户以为配置丢失。
    /// </remarks>
    public bool IsStale { get; }

    /// <summary>条目级开关状态（FR-4.6）。</summary>
    public bool IsEnabled => Item.Enabled;
}
