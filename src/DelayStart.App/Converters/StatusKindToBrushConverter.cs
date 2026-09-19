using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace DelayStart.App.Converters;

/// <summary>
/// 状态徽标种类 → 颜色（D1：状态徽标化）。
/// </summary>
/// <remarks>
/// <para>
/// 输入是 <see cref="ViewModels.StartupEntryRow.StatusKind"/>（"protected"/"missing"/
/// "taken"/"enabled"/"disabled"），用 <c>ConverterParameter</c> 选前景 / 背景：
/// <c>fg</c> 返回文字色，其余（含缺省）返回药丸底色。
/// </para>
/// <para>
/// 颜色刻意选**中明度前景 + 半透明背景**：不读主题资源（转换器拿不到可靠的
/// <c>Application.Current</c> 时机），靠半透明底在明暗两套主题下都不刺眼；
/// 文字色是各自语义的通用色（接管=琥珀、失效=红、受保护=灰紫、禁用=灰、启用=蓝）。
/// </para>
/// </remarks>
public sealed class StatusKindToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush TakenForeground = Make(0xE0, 0x8E, 0x0B);
    private static readonly SolidColorBrush TakenBackground = Make(0x2E, 0xE0, 0x8E, 0x0B);
    private static readonly SolidColorBrush MissingForeground = Make(0xD1, 0x34, 0x38);
    private static readonly SolidColorBrush MissingBackground = Make(0x2E, 0xD1, 0x34, 0x38);
    private static readonly SolidColorBrush ProtectedForeground = Make(0x8A, 0x7F, 0xA0);
    private static readonly SolidColorBrush ProtectedBackground = Make(0x2E, 0x8A, 0x7F, 0xA0);
    private static readonly SolidColorBrush DisabledForeground = Make(0x8F, 0x8D, 0x8D);
    private static readonly SolidColorBrush DisabledBackground = Make(0x22, 0x8F, 0x8D, 0x8D);
    private static readonly SolidColorBrush EnabledForeground = Make(0x2B, 0x88, 0xD8);
    private static readonly SolidColorBrush EnabledBackground = Make(0x1F, 0x2B, 0x88, 0xD8);

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var kind = value as string ?? string.Empty;
        var wantForeground = string.Equals(parameter as string, "fg", StringComparison.Ordinal);

        return kind switch
        {
            "taken" => wantForeground ? TakenForeground : TakenBackground,
            "missing" => wantForeground ? MissingForeground : MissingBackground,
            "protected" => wantForeground ? ProtectedForeground : ProtectedBackground,
            "disabled" => wantForeground ? DisabledForeground : DisabledBackground,
            _ => wantForeground ? EnabledForeground : EnabledBackground,
        };
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();

    private static SolidColorBrush Make(byte r, byte g, byte b) => new() { Color = Microsoft.UI.ColorHelper.FromArgb(0xFF, r, g, b) };

    private static SolidColorBrush Make(byte a, byte r, byte g, byte b) => new() { Color = Microsoft.UI.ColorHelper.FromArgb(a, r, g, b) };
}
