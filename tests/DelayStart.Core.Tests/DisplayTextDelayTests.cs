using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="DisplayText.DelayOf"/> 的单元测试。
/// </summary>
/// <remarks>
/// 🔴 这份格式是 **2026-10-02 用户拍定的**（<c>nn秒</c> / <c>nn分钟</c> / <c>nn分mm秒</c>，
/// 不带空格），而延时启动页的**列宽是按这份格式算出来的** —— 改了格式不改列宽，
/// 或者反过来，就会重新出现"字被裁掉"。
/// <para>
/// 这组用例当初写不了，因为 <c>DisplayText</c> 住在 App 层，而测试工程不能引用 App
/// （硬约束）。它因此至今零测试 —— 而这一轮用户报的问题里就有一条是"静态验不出的界面行为"。
/// v0.6.1 已把整个 <see cref="DisplayText"/> 搬进 Core（它只依赖 <c>Core.Models</c>），
/// 才有了这份测试。
/// </para>
/// </remarks>
public sealed class DisplayTextDelayTests
{
    [Theory]
    [InlineData(0, "立即")]
    [InlineData(-1, "立即")]
    [InlineData(1, "1秒")]
    [InlineData(30, "30秒")]
    [InlineData(59, "59秒")]
    public void BelowOneMinute_RendersSecondsOnly(int seconds, string expected)
        => Assert.Equal(expected, DisplayText.DelayOf(seconds));

    [Theory]
    [InlineData(60, "1分钟")]
    [InlineData(120, "2分钟")]
    [InlineData(300, "5分钟")]
    [InlineData(600, "10分钟")]
    public void WholeMinutes_Renders分钟(int seconds, string expected)
        => Assert.Equal(expected, DisplayText.DelayOf(seconds));

    [Theory]
    [InlineData(61, "1分1秒")]
    [InlineData(90, "1分30秒")]
    [InlineData(150, "2分30秒")]
    [InlineData(599, "9分59秒")]
    [InlineData(660, "11分钟")]
    public void MixedMinutesAndSeconds(int seconds, string expected)
        => Assert.Equal(expected, DisplayText.DelayOf(seconds));

    [Fact]
    public void NoSpaces_Anywhere()
    {
        // 🔴 列宽是按"无空格"算的。悄悄加回一个空格（哪怕只是好看）就会让列宽失准，
        // 而这种改动在界面上看不出来 —— 只在数字够大时才露馅。
        foreach (var seconds in new[] { 1, 59, 60, 61, 150, 599, 600, 3599, 604799 })
        {
            Assert.DoesNotContain(' ', DisplayText.DelayOf(seconds));
        }
    }

    [Fact]
    public void WorstCaseAtTheEditorsUpperBound_FitsTheColumnBudget()
    {
        // 🔴 列宽 88px 是照这个值算的：编辑器允许的最大延时
        // （DelayEditorDialog.FallbackMaxDelay = 604800 秒 = 10080 分钟）
        // 减去 1 秒再拆开 —— 「10079分59秒」是所有可能输出里最宽的一个。
        const int EditorMaxDelay = 604800;

        var widest = DisplayText.DelayOf(EditorMaxDelay - 1);

        Assert.Equal("10079分59秒", widest);

        // 逐字段量：3 个数字组 + 2 个汉字（14px 正文字体下 数字≈7.8px、汉字≈14px）。
        var digits = widest.Count(static ch => ch is >= '0' and <= '9');
        var hanzi = widest.Count(static ch => ch is > (char)127);

        Assert.Equal(7, digits);
        Assert.Equal(2, hanzi);

        var estimatedWidth = (digits * 7.8) + (hanzi * 14);
        Assert.True(
            estimatedWidth <= 88,
            $"最宽输出『{widest}』估算 {estimatedWidth:F1}px，超出 88px 列宽预算");
    }

    [Fact]
    public void TypicalValues_LeaveGenerousRoomInTheColumn()
    {
        // 反向：绝大多数条目是两位数秒或个位数分钟，不该因为"照顾 7 天上界"而占着宽列。
        foreach (var seconds in new[] { 0, 5, 10, 15, 20, 30, 60, 150, 300 })
        {
            var text = DisplayText.DelayOf(seconds);
            var estimated = (text.Count(static ch => ch is >= '0' and <= '9') * 7.8)
                + (text.Count(static ch => ch is > (char)127) * 14);

            Assert.True(estimated <= 60, $"`{text}` 估算 {estimated:F1}px，对个位数分钟来说太宽了");
        }
    }
}
