using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace DelayStart.App.Converters;

/// <summary>
/// 字符串非空 → <see cref="Visibility.Visible"/>，空白 → <see cref="Visibility.Collapsed"/>。
/// </summary>
/// <remarks>
/// 服务页"描述与路径合并列"用：描述非空才显示描述行，为空只留路径行；
/// 组策略 / Winlogon 的空提示也用它（提示文案为空串 = 不显示）。
/// </remarks>
public sealed partial class NonEmptyToVisibilityConverter : IValueConverter
{
    /// <summary>把字符串转成可见性。</summary>
    /// <param name="value">字符串。</param>
    /// <param name="targetType">目标类型，应为 <see cref="Visibility"/>。</param>
    /// <param name="parameter">未使用。</param>
    /// <param name="language">未使用。</param>
    /// <returns>非空白文本为 <see cref="Visibility.Visible"/>，否则 <see cref="Visibility.Collapsed"/>。</returns>
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is string text && text.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>不支持反向转换（绑定均为单向）。</summary>
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => Visibility.Collapsed;
}
