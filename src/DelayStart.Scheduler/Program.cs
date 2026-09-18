namespace DelayStart.Scheduler;

/// <summary>
/// 调度端入口点。
/// </summary>
/// <remarks>
/// <para>
/// <b>技术选型（D24 批复 B，2026-09-19）：纯 Win32，不引用任何 UI 框架。</b>
/// 托盘图标用 <c>Shell_NotifyIcon</c>，点击弹出的面板用自绘无边框窗口，全部走 P/Invoke。
/// 这样 NativeAOT 落在官方支持范围内 —— 不需要 <c>_SuppressWinFormsTrimError</c> 这类内部属性，
/// 也不会触发 <c>NETSDK1175</c>（WinForms + trimming 在 SDK 里是被主动拦截的）。
/// 背景与完整论证见 <c>docs/architecture.md</c> R11 与 <c>docs/design-spec.md</c> 6.6。
/// </para>
/// <para>
/// <b>当前状态：Phase 0 骨架，尚无实现。</b>
/// 托盘图标、悬停摘要、点击面板、完成通知、以及 <c>state\current-run.json</c> 的写入
/// 都在 Phase 4 实现，交互细节与硬约束见 <c>docs/scheduler-design.md</c>。
/// </para>
/// <para>
/// 实现时必须遵守的几条（现在写在这里，是为了避免 Phase 4 才想起来）：
/// </para>
/// <list type="bullet">
///   <item><description><c>OutputType</c> 必须是 <c>WinExe</c> —— 用 <c>Exe</c> 会在登录瞬间闪一个控制台黑框。</description></item>
///   <item><description>禁止 <c>.resx</c> 反射式资源加载，图标用 <c>Assembly.GetManifestResourceStream</c>（AOT 下 <c>.resx</c> 必失败）。</description></item>
///   <item><description>禁止 <c>System.Reflection</c>、<c>Reflection.Emit</c>、动态 COM 互操作 —— 这些是 NativeAOT 的硬边界。</description></item>
///   <item><description>通知只能用 <c>Shell_NotifyIcon</c> 的气泡（<c>NIF_INFO</c>）；elevated 进程发不了真 Toast，WinRT 通知 API 在 AOT 下也不可用。</description></item>
///   <item><description>运行身份是交互用户（<c>LogonType=Interactive</c> + <c>RunLevel=Highest</c>），<b>禁止 SYSTEM</b> —— 否则 <c>%APPDATA%</c> 指向 systemprofile，配置静默读不到（NFR-6.8）。</description></item>
/// </list>
/// </remarks>
internal static class Program
{
    /// <summary>
    /// 调度端主入口。
    /// </summary>
    /// <returns>进程退出码。<c>0</c> 表示本次调度流程正常结束。</returns>
    [STAThread]
    private static int Main()
    {
        // Phase 4：在这里建立消息循环、注册托盘图标、读取 config.json 并按延时启动条目。
        // 时序规则见 docs/architecture.md 第 4 节（相对登录时刻的绝对时间点，不是累加）。
        return 0;
    }
}
