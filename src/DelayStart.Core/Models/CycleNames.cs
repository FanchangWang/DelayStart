namespace DelayStart.Core.Models;

/// <summary>
/// 内置周期档的中文名，以及"周期名是否已被占用"的判据 —— 全仓库唯一一处。
/// </summary>
/// <remarks>
/// <para>
/// 名称原先只存在于界面侧（App 的 <c>CycleInfoProvider.NameOf</c>）。下移到 Core 是因为
/// **管理端也要用它判重**：自定义周期不许与内置档或别的自定义周期同名，而这条校验
/// 必须发生在落盘点（<c>ConfigEditService</c>），那里够不到界面层。
/// </para>
/// <para>
/// 🔴 **判据只有这一份**。界面上的即时红字（<c>CycleEditorForm.Validate</c>）与落盘前的
/// 兜底校验都调 <see cref="IsTaken"/> —— 判据分叉会造出"面板说可以、保存说不行"这种
/// 最消耗信任的错误（2026-09-23 批复 14）。
/// </para>
/// <para>
/// 内置档的<b>顺序</b>由 <see cref="BuiltinCycleIds.Ordered"/> 给出、<b>名称</b>由这里翻译，
/// 两处都用 id 串起来，不靠数组下标对齐 —— 名字是给人看的文案，会随文案调整，
/// 不该让顺序跟着一起动。
/// </para>
/// </remarks>
public static class CycleNames
{
    /// <summary>「每天」—— 默认档，也是引用失效 / 判不出来时的兜底名（FR-15.15）。</summary>
    public const string Everyday = "每天";

    /// <summary>「周一至周五」（纯星期规律）。</summary>
    public const string Weekdays = "周一至周五";

    /// <summary>「周六日」（纯星期规律）。</summary>
    public const string Weekends = "周六日";

    /// <summary>「法定工作日」（含调休补班日）。</summary>
    public const string LegalWorkday = "法定工作日";

    /// <summary>「法定节假日」。</summary>
    public const string LegalHoliday = "法定节假日";

    /// <summary>内置五档的名称，顺序与 <see cref="BuiltinCycleIds.Ordered"/> 一致。</summary>
    public static readonly string[] Builtin =
        [Everyday, Weekdays, Weekends, LegalWorkday, LegalHoliday];

    /// <summary>内置周期 id 对应的中文名；不是内置 id 时返回 <see langword="null"/>。</summary>
    /// <param name="cycleId">周期 id。</param>
    /// <returns>内置档名称，或 <see langword="null"/>（说明它可能是自定义 id、或压根不存在）。</returns>
    public static string? Of(string? cycleId) => cycleId switch
    {
        BuiltinCycleIds.Everyday => Everyday,
        BuiltinCycleIds.Weekdays => Weekdays,
        BuiltinCycleIds.Weekends => Weekends,
        BuiltinCycleIds.LegalWorkday => LegalWorkday,
        BuiltinCycleIds.LegalHoliday => LegalHoliday,
        _ => null,
    };

    /// <summary>两个周期名是否算同一个：去首尾空白 + 忽略大小写。</summary>
    /// <param name="left">一个名称。</param>
    /// <param name="right">另一个名称。</param>
    /// <returns>同名（含仅大小写 / 首尾空白差异）为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 忽略大小写是为了让「Work」与「work」不能并存 —— 中文名用不上这一条，
    /// 但用户完全可能起英文名，而两个只差大小写的徽标在列表里分不出来。
    /// </remarks>
    public static bool Same(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 这个名称是否已被占用：命中的是内置五档，或 <paramref name="otherNames"/> 中的任一个。
    /// </summary>
    /// <param name="name">待检查的名称。</param>
    /// <param name="otherNames">**自己以外**的现有自定义周期名；没有别的周期时可为 <see langword="null"/>。</param>
    /// <returns>被占用为 <see langword="true"/>；名字为空时返回 <see langword="false"/>（"空名"由调用方另行报错）。</returns>
    public static bool IsTaken(string? name, IEnumerable<string?>? otherNames = null)
    {
        // 空名不在这里判：它会命中每一条比较（Trim 后相等）吗？不会 —— Same 对空白直接 false。
        // 但"必须填名字"是另一条规则（FR-15.20），报错文案也不同，留给调用方。
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (var reserved in Builtin)
        {
            if (Same(reserved, name))
            {
                return true;
            }
        }

        if (otherNames is null)
        {
            return false;
        }

        foreach (var other in otherNames)
        {
            if (Same(other, name))
            {
                return true;
            }
        }

        return false;
    }
}
