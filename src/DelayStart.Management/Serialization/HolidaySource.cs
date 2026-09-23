using System.Text.Json.Serialization;

namespace DelayStart.Management.Serialization;

/// <summary>
/// holiday-cn 源文件（<c>{year}.json</c>）的**只读**结构，只取用得上的三个字段。
/// </summary>
/// <remarks>
/// <para>
/// 这是第三方 schema 的唯一落点。它明天改字段名、多加一层，改这里就够了
/// —— 判定代码与本地落盘格式（<c>HolidayCalendarDocument</c>）都不受影响。
/// 多出来的字段（比如 <c>papers</c>）我们完全不读时也不会报错：
/// 源生成反序列化对未声明的成员直接忽略。
/// </para>
/// </remarks>
public sealed class HolidaySource
{
    /// <summary>年份，用于与请求的年份交叉校验。</summary>
    [JsonPropertyName("year")]
    public int Year { get; set; }

    /// <summary>国务院公告原文链接。取第一条作为来源出处。</summary>
    [JsonPropertyName("papers")]
    public List<string>? Papers { get; set; }

    /// <summary>只列**特殊日**：放假日与调休上班日，普通周末不在里面。</summary>
    [JsonPropertyName("days")]
    public List<HolidaySourceDay>? Days { get; set; }
}

/// <summary>
/// 源文件里的一天。
/// </summary>
public sealed class HolidaySourceDay
{
    /// <summary>节日名（如「中秋节」）。🔴 **不进本地数据**：两个源对同一天的归属标注不一致。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>日期，<c>yyyy-MM-dd</c>。</summary>
    [JsonPropertyName("date")]
    public string? Date { get; set; }

    /// <summary>true = 休息；false = 调休上班。</summary>
    [JsonPropertyName("isOffDay")]
    public bool IsOffDay { get; set; }
}
