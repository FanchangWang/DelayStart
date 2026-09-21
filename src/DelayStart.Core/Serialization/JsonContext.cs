using System.Text.Json;
using System.Text.Json.Serialization;

using DelayStart.Core.Launch;
using DelayStart.Core.Models;

namespace DelayStart.Core.Serialization;

/// <summary>
/// 源生成的 JSON 序列化上下文（FR-5.14）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **全部 JSON 读写必须走这里**，禁止使用
/// <c>JsonSerializer.Serialize&lt;T&gt;(value, new JsonSerializerOptions())</c> 这类反射式调用 ——
/// 反射序列化在 NativeAOT 裁剪后会失效，而 <c>Core</c> 层的 <c>IsAotCompatible=true</c>
/// 会让这类调用在构建期直接报 <c>IL2026</c> / <c>IL3050</c>（R5 守门）。
/// </para>
/// <para>
/// 命名策略固定 camelCase（与 <c>docs/design.md</c> 7.4 的示例一致）；
/// 枚举序列化为**字符串**而非数字，因为配置文件是给人看的，也是跨版本演进时更稳的格式。
/// </para>
/// <para>
/// 读入侧刻意宽容（允许注释与尾逗号、大小写不敏感）：用户可能手改过配置文件，
/// 为一个小语法错误就判定"配置损坏"并重建，代价太大。写出侧则严格。
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(RunRecord))]
[JsonSerializable(typeof(LegacyConfigV1))]
internal sealed partial class JsonContext : JsonSerializerContext
{
}

/// <summary>
/// UIAccess 中转器（<c>DelayStart.LaunchBroker.exe</c>）作业/结果的 JSON 上下文（D70）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="JsonContext"/> 分开成<b>公开</b>上下文，因为中转器是独立 AOT 工程
/// （引用 Core 但不进调度端程序集），作业与结果两端都要用同一份 schema。
/// 紧凑写出（WriteIndented=false）：作业/结果是机器间交接，不是给人看的。
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BrokerLaunchJob))]
[JsonSerializable(typeof(BrokerLaunchResult))]
public sealed partial class BrokerJsonContext : JsonSerializerContext
{
}
