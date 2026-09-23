using DelayStart.Core.Models;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DelayStart.App.Controls;

/// <summary>
/// 新建 / 编辑周期的表单内容（FR-15.10 / FR-15.20）：<b>只有</b>名称与七宫格两样输入。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 面板内**不放任何常驻提示**（2026-09-23 批复 10）：没有"至少选一天"的灰字说明、
/// 没有未来 7 天预览、没有小贴士。错了才在对应字段下方红字报错，改正后红字立即消失 ——
/// 这是"让用户先试、错了当场说"，比提前把按钮锁死省一次猜测。
/// </para>
/// <para>
/// 本类只是**内容**，不含标题栏与按钮行 —— 它有两个宿主（设置页的真 <c>ContentDialog</c>、
/// 延时编辑弹窗内的同层 Overlay，见 design.md FR-15「管理端呈现」），
/// 按钮归各自的宿主管。抽在同一处是为了保证两处长得一模一样。
/// </para>
/// </remarks>
public sealed class CycleEditorForm : StackPanel
{
    /// <summary>名称错误文案（FR-15.20）。</summary>
    public const string NameErrorText = "请填写周期名称。";

    /// <summary>名称重复的错误文案（2026-09-23 批复 14）。</summary>
    public const string NameTakenErrorText = "已有同名周期，换一个名字。";

    /// <summary>星期错误文案（FR-15.20）。</summary>
    public const string DaysErrorText = "至少选择一天，否则无法保存。";

    private readonly TextBox _nameBox;
    private readonly TextBlock _nameError;
    private readonly WeekdayGrid _grid;
    private readonly TextBlock _daysError;

    /// <summary>构造表单。</summary>
    public CycleEditorForm()
    {
        Spacing = 14;

        _nameError = CreateErrorBlock(NameErrorText);
        _nameBox = new TextBox
        {
            Header = "名称",
            PlaceholderText = "例如：上一休一",
            MaxLength = 20,
        };
        _nameBox.TextChanged += (_, _) => Hide(_nameError);

        var dayLabel = new TextBlock { Text = "包含哪些天" };
        _daysError = CreateErrorBlock(DaysErrorText);
        _grid = new WeekdayGrid();
        _grid.DaysChanged += (_, _) => Hide(_daysError);

        var nameBlock = new StackPanel { Spacing = 4 };
        nameBlock.Children.Add(_nameBox);
        nameBlock.Children.Add(_nameError);

        var daysBlock = new StackPanel { Spacing = 6 };
        daysBlock.Children.Add(dayLabel);
        daysBlock.Children.Add(_grid);
        daysBlock.Children.Add(_daysError);

        Children.Add(nameBlock);
        Children.Add(daysBlock);
    }

    /// <summary>周期名（首尾空白已去）。</summary>
    public string CycleName
    {
        get => _nameBox.Text.Trim();
        set => _nameBox.Text = value;
    }

    /// <summary>包含哪些天。</summary>
    public WeekdaySet Days
    {
        get => _grid.Days;
        set => _grid.Days = value;
    }

    /// <summary>
    /// 校验并显示红字。
    /// </summary>
    /// <param name="takenNames">
    /// **自己以外**的现有自定义周期名（查重用）；为 <see langword="null"/> 时只做字段级校验。
    /// 内置五档的名字不必传 —— <see cref="CycleNames.IsTaken"/> 自己算上。
    /// </param>
    /// <returns>通过为 <see langword="true"/>；否则为 <see langword="false"/> 且错误已就地显示。</returns>
    /// <remarks>
    /// 🔴 查重判据与落盘前那条（<c>ConfigEditService.EnsureNameAvailable</c>）共用
    /// <see cref="CycleNames.IsTaken"/>：面板说可以、保存说不行（或反过来）是最消耗信任的一类不一致。
    /// </remarks>
    public bool Validate(IReadOnlyCollection<string>? takenNames = null)
    {
        // 两条各自归属自己的字段下方；都错就都显示 —— 一次点保存把问题说完，
        // 好过让用户改一个再点一次才发现还有第二个。
        var nameOk = CycleName.Length > 0;
        var nameTaken = nameOk && CycleNames.IsTaken(CycleName, takenNames);
        var daysOk = WeekdaySets.Sanitize(Days) != WeekdaySet.None;

        // 同一块红字位置轮换两条文案：空名优先（"没填"比"重名"更前置）。
        _nameError.Text = nameTaken ? NameTakenErrorText : NameErrorText;

        SetVisible(_nameError, !(nameOk && !nameTaken));
        SetVisible(_daysError, !daysOk);

        return nameOk && !nameTaken && daysOk;
    }

    /// <summary>把焦点放到名称框（面板刚打开时用）。</summary>
    public void FocusName() => _nameBox.Focus(FocusState.Programmatic);

    private static TextBlock CreateErrorBlock(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        // 红色取自框架的"严重"语义色；取不到就退回默认前景色 ——
        // 宁可少一层颜色，也不要为了一行红字把面板炸掉。
        if (Application.Current?.Resources.TryGetValue("SystemFillColorCriticalBrush", out var brush) == true
            && brush is Brush foreground)
        {
            block.Foreground = foreground;
        }

        return block;
    }

    private static void Hide(TextBlock block) => SetVisible(block, visible: false);

    private static void SetVisible(TextBlock block, bool visible)
        => block.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
}
