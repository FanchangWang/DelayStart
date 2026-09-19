using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Foundation;

namespace DelayStart.App.Controls;

/// <summary>
/// 横向流式换行面板：子项按自身内容宽度排成一行，放不下就换行。
/// </summary>
/// <remarks>
/// <para>
/// WinUI 3 没有内置 <c>WrapPanel</c>：<c>WrapGrid</c> / <c>ItemsWrapGrid</c> 都按**统一
/// 单元格**布局，会把宽度不同的胶囊裁齐（2026-09-20 批复 5 明确不要）。本面板按内容
/// 测量排布，用于延时胶囊 / 预设胶囊 —— 宽度自适应 + 超宽自动换行（Round H 批复 5/7）。
/// </para>
/// </remarks>
public sealed class WrapPanel : Panel
{
    /// <summary>子项间距（水平与行间一致）。</summary>
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing),
        typeof(double),
        typeof(WrapPanel),
        new PropertyMetadata(0d, OnSpacingChanged));

    /// <summary>子项间距。</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private static void OnSpacingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((WrapPanel)sender).InvalidateMeasure();

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var lineWidth = 0d;
        var lineHeight = 0d;
        var totalHeight = 0d;
        var maxLineWidth = 0d;

        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var childWidth = child.DesiredSize.Width;
            var childHeight = child.DesiredSize.Height;

            if (lineWidth > 0 && lineWidth + Spacing + childWidth > availableSize.Width)
            {
                totalHeight += lineHeight + Spacing;
                maxLineWidth = Math.Max(maxLineWidth, lineWidth);
                lineWidth = 0;
                lineHeight = 0;
            }

            if (lineWidth > 0)
            {
                lineWidth += Spacing;
            }

            lineWidth += childWidth;
            lineHeight = Math.Max(lineHeight, childHeight);
        }

        totalHeight += lineHeight;
        maxLineWidth = Math.Max(maxLineWidth, lineWidth);

        var width = double.IsInfinity(availableSize.Width)
            ? maxLineWidth
            : Math.Min(availableSize.Width, maxLineWidth);
        return new Size(width, totalHeight);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0d;
        var y = 0d;
        var rowHeight = 0d;

        foreach (var child in Children)
        {
            var childWidth = child.DesiredSize.Width;
            var childHeight = child.DesiredSize.Height;

            if (x > 0 && x + Spacing + childWidth > finalSize.Width)
            {
                y += rowHeight + Spacing;
                x = 0;
                rowHeight = 0;
            }

            if (x > 0)
            {
                x += Spacing;
            }

            child.Arrange(new Rect(x, y, childWidth, childHeight));
            x += childWidth;
            rowHeight = Math.Max(rowHeight, childHeight);
        }

        return finalSize;
    }
}
