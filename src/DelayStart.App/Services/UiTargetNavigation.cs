using DelayStart.Core.Launch;

namespace DelayStart.App.Services;

/// <summary>
/// 跨进程定位令牌 → 导航标签的映射（D74 / D81）。
/// </summary>
/// <remarks>
/// <para>
/// 系统通知的点击（<c>delaystart://…</c> → <c>--goto-startup</c>），以及命令行
/// <c>--goto-startup</c> / <c>--stale</c>，都走这一处翻译。
/// 单独成文件是因为它是"跨进程契约 → 界面标签"的唯一翻译点 ——
/// 散在 <see cref="MainWindow"/> 里的话，"守卫写了一个新令牌但界面没接"
/// 这种断链只能靠读代码发现。
/// </para>
/// <para>
/// 🔴 令牌来自**跨进程文件 / 命令行**，是不可信输入：不认识的值一律当作"不定位"
/// （返回 <see langword="null"/>），既不抛异常也不猜一个页面 ——
/// 猜错会把用户带到不相干的地方，比什么都不做更糟。
/// </para>
/// <para>
/// 🔴 <see cref="UiNavigationTarget.Stale"/> 与 <see cref="UiNavigationTarget.Delay"/>
/// 都映到「延时启动」页：失效条目自 D81 起就不再是独立页面，而是那一页里被标成
/// "已失效"的行。所以它们本来就是一回事，分成两个令牌只是为了不破坏跨进程契约
/// （已发出的通知里写着 <c>stale</c>）。
/// </para>
/// <para>
/// <see cref="UiNavigationTarget.RunsLog"/> 是 <c>--goto-log</c> 的**文件形态**（D82）：
/// 未提权的点击方只能写请求文件，而请求文件必须带一个令牌，于是"看日志"这件事
/// 也取得了一个跨进程写法。它与命令行参数 <c>--goto-log</c> 是同一意图的两种形态，
/// 映到同一个页面。
/// </para>
/// </remarks>
public static class UiTargetNavigation
{
    /// <summary>把定位令牌翻译成导航标签。</summary>
    /// <param name="target">令牌，取值见 <see cref="UiNavigationTarget"/>。</param>
    /// <returns>导航标签；未知令牌或 <see langword="null"/> 时为 <see langword="null"/>。</returns>
    public static string? TagFor(string? target) => target switch
    {
        UiNavigationTarget.Registry => NavigationService.ItemsRegistryTag,
        UiNavigationTarget.StartupFolder => NavigationService.ItemsFolderTag,
        UiNavigationTarget.ScheduledTask => NavigationService.ItemsTaskTag,
        UiNavigationTarget.Uwp => NavigationService.ItemsUwpTag,
        UiNavigationTarget.Delay => NavigationService.DelayTag,
        UiNavigationTarget.Stale => NavigationService.DelayTag,
        UiNavigationTarget.RunsLog => NavigationService.RunsTag,
        _ => null,
    };
}
