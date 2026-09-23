using DelayStart.Core.Models;

namespace DelayStart.App.ViewModels;

/// <summary>
/// 把 <c>Core</c> 的枚举翻译成界面文案。
/// </summary>
/// <remarks>
/// <para>
/// 单独一个类而不是写成 <c>StartupEntry</c> 上的计算属性：界面文案的唯一来源是
/// <c>docs/design.md</c>，而 <c>Core</c> 的模型不承载任何中文展示串 ——
/// 它是调度端与管理端共用的数据契约，把文案塞进去会让"改一句话"变成"改数据契约"。
/// </para>
/// <para>
/// 同时这也让文案可被单测断言（本类无依赖、无副作用）。注意管理端的 ViewModel
/// 本身进不了测试项目（<c>design.md</c> 7.1 禁止测试项目引用 <c>App</c>），
/// 所以能抽出来的纯函数就在这里。
/// </para>
/// </remarks>
public static class DisplayText
{
    /// <summary>来源类型 → 列表里的来源名。</summary>
    /// <param name="source">来源类型。</param>
    /// <returns>界面文案。</returns>
    public static string SourceOf(StartupSource source) => source switch
    {
        StartupSource.Registry => "注册表",
        StartupSource.StartupFolder => "启动文件夹",
        StartupSource.ScheduledTask => "计划任务",
        StartupSource.Uwp => "UWP 应用",
        StartupSource.Manual => "手动添加",
        _ => source.ToString(),
    };

    /// <summary>作用域 → 界面文案。</summary>
    /// <param name="scope">作用域。</param>
    /// <returns>界面文案。</returns>
    /// <remarks>
    /// 注册表的三个 hive 分开写而不是统一"注册表"：用户需要一眼看出这条改的是
    /// 当前用户还是所有用户，这决定了"要不要提权"以及"影响谁"。
    /// </remarks>
    public static string ScopeOf(StartupScope scope) => scope switch
    {
        StartupScope.Hkcu => "当前用户（HKCU）",
        StartupScope.Hklm => "所有用户（HKLM）",
        StartupScope.HklmWow => "所有用户（32 位）",
        StartupScope.UserFolder => "用户启动文件夹",
        StartupScope.SystemFolder => "系统启动文件夹",
        StartupScope.None => "—",
        _ => scope.ToString(),
    };

    /// <summary>
    /// 条目的状态徽标文案。
    /// </summary>
    /// <param name="entry">扫描得到的条目。</param>
    /// <returns>设计稿规定的五种文案之一。</returns>
    /// <remarks>
    /// 🔴 **判断顺序不能改**：一个被接管的条目，其系统项此刻必然处于禁用状态
    /// （是我们刚写的标记），所以若先判 <c>IsEnabled</c> 它会显示成"已禁用"，
    /// 用户就看不出"这是本程序在管它"。同理，"受保护"与"已失效"都优先于
    /// "已接管" —— 前两者决定了这一条**能不能操作**，比"归谁管"更该先看到。
    /// </remarks>
    public static string StatusOf(StartupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.IsProtected)
        {
            return "受保护";
        }

        if (entry.IsMissing)
        {
            return "已失效";
        }

        if (entry.IsTakenOver)
        {
            return "已接管";
        }

        return entry.IsEnabled ? "已启用" : "已禁用";
    }

    /// <summary>延时秒数 → 界面文案（短格式：不带「登录后」前缀）。</summary>
    /// <param name="seconds">相对登录时刻的绝对秒数。</param>
    /// <returns>形如 <c>立即</c> / <c>30 秒</c> / <c>2 分 30 秒</c> / <c>2 分</c>。</returns>
    public static string DelayOf(int seconds)
    {
        if (seconds <= 0)
        {
            return "立即";
        }

        return seconds < 60
            ? $"{seconds} 秒"
            : seconds % 60 == 0
                ? $"{seconds / 60} 分"
                : $"{seconds / 60} 分 {seconds % 60} 秒";
    }

    /// <summary>延时秒数 → 「登录后 …」文案（设置页的预设延时列表用，2026-09-23 批复 24）。</summary>
    /// <param name="seconds">相对登录时刻的绝对秒数。</param>
    /// <returns>
    /// 形如「登录后立即」「登录后 30 秒」「登录后 2 分（120 秒）」「登录后 2 分 30 秒（150 秒）」。
    /// </returns>
    /// <remarks>
    /// <para>
    /// 🔴 与 <see cref="DelayOf"/> 分开、而不是改它：那一份是**列宽敏感**的短文案
    /// （列表列、徽标、分组标题都按它排版）。这里多出来的「（150 秒）」只服务设置页那一处
    /// —— 用户填进去的是秒数，回到列表里却只看见「2 分 30 秒」，中间那一步换算不该由用户做。
    /// </para>
    /// <para>
    /// 分钟整点时也补括号（「2 分（120 秒）」）：同样是"分 → 秒"的换算，
    /// 只在混合形态下给会让用户以为整点的那种没被记录。
    /// </para>
    /// <para>
    /// 0 秒写「登录后立即」（2026-09-24 用户批复，历经「登录后 0 秒」→「立即」→「登录后立即」）：
    /// 同列每一行都在回答"登录后多久"，留着前缀、把 0 换成「立即」，既说清"不等待"，
    /// 又不让这一行在纵向上落单。列表页的延时列（<see cref="DelayOf"/>）仍是裸的「立即」
    /// —— 那一列按列宽排版，且整列本来就没有「登录后」前缀。
    /// </para>
    /// </remarks>
    public static string LogonDelayOf(int seconds)
    {
        if (seconds <= 0)
        {
            return "登录后立即";
        }

        if (seconds < 60)
        {
            return $"登录后 {seconds} 秒";
        }

        var minutes = seconds / 60;
        var rest = seconds % 60;

        return rest == 0
            ? $"登录后 {minutes} 分（{seconds} 秒）"
            : $"登录后 {minutes} 分 {rest} 秒（{seconds} 秒）";
    }

    /// <summary>启动身份 → 界面文案。</summary>
    /// <param name="runAsAdmin">是否继承管理员令牌。</param>
    /// <returns>界面文案。</returns>
    public static string IdentityOf(bool runAsAdmin) => runAsAdmin ? "管理员" : "普通用户";
}
