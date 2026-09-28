using System.Text.Json;
using System.Text.Json.Serialization;

using DelayStart.Core.Models;

namespace DelayStart.Core.Serialization;

/// <summary>
/// 配置文件的 JSON 上下文（FR-4.8 / FR-12）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **刻意与 <see cref="JsonContext"/> 分开**，只为把「未映射字段」这一个选项独立出来：
/// <c>UnmappedMemberHandling = Disallow</c> 意味着配置里出现一个本程序不认识的字段就
/// **直接报错**，而不是把它静默忽略。
/// </para>
/// <para>
/// 为什么配置需要这个严格度：项目不做旧格式迁移（见 <c>DelayStart.Core.Services.ConfigService</c>），
/// 没有迁移就意味着「写错字段名」没有任何兜底 —— 少一个字段会被
/// <c>Normalize</c> 补成默认值并**写回磁盘**，用户配的延时静默变成 30 秒。
/// 立刻报错让人看见，是这里唯一有意义的失败方式。
/// </para>
/// <para>
/// 为什么运行归档（<see cref="RunRecord"/>）**不能**跟着加严：<c>RunStateService.ReadRecent</c>
/// 对解析失败是「跳过该文件并记一条 Warn」。一旦加严，来自更高版本、多了字段的归档
/// 会被整条跳过 —— 用户看到的是调度日志里**历史凭空消失**，那是比配置报错坏得多的故障。
/// 故 <see cref="JsonContext"/> 保持宽松。
/// </para>
/// <para>
/// 其余选项（camelCase / 注释 / 尾逗号 / 大小写不敏感）与 <see cref="JsonContext"/> 一致：
/// 读入侧宽容小语法错误（手改配置很常见），但对**不认识的字段**不再宽容。
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AppConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext
{
}
