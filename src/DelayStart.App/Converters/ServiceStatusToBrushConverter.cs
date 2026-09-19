using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace DelayStart.App.Converters;

/// <summary>
/// 服务运行状态 → 颜色（2026-09-19 批复 11：状态列用不同颜色区分）。
/// </summary>
/// <remarks>
/// 输入是 <c>ServiceInfo.StatusText</c>（"正在运行" / "已暂停" / "正在停止"…），
/// 也可以直接绑 <c>IsRunning</c> 布尔值。颜色与 <see cref="StatusKindToBrushConverter"/>
/// 同一套思路：中明度前景，明暗主题都不刺眼。运行 = 绿、暂停 / 过渡态 = 橙、停止 = 灰。
/// </remarks>
public sealed class ServiceStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Running = Make(0x6C, 0xCB, 0x5F);
    private static readonly SolidColorBrush Transitional = Make(0xE0, 0x8E, 0x0B);
    private static readonly SolidColorBrush Stopped = Make(0x8F, 0x8D, 0x8D);

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        bool running => running ? Running : Stopped,
        "正在运行" => Running,
        "已暂停" or "正在暂停" or "正在停止" => Transitional,
        _ => Stopped,
    };

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();

    private static SolidColorBrush Make(byte r, byte g, byte b)
        => new() { Color = Microsoft.UI.ColorHelper.FromArgb(0xFF, r, g, b) };
}
