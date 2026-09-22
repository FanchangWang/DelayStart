using System.Text.Json;
using System.Text.Json.Serialization;

using DelayStart.Management.Models;

namespace DelayStart.Management.Serialization;

/// <summary>
/// 守卫基线快照的 JSON 上下文（D74）。
/// </summary>
/// <remarks>
/// <para>
/// 单独一份上下文而不是塞进 Core 的 <c>JsonContext</c>：基线是守卫的内部数据，
/// 与用户配置（<c>config.json</c>）和运行归档（<c>runs/</c>）都无关，
/// 混在一起会让"改基线格式"看起来像在改配置格式。
/// </para>
/// <para>
/// 写出侧严格、读入侧宽容（允许注释与尾逗号），与配置的处理口径一致：
/// 用户可能出于好奇打开这个文件，为了一个手改留下的小语法错误就判定"基线损坏"
/// 然后重建，代价是下一次巡检把当前全部条目报成"新增"—— 噪声太大。
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
[JsonSerializable(typeof(GuardBaseline))]
internal sealed partial class GuardJsonContext : JsonSerializerContext
{
}
