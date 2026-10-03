using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// 编辑器里「启动身份」默认选什么的纯判定（D147）。
/// </summary>
/// <remarks>
/// <para>
/// 职责只有一个：<b>给定一条 <see cref="StartupEntry"/>，新建 / 接管形态下默认选管理员还是普通身份</b>。
/// 它不读配置文件、不碰文件系统、不看当前进程。
/// </para>
/// <para>
/// 🔴 <b>为什么住 Core 而不是 App</b>：Tests 绝不引用 App（硬约束），所以 App 里的任何判定
/// 都是<b>零覆盖</b>的 —— 改坏它构建照样绿、测试照样全绿（D146 的同一条论点）。
/// 而"UWP 恒为普通身份"这条不变量此前只写在 <c>DelayEditorDialog.RunAsAdmin</c> 的表达式里，
/// 一旦被"顺手简化"成 <c>RequiresAdminRun</c>，后果是 UWP 条目走进管理员分支、
/// 启动链里又有一层 D45 兜底 —— 两处各判一次，迟早漂移。搬到这里才有测试钉住。
/// 判据本身也确实是纯逻辑：三个入参全是数据。
/// </para>
/// <para>
/// 🔴 <b>调用方只准传结论，不准在 App 层再写一遍等价表达式</b>（例如
/// <c>entry.RequiresAdminRun &amp;&amp; !IsUwp(entry)</c>）。那样等于把刚搬走的那份实现又抄回去，
/// 下一个人还是会看见"胶囊里有个 &amp;&amp;"然后照抄。
/// </para>
/// </remarks>
public static class LaunchIdentityPolicy
{
    /// <summary>
    /// 新建 / 接管形态下的默认启动身份。
    /// </summary>
    /// <param name="entry">要判定的系统自启动项。</param>
    /// <returns>应默认选中「管理员」时为 <see langword="true"/>，否则为 <see langword="false"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> 为 <see langword="null"/>。</exception>
    /// <remarks>
    /// <para>
    /// 判据只有两条，缺一不可：
    /// </para>
    /// <list type="number">
    /// <item><description><see cref="StartupEntry.RequiresAdminRun"/> 为真 ——
    /// 这一项按系统里的原有定义就需要管理员（目前只有计划任务的 <c>RunLevel=Highest</c>）。
    /// 这是本次功能本体：用户提这个需求的原始理由是「<b>如果用户没注意</b>，
    /// 就会用普通身份启动本该管理员身份的任务」—— 默认错一次，条目就静默地少了权限。</description></item>
    /// <item><description>🔴 <b>UWP 恒为普通身份</b>（D45 真机实测：UWP 进程恒为普通用户身份，
    /// 中转外壳用谁的令牌都不改结果）。这条<b>压过</b>上一条：UWP 项的
    /// <c>RequiresAdminRun</c> 恒为假，但把不变量写成显式的排除分支而不是"恰好为真"，
    /// 是为了让"来源判错了"也不会走到管理员 —— 它是一条<b>关于物理事实</b>的断言，
    /// 不是一条可被数据满足的前提。</description></item>
    /// </list>
    /// <para>
    /// 🔴 <b>刻意不读 <see cref="StartupEntry.IsMissing"/> 与 <see cref="StartupEntry.IsProtected"/></b>：
    /// 那两个字段说的是「这一项现在还能不能被接管」，与「它启动时要什么身份」正交。
    /// 而且失效项 / 受保护项<b>根本走不到这个编辑器</b>（<see cref="StartupEntry.CanTakeOver"/>
    /// 已经把它们挡掉），把操作性字段接进身份默认值只会制造一条永远走不到的分支。
    /// 唯一能走到这里的路径是命令行 <c>--takeover</c>，那里同样由调用方按
    /// <see cref="StartupEntry.CanTakeOver"/> 把关。
    /// </para>
    /// <para>
    /// <b>方向 fail-open</b>：查不到 → <see langword="false"/>（按普通身份启动），与
    /// <see cref="StartupEntry.RequiresAdminRun"/> 的备注同源。判错的代价是"用户看到程序
    /// 少了点权限、可以自己改回管理员"，而反过来是"凭空弹 UAC"。
    /// </para>
    /// </remarks>
    public static bool DefaultRunAsAdmin(StartupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.RequiresAdminRun && !IsUwpTarget(entry);
    }

    /// <summary>
    /// 这一项是不是 UWP 目标：来源是 UWP，或路径已经是 <c>shell:AppsFolder\…</c> 解析名。
    /// </summary>
    /// <remarks>
    /// 与 <c>DelayEditorDialog.IsUwpSource</c> 同一条判据（D41 的正例 + 兜底），
    /// 也与 <c>DeElevatedProcessLauncher.IsUwpItem</c> 同源。之所以在这里重写而不是复用 App 的
    /// 那个私有方法：方向反了 —— App 能引 Core，Core 引不到 App。
    /// 口径相同这一点靠本方法的注释与那两处互相点名维持。
    /// </remarks>
    private static bool IsUwpTarget(StartupEntry entry)
        => entry.Source == StartupSource.Uwp || UwpParsingName.IsParsingName(entry.Path);
}
