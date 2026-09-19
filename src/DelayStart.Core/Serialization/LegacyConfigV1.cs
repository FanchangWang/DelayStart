namespace DelayStart.Core.Serialization;

/// <summary>
/// demo 时代的 v1 配置格式（**只读，仅用于迁移**，FR-12.1）。
/// </summary>
/// <remarks>
/// <para>
/// 单独定义 DTO 而不是让 v1 直接反序列化进 <c>AppConfig</c>，是因为两者的
/// <c>items[]</c> 结构确有差异：v1 用 <c>args</c> / <c>sourceKeyName</c>，
/// 且**没有** <c>scope</c> / <c>enabled</c> / <c>originalState</c>。
/// 硬塞进 v2 模型会让"字段缺失"和"字段为空"两种状态无法区分。
/// </para>
/// <para>
/// ⚠️ demo 的序列化选项没设命名策略，因此真实 v1 文件的属性名是 **PascalCase**。
/// 迁移用的 <c>JsonContext</c> 开了 <c>PropertyNameCaseInsensitive</c> 以同时兼容两种写法。
/// </para>
/// </remarks>
internal sealed class LegacyConfigV1
{
    /// <summary>v1 版本号。老文件里可能缺失，反序列化后按 1 处理。</summary>
    public int Version { get; set; } = 1;

    /// <summary>v1 的延时条目集合。</summary>
    public List<LegacyItemV1> Items { get; set; } = [];
}
