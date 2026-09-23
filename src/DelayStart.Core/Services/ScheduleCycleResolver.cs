using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// 一个周期 id 解析后的判定输入。
/// </summary>
/// <param name="Kind">判定种类。</param>
/// <param name="Days">自定义周期的星期集合；非 <see cref="ScheduleRuleKind.Custom"/> 时为 <see cref="WeekdaySet.None"/>。</param>
/// <param name="Found">
/// 是否在两处都找得到：内置常量或配置的自定义周期表。
/// <c>false</c> 表示引用失效（手改 / 备份还原不一致 / 周期 <c>Days</c> 为空），
/// 此时 <see cref="Kind"/> 恒为 <see cref="ScheduleRuleKind.Everyday"/>。
/// </param>
public sealed record ResolvedCycle(ScheduleRuleKind Kind, WeekdaySet Days, bool Found);

/// <summary>
/// 把条目上的"周期 id"翻译成判定所需的 <c>(kind, days)</c>（§4.2）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **兜底方向只能是"每天"，绝不能是"不匹配"**（FR-15.15）：引用失效是配置损坏的结果，
/// 让条目从此<b>静默地永远不启动</b>是最坏的一类失败 —— 用户只会看到"它不跑了"，
/// 而原因藏在一次已经过去的误操作里。这条规则反过来也好记：
/// **解析不出来就往"更宽松"兜（每天），绝不往"更严格"兜（不启动）。**
/// </para>
/// <para>
/// 同样地，手改配置造出 <c>Days = 0</c> 的自定义周期时按**无效**处理（= 不存在），
/// 走同一条兜底路径。这样"空的星期集合"就不需要第三种语义 —— 它不是"永不启动"，
/// 它就相当于没配。
/// </para>
/// </remarks>
public static class ScheduleCycleResolver
{
    /// <summary>空 id（或全空白）视为未配置，按内置的「每天」处理。</summary>
    private static readonly ResolvedCycle Everyday = new(ScheduleRuleKind.Everyday, WeekdaySet.None, Found: true);

    /// <summary>是否内置周期 id。</summary>
    /// <param name="cycleId">待判断的 id。</param>
    /// <returns>内置为 <see langword="true"/>。</returns>
    public static bool IsBuiltin(string? cycleId) => cycleId switch
    {
        BuiltinCycleIds.Everyday or BuiltinCycleIds.Weekdays or BuiltinCycleIds.Weekends
            or BuiltinCycleIds.LegalWorkday or BuiltinCycleIds.LegalHoliday => true,
        _ => false,
    };

    /// <summary>
    /// 解析一个周期 id。
    /// </summary>
    /// <param name="cycleId">条目上的周期 id。</param>
    /// <param name="cycles">配置里的自定义周期表；为 <see langword="null"/> 时视作空表。</param>
    /// <returns>解析结果；查不到时得到"每天 + <c>Found=false</c>"。</returns>
    public static ResolvedCycle Resolve(string? cycleId, IReadOnlyList<ScheduleCycle>? cycles)
    {
        if (string.IsNullOrWhiteSpace(cycleId))
        {
            return Everyday;
        }

        var kind = cycleId switch
        {
            BuiltinCycleIds.Weekdays => ScheduleRuleKind.Weekdays,
            BuiltinCycleIds.Weekends => ScheduleRuleKind.Weekends,
            BuiltinCycleIds.LegalWorkday => ScheduleRuleKind.LegalWorkday,
            BuiltinCycleIds.LegalHoliday => ScheduleRuleKind.LegalHoliday,
            BuiltinCycleIds.Everyday => ScheduleRuleKind.Everyday,
            _ => (ScheduleRuleKind?)null,
        };

        if (kind is not null)
        {
            return new ResolvedCycle(kind.Value, WeekdaySet.None, Found: true);
        }

        if (cycles is not null)
        {
            foreach (var cycle in cycles)
            {
                if (cycle is null || !string.Equals(cycle.Id, cycleId, StringComparison.Ordinal))
                {
                    continue;
                }

                var days = WeekdaySets.Sanitize(cycle.Days);
                if (days is WeekdaySet.None)
                {
                    // 空周期 = 无效定义。注意这里**不**返回"永不匹配"的那类结果。
                    return new ResolvedCycle(ScheduleRuleKind.Everyday, WeekdaySet.None, Found: false);
                }

                return new ResolvedCycle(ScheduleRuleKind.Custom, days, Found: true);
            }
        }

        return new ResolvedCycle(ScheduleRuleKind.Everyday, WeekdaySet.None, Found: false);
    }

    /// <summary>
    /// 这个自定义周期是否正被任何条目引用（FR-15.14 / FR-15.16 的数据来源）。
    /// </summary>
    /// <param name="cycleId">自定义周期 id。</param>
    /// <param name="items">配置里的全部条目。</param>
    /// <returns>引用它的条目数量。</returns>
    public static int CountReferences(string? cycleId, IReadOnlyList<DelayedItem>? items)
    {
        if (string.IsNullOrWhiteSpace(cycleId) || items is null)
        {
            return 0;
        }

        var count = 0;
        foreach (var item in items)
        {
            if (item is not null && string.Equals(item.ScheduleCycleId, cycleId, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }
}
