namespace DelayStart.Core.Models;

/// <summary>
/// 调度完成时的通知策略（FR-9.7 / FR-6.3；N2，2026-09-22 批复起语义 = 发不发系统通知）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 历史（UI v2，2026-09-21）：本枚举曾一度管"收尾弹不弹进度面板"。
/// N2–N5（<c>design.md</c> FR-14.1）把通知与面板拆开后，语义回归字面 ——
/// 调度全部结束后按策略决定是否发送 Windows 系统通知（经通知中转器发出），
/// 判定实现在 <see cref="Services.NotifyDecision"/>；面板仅手动弹出，与本枚举无关。
/// </para>
/// <para>
/// 显式赋值（而非按声明顺序）是为了让配置文件里的数字含义长期稳定 ——
/// 将来若在中间插入新取值，旧配置的语义不会被悄悄改写。枚举取值与存储不变（零迁移）。
/// </para>
/// </remarks>
public enum NotifyMode
{
    /// <summary>仅在有失败时通知。默认值。</summary>
    FailuresOnly = 0,

    /// <summary>无论成败都通知。</summary>
    Always = 1,

    /// <summary>从不通知。</summary>
    Never = 2,
}
