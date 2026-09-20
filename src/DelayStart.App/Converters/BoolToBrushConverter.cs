using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace DelayStart.App.Converters;

/// <summary>
/// 布尔 → 文字色：<c>true</c> 用警示红，<c>false</c> 返回 <see langword="null"/>。
/// </summary>
/// <remarks>
/// <para>
/// 给"状态是否异常"类文字用（运行记录失败等）。返回 null 而不是一份"普通颜色"，
/// 是为了不在转换器里猜主题。
/// </para>
/// <para>
/// 🔴 <b>只能用在"为真才显示"的那份控件上，不要单独拿它绑 <c>Foreground</c></b>
/// （D42 真机教训）：<c>Foreground</c> 是可继承的依赖属性，显式设成 null 会<b>切断继承链</b>，
/// 控件拿不到任何画刷 —— 文字直接不可见。总览「结果」列原先就这么写，
/// 结果是成功项整列空白、失败项反而有红色文字。正确做法是拆成两份互斥的 TextBlock
/// （见 <c>OverviewPage.xaml</c>）：失败那份用本转换器，其余那份不设 Foreground。
/// </para>
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
