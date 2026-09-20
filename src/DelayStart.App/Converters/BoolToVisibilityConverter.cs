using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace DelayStart.App.Converters;

/// <summary>
/// <see cref="bool"/> → <see cref="Visibility"/> 转换器。
/// </summary>
/// <remarks>
/// <para>
/// <c>x:Bind</c> 不会自动把布尔值转成 <see cref="Visibility"/>（UWP 时代的隐式转换在
/// WinUI 3 里不可依赖），所以凡是把"能不能操作"直接映射成"控件显不显示"的地方都要走这里。
/// </para>
/// <para>
/// 反向转换按"可见即 true"处理：绑定目标是 <c>TwoWay</c> 的开关类控件时不会出错。
/// </para>
/// </remarks>
public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>把布尔值转成可见性。</summary>
    /// <param name="value">布尔值。</param>
    /// <param name="targetType">目标类型，应为 <see cref="Visibility"/>。</param>
    /// <param name="parameter">
    /// 传 <c>"Negate"</c> 时反转（true → Collapsed）。用于"同一个位置放两种互斥呈现"的场景，
    /// 例如总览「结果」列：失败用红色那份、其余用默认前景那份。
    /// </param>
    /// <param name="language">未使用。</param>
    /// <returns><see cref="Visibility.Visible"/> 或 <see cref="Visibility.Collapsed"/>。</returns>
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool value2 && value2;
        if (string.Equals(parameter as string, "Negate", StringComparison.Ordinal))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>把可见性转回布尔值。</summary>
    /// <param name="value"><see cref="Visibility"/> 值。</param>
    /// <param name="targetType">目标类型，应为 <see cref="bool"/>。</param>
    /// <param name="parameter">未使用。</param>
    /// <param name="language">未使用。</param>
    /// <returns>是否可见。</returns>
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility visibility && visibility == Visibility.Visible;
}
