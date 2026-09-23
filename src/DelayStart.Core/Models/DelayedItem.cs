namespace DelayStart.Core.Models;

/// <summary>
/// 一个"延时启动"条目，持久化到 <c>config.json</c>（§4.3）。
/// </summary>
/// <remarks>
/// <para>
/// 刻意用可变属性（<c>{ get; set; }</c>）而非 <c>init</c>：配置在管理端会被**原地修改**
/// （改延时、调顺序、切开关），<c>init</c> 会强迫每次编辑都重建整棵配置对象树。
/// 这与 <c>design.md</c> 9.2对 <c>Core.Models</c> 的建议不同，
/// 属于有明确理由的偏离。
/// </para>
/// <para>
/// 另一个刻意的选择：**不加 <c>required</c>**。源生成 JSON 反序列化需要一个
/// 全默认值的实例，<c>required</c> 会让反序列化抛异常。
/// </para>
/// </remarks>
public sealed class DelayedItem
{
    /// <summary><see cref="DelaySeconds"/> 的默认值（FR-4.1 预设值之一）。</summary>
    public const int DefaultDelaySeconds = 30;

    /// <summary>稳定主键，由 <c>ItemKeyBuilder</c> 生成（机制 1）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>目标路径。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>自定义命令行参数，留空则不附加（FR-4.5）。</summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>
    /// 启动时的当前工作目录。留空则使用程序所在目录（设计稿三之二「工作目录 — 可选」）。
    /// </summary>
    /// <remarks>
    /// 只有**手动添加**的条目用得上它：系统自启动项的工作目录由系统决定
    /// （注册表项没有工作目录这一说，快捷方式自带一份），接管时不该改。
    /// 字段放在 <see cref="DelayedItem"/> 而不是单独一张"手动条目表"，是因为除了来源以外
    /// 两类条目的行为完全一致，分表会让调度端出现两套启动逻辑。
    /// </remarks>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 相对**登录时刻**的绝对秒数，不是"上一个启动后再等 N 秒"（机制 5 / FR-5.3）。
    /// </summary>
    public int DelaySeconds { get; set; } = DefaultDelaySeconds;

    /// <summary>相同延时值内的发起顺序（FR-4.7）。仅延时相同时参与比较。</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// 启动身份。false = 普通用户，走降权启动（NFR-3.3）；true = 继承调度端管理员令牌。
    /// </summary>
    public bool RunAsAdmin { get; set; }

    /// <summary>条目级开关。关闭后本次登录不启动，配置与原始自启动项状态均不变（FR-4.6）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 所属**调度周期**的 id（FR-15.1）。缺省即内置「每天」，与升级前行为完全一致。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是一条**引用**，不是拷贝：周期定义（名称与星期集合）在配置的
    /// <c>cycles</c> 顶层集合里，改一处影响所有引用它的条目（D87 = 🅑）。
    /// </para>
    /// <para>
    /// 🔴 **不存在"没选周期"的状态**（用户 2026-09-23 批复 2）：一个条目永远属于某个周期，
    /// 默认是「每天」。不允许用"空引用"或"星期集合为空"来表达"永不启动" ——
    /// 想让条目不跑，请关掉 <see cref="Enabled"/>；那是唯一一处表达这个意思的地方。
    /// </para>
    /// </remarks>
    public string ScheduleCycleId { get; set; } = BuiltinCycleIds.Everyday;

    /// <summary>来源类型。手动条目为 <see cref="StartupSource.Manual"/>。</summary>
    public StartupSource Source { get; set; } = StartupSource.Manual;
    /// <summary>作用域。手动条目为 <see cref="StartupScope.None"/>。</summary>
    public StartupScope Scope { get; set; } = StartupScope.None;

    /// <summary>来源内的原始键，用于移除接管时恢复系统项。手动条目为空串。</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>面向用户的位置描述（展示用，不参与逻辑判断）。</summary>
    public string SourceDetail { get; set; } = string.Empty;

    /// <summary>接管前的原始状态，移除接管时据此精确还原（FR-2.7）。</summary>
    public OriginalState OriginalState { get; set; } = new();

    /// <summary>是否为手动添加的条目。手动条目在系统中没有任何对应物，移除时**不做任何恢复动作**（FR-3.4）。</summary>
    public bool IsManual => Source == StartupSource.Manual;
}
