using DelayStart.App.Services;
using DelayStart.Core.Models;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DelayStart.App.Controls;

/// <summary>
/// 周一至周日七宫格（FR-15 周期定义共用控件）。
/// </summary>
/// <remarks>
/// <para>
/// 只有一副实现，两种模式：<b>可编辑</b>（新建 / 编辑周期）与<b>只读</b>
/// （延时编辑弹窗里展示"这个周期包含哪些天"，FR-15.23）。
/// 只读模式不是"另一套画法"，而是同一批 <see cref="ToggleButton"/> 关掉命中测试 ——
/// 两套画法迟早会在某个主题下长得不一样，而用户看到的必须是同一个东西。
/// </para>
/// <para>
/// 🔴 格子顺序是<b>周一在左</b>，位序是 <see cref="WeekdaySet"/> 定义的
/// 周一 = bit0（<b>不是</b> <see cref="DayOfWeek"/> 的周日 = 0）。
/// 两者的换算只经 <see cref="WeekdaySets"/>，别在这里手写位移。
/// </para>
/// <para>
/// 用纯代码构造而不是 XAML：这个控件的全部内容就是七个一样的格子，
/// 写成 XAML 要摊开七份声明，还要在各宿主里再复制一遍 —— 收益为零。
/// </para>
/// </remarks>
public sealed class WeekdayGrid : StackPanel
{
    /// <summary>七个格子的位序（自左向右）。</summary>
    private static readonly WeekdaySet[] Flags =
    [
        WeekdaySet.Monday,
        WeekdaySet.Tuesday,
        WeekdaySet.Wednesday,
        WeekdaySet.Thursday,
        WeekdaySet.Friday,
        WeekdaySet.Saturday,
        WeekdaySet.Sunday,
    ];

    /// <summary>格子里显示的字（一…日，不带「周」——宽度只够一个字）。</summary>
    private static readonly string[] Glyphs = ["一", "二", "三", "四", "五", "六", "日"];

    private readonly ToggleButton[] _cells = new ToggleButton[7];
    private readonly TextBlock[] _checks = new TextBlock[7];

    /// <summary>回灌期守卫：程序性置位不应触发 <see cref="DaysChanged"/>。</summary>
    private bool _syncing;

    private WeekdaySet _days = WeekdaySet.None;
    private bool _readOnly;

    /// <summary>构造七宫格。</summary>
    public WeekdayGrid()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 6;

        for (var index = 0; index < Flags.Length; index++)
        {
            var check = new TextBlock
            {
                Text = "✓",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };

            var caption = new TextBlock
            {
                Text = Glyphs[index],
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var content = new StackPanel { Spacing = 0 };
            content.Children.Add(caption);
            content.Children.Add(check);

            var cell = new ToggleButton
            {
                Content = content,
                Tag = Flags[index],
                Width = 42,
                Height = 46,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(0),
            };

            ToolTipService.SetToolTip(cell, CycleInfoProvider.NameOfDay(DayOfWeekOf(Flags[index])));
            cell.Checked += OnCellToggled;
            cell.Unchecked += OnCellToggled;

            _cells[index] = cell;
            _checks[index] = check;
            Children.Add(cell);
        }

        Paint();
    }

    /// <summary>星期集合发生变化（用户点击，且非程序性回灌）。</summary>
    public event EventHandler? DaysChanged;

    /// <summary>当前选中的星期集合。赋值会重画且**不**触发 <see cref="DaysChanged"/>。</summary>
    public WeekdaySet Days
    {
        get => _days;
        set
        {
            _days = WeekdaySets.Sanitize(value);
            Paint();
        }
    }

    /// <summary>
    /// 只读模式：格子照常显示选中态，但不接受点击 / 键盘。
    /// </summary>
    /// <remarks>
    /// 不用 <c>IsEnabled = false</c>：那会把格子画灰，与"这个周期没选中这天"
    /// 在视觉上撞车 —— 只读要表达的是"不能改"，不是"没选"。
    /// </remarks>
    public bool IsReadOnly
    {
        get => _readOnly;
        set
        {
            _readOnly = value;
            foreach (var cell in _cells)
            {
                cell.IsHitTestVisible = !value;
                cell.IsTabStop = !value;
            }
        }
    }

    private static DayOfWeek DayOfWeekOf(WeekdaySet flag) => flag switch
    {
        WeekdaySet.Monday => DayOfWeek.Monday,
        WeekdaySet.Tuesday => DayOfWeek.Tuesday,
        WeekdaySet.Wednesday => DayOfWeek.Wednesday,
        WeekdaySet.Thursday => DayOfWeek.Thursday,
        WeekdaySet.Friday => DayOfWeek.Friday,
        WeekdaySet.Saturday => DayOfWeek.Saturday,
        _ => DayOfWeek.Sunday,
    };

    private void OnCellToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing || _readOnly || sender is not ToggleButton { Tag: WeekdaySet flag } cell)
        {
            return;
        }

        _days = cell.IsChecked == true ? _days | flag : _days & ~flag;

        // ✓ 只跟着一个格子的选中态走，不必整盘重画。
        for (var index = 0; index < Flags.Length; index++)
        {
            _checks[index].Visibility = _cells[index].IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        DaysChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Paint()
    {
        _syncing = true;
        for (var index = 0; index < Flags.Length; index++)
        {
            var selected = (_days & Flags[index]) == Flags[index];
            _cells[index].IsChecked = selected;
            _checks[index].Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }

        _syncing = false;
    }
}
