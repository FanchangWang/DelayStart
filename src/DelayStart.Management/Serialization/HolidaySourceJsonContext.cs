using System.Text.Json;
using System.Text.Json.Serialization;

using DelayStart.Core.Serialization;

namespace DelayStart.Management.Serialization;

/// <summary>
/// 第三方节假日源文件的 JSON 上下文（区别于本地归一化文件的 <see cref="HolidayJsonContext"/>）。
/// </summary>
/// <remarks>
/// 单独一份上下文：读入的是**别人的 schema**，随时可能变；落盘用的是**我们的 schema**，
/// 由 <see cref="HolidayJsonContext"/> 定义。两者混在一起的话，"改了源解析"
/// 和"改了落盘格式"会看起来是同一件事。
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(HolidaySource))]
internal sealed partial class HolidaySourceJsonContext : JsonSerializerContext
{
}
