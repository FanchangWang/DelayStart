namespace DelayStart.Core.Serialization;

/// <summary>
/// v1 配置里的单个延时条目（**只读，仅用于迁移**）。
/// </summary>
/// <remarks>
/// 字段严格对应 demo 的 <c>StartupItem</c>，不要在迁移过程中"顺手补字段" ——
/// 迁移的职责是**搬运**，补齐默认值是 <c>ConfigService</c> 的事。
/// </remarks>
internal sealed class LegacyItemV1
{
    /// <summary>v1 的条目 ID（demo 用随机 Guid，不稳定）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>目标路径。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>启动参数（v2 中改名为 <c>arguments</c>）。</summary>
    public string Args { get; set; } = string.Empty;

    /// <summary>延时秒数。</summary>
    public int DelaySeconds { get; set; }

    /// <summary>是否以管理员身份启动。</summary>
    public bool RunAsAdmin { get; set; }

    /// <summary>同延时内的顺序。</summary>
    public int SortOrder { get; set; }

    /// <summary>来源标识：<c>registry</c> / <c>startup_folder</c> / <c>scheduled_task</c> / <c>uwp</c>。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>位置描述。v2 的 <c>scope</c> 就从这个字符串**一次性推断**出来（见 ConfigService）。</summary>
    public string SourceDetail { get; set; } = string.Empty;

    /// <summary>来源内的原始键。</summary>
    public string SourceKeyName { get; set; } = string.Empty;
}
