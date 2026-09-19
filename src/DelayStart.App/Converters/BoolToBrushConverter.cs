using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace DelayStart.App.Converters;

/// <summary>
/// 布尔 → 文字色：<c>true</c> 用警示红，<c>false</c> 返回 <see langword="null"/>（沿用控件默认前景）。
/// </summary>
/// <remarks>
/// 给"状态是否异常"类文字用（调度任务缺失 / 运行记录失败）。返回 null 而不是一份
/// "普通颜色"：x:Bind 绑定 <c>Foreground</c> 得到 null 时控件退回默认前景，
/// 明暗主题各自正确，不必在转换器里猜主题。
/// </remarks>
public sealed class BoolToBrushConverter : IValueConverter
{
    /// <summary>警示红（与状态徽标的"失效"同色系）。</summary>
    private static readonly SolidColorBrush Alert = new()
    {
        Color = Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xD1, 0x34, 0x38),
    };

    /// <inheritdoc />
    public object? Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Alert : null;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
