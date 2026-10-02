namespace DelayStart.Core.Services;

/// <summary>
/// 全项目**唯一**的路径解析入口（D23 / <c>docs/design.md</c> 六）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **禁止硬编码路径**。任何类库里出现 <c>@"C:\..."</c> 或手工拼
/// <c>Environment.GetFolderPath</c> 都算违规 —— 集中在一处才能保证
/// "安装目录只读"（NFR-6.7）与"运行时数据与安装目录分离"（D23）这两条约束
/// 不会被某处随手写的路径悄悄破坏。
/// </para>
/// <para>
/// 两个覆盖环境变量 <c>DELAYSTART_LOCAL_DIR</c> / <c>DELAYSTART_CONFIG_DIR</c>
/// **只用于开发与测试**，正式代码不得依赖它们的存在。
/// </para>
/// </remarks>
public sealed class PathService
{
    /// <summary>覆盖 Local 根目录的环境变量名，仅用于开发与测试。</summary>
    public const string LocalRootOverrideVariable = "DELAYSTART_LOCAL_DIR";

    /// <summary>覆盖配置根目录的环境变量名，仅用于开发与测试。</summary>
    public const string ConfigRootOverrideVariable = "DELAYSTART_CONFIG_DIR";

    private const string FolderName = "DelayStart";
    private const string ManagerExecutableName = "DelayStart.exe";
    private const string SchedulerExecutableName = "DelayStart.Scheduler.exe";
    private const string GuardExecutableName = "DelayStart.Guard.exe";
    private const string NotifyBrokerExecutableName = "DelayStart.NotifyBroker.exe";

    /// <summary>用系统默认位置构造（正式运行路径）。</summary>
    public PathService()
        : this(null, null, null)
    {
    }

    /// <summary>
    /// 用显式覆盖值构造，便于测试隔离。
    /// </summary>
    /// <param name="localRoot">Local 根目录；为 <see langword="null"/> 时依次回退到环境变量、系统目录。</param>
    /// <param name="configRoot">配置根目录；为 <see langword="null"/> 时依次回退到环境变量、系统目录。</param>
    /// <param name="installedRoot">安装目录；为 <see langword="null"/> 时取当前程序基目录。</param>
    public PathService(string? localRoot, string? configRoot, string? installedRoot = null)
    {
        InstalledRoot = NormalizeDirectory(installedRoot ?? AppContext.BaseDirectory);
        LocalRoot = NormalizeDirectory(
            ResolveRoot(localRoot, LocalRootOverrideVariable, Environment.SpecialFolder.LocalApplicationData));
        // 🔴 配置**不**再放 Roaming（%APPDATA%）。它是漫游目录，域环境下会跟着用户同步到
        // 另一台机器，而配置里装的是纯机器相关的东西：可执行文件绝对路径、注册表键名、
        // 计划任务路径、接管前的原始状态。漫游过去的后果是守卫看到一堆"源已消失"的孤儿、
        // 调度器去启动不存在的路径，而且**这些数据在源机器上已被覆盖、无法还原**。
        // 其余数据（调度、守卫、法定日历、日志）本来就都在 Local，配置是唯一的例外。
        //
        // 🔴 **放在 LocalRoot 下的 config\ 子目录**，而不是与 LocalRoot 平级：
        // 同一个目录里既堆着 logs\ scheduler\ guard\ holidays\ 这些**可随时删**的运行数据，
        // 又放着唯一的**还原依据**。用户清理"日志和缓存"时顺手动到 config.json 的概率不低，
        // 而配置丢了 = 接管关系全部失忆 = 无法还原被接管的系统启动项。
        // 分成子目录之后，"删运行数据"与"留配置"在目录层面就是两件不同的事。
        var resolvedLocalRoot = ResolveRoot(localRoot, LocalRootOverrideVariable, Environment.SpecialFolder.LocalApplicationData);
        LocalRoot = NormalizeDirectory(resolvedLocalRoot);

        // 环境变量仍然优先：它本来就是给测试与"把配置放到别处"用的显式入口，
        // 显式指定时不该再被强行塞进 LocalRoot\config。
        ConfigRoot = NormalizeDirectory(
            configRoot is not null || Environment.GetEnvironmentVariable(ConfigRootOverrideVariable) is { } overridden
                ? ResolveRoot(configRoot, ConfigRootOverrideVariable, Environment.SpecialFolder.LocalApplicationData)
                : Path.Combine(resolvedLocalRoot, ConfigDirectoryName));
    }

    /// <summary>配置目录名（<see cref="LocalRoot"/> 下的子目录）。</summary>
    private const string ConfigDirectoryName = "config";

    /// <summary>配置文件名（不含目录）。</summary>
    private const string ConfigFileName = "app.json";

    /// <summary>
    /// 安装目录（per-user 安装时为 <c>%LOCALAPPDATA%\Programs\DelayStart</c>）。
    /// 🔴 **只读**：仅用于取自身 exe 路径，**不得作为任何写入目标**（NFR-6.7 / FR-11.1）。
    /// </summary>
    public string InstalledRoot { get; }

    /// <summary>Local 根目录：<c>%LOCALAPPDATA%\DelayStart</c>。日志、状态、归档都放这里，不随漫游搬家。</summary>
    public string LocalRoot { get; }

    /// <summary>
    /// 配置根目录：<c>%LOCALAPPDATA%\DelayStart\config</c>。
    /// </summary>
    /// <remarks>
    /// 🔴 刻意与 <see cref="LocalRoot"/> 同在 Local、而不是漫游目录：配置内容全是机器相关的
    /// （绝对路径、注册表键名、计划任务路径、接管前状态），漫游到另一台机器上全部失效且无法还原。
    /// 用户偏好（主题、上次选的延时）才适合漫游，而本项目没有那类数据。
    /// <para>
    /// 🔴 而它**必须**是 <see cref="LocalRoot"/> 的**子目录**而不是与它平级：同一个目录里
    /// 既堆着 logs\ / scheduler\ / guard\ / holidays\ 这些**可随时删**的运行数据，又放着
    /// 唯一的**还原依据**。用户清理"日志和缓存"时顺手动到配置文件的概率不低，而配置丢了
    /// 等于接管关系全部失忆、无法还原被接管的系统启动项（硬约束 7 可逆优先）。
    /// </para>
    /// <para>
    /// 显式传入或设了 <see cref="ConfigRootOverrideVariable"/> 时**不**再套子目录 ——
    /// 那两个入口本来就是给测试与"把配置放到别处"用的，显式指定就该照办。
    /// </para>
    /// </remarks>
    public string ConfigRoot { get; }

    /// <summary>日志目录。</summary>
    public string LogsRoot => Path.Combine(LocalRoot, "logs");

    /// <summary>
    /// 调度端的运行时数据目录（D116）：<c>{LocalRoot}\scheduler</c>。
    /// </summary>
    /// <remarks>
    /// 🔴 D116 起运行时数据**按进程归堆**：调度端的实时状态与归档都收进这里，
    /// 守卫的数据留在 <see cref="GuardRoot"/> —— 目录名即进程名，与 logs\ 下的文件名对得上。
    /// </remarks>
    public string SchedulerRoot => Path.Combine(LocalRoot, "scheduler");

    /// <summary>运行归档目录（D116）：<c>{LocalRoot}\scheduler\archive</c>，每份 <c>{runId}.json</c>。</summary>
    public string SchedulerArchiveRoot => Path.Combine(SchedulerRoot, "archive");

    /// <summary>
    /// 守卫巡检归档目录（D116）：<c>{LocalRoot}\guard\inspections</c>，每次巡检一份
    /// <c>{yyyyMMdd-HHmmss}.json</c>。
    /// </summary>
    /// <remarks>目录由写入方按需创建（<c>AtomicFileWriter.WriteAllText</c> 自带建目录），
    /// 与 <see cref="GuardRoot"/> 同口径，不进 <see cref="EnsureCreated"/>。</remarks>
    public string GuardInspectionsRoot => Path.Combine(GuardRoot, "inspections");

    /// <summary>配置文件完整路径（<c>{ConfigRoot}\app.json</c>）。</summary>
    /// <remarks>
    /// 🔴 文件名不叫 <c>config.json</c>：它所在的目录已经叫 <c>config</c> 了，
    /// <c>config\config.json</c> 是那种读起来会卡一下的重复。
    /// </remarks>
    public string ConfigFilePath => Path.Combine(ConfigRoot, ConfigFileName);

    /// <summary>调度端日志完整路径。</summary>
    public string SchedulerLogPath => Path.Combine(LogsRoot, "scheduler.log");

    /// <summary>管理端日志完整路径。</summary>
    public string ManagerLogPath => Path.Combine(LogsRoot, "manager.log");

    /// <summary>UIAccess 中转器日志完整路径（D70：与调度端同目录，便于联合排查）。</summary>
    public string BrokerLogPath => Path.Combine(LogsRoot, "launchbroker.log");

    /// <summary>通知中转器日志完整路径（N1，2026-09-22 批复：同样与调度端同目录）。</summary>
    public string NotifyBrokerLogPath => Path.Combine(LogsRoot, "notifybroker.log");

    /// <summary>守卫日志完整路径（D74）。</summary>
    public string GuardLogPath => Path.Combine(LogsRoot, "guard.log");

    /// <summary>
    /// 守卫的数据目录：基线与一次性请求文件都放这里。
    /// </summary>
    public string GuardRoot => Path.Combine(LocalRoot, "guard");

    /// <summary>
    /// 法定日历数据目录（FR-15）：<c>{LocalRoot}\holidays</c>，每年一个 <c>{year}.json</c>。
    /// </summary>
    /// <remarks>
    /// 🔴 落 **Local**：它不是个人配置，是"下载来的可复现缓存"。
    /// 放进会漫游的目录会让一台机器下载的数据被同步到另一台，而另一台的用户并不知道这份数据从哪来。
    /// 目录由写入方按需创建（<c>AtomicFileWriter.WriteAllText</c> 自带建目录），读取方目录不存在即视为"无数据"。
    /// </remarks>
    public string HolidaysRoot => Path.Combine(LocalRoot, "holidays");

    /// <summary>新增自启动项检测的基线快照路径（D74）。</summary>
    public string GuardBaselinePath => Path.Combine(GuardRoot, "baseline.json");

    /// <summary>
    /// 跨进程 UI 定位请求文件路径（D74）：守卫点「查看」时写，管理端实例读取后删除。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 唤起用的是 <c>EventWaitHandle</c>，**不带载荷**，来源参数跨进程传不过去，
    /// 因此需要这份一次性文件作为旁路通道。
    /// </para>
    /// <para>
    /// 🔴 D82 起它还是**跨完整性级别**的唯一通道：未提权的点击方（Shell 按 <c>delaystart:</c>
    /// 协议拉起的管理端进程，中完整性）既写不了提权实例（高完整性）的内核对象状态，
    /// 也发不出它认的窗口消息，只能把意图写进文件，由实例侧的文件监听取走
    /// （见 <c>App.StartRequestWatcher</c>）。它落在用户目录里、标签是中完整性，两边都写得进去。
    /// </para>
    /// </remarks>
    public string UiRequestFilePath => Path.Combine(LocalRoot, "ui-request.json");


    /// <summary>管理端可执行文件完整路径，注册计划任务时不用它（计划任务指向调度端），仅用于诊断展示。</summary>
    public string ManagerExecutablePath => Path.Combine(InstalledRoot, ManagerExecutableName);

    /// <summary>调度端可执行文件完整路径 —— 计划任务 action 的目标（FR-11.1）。</summary>
    public string SchedulerExecutablePath => Path.Combine(InstalledRoot, SchedulerExecutableName);

    /// <summary>守卫可执行文件完整路径 —— 守卫计划任务 action 的目标（D74）。</summary>
    public string GuardExecutablePath => Path.Combine(InstalledRoot, GuardExecutableName);

    /// <summary>
    /// 通知中转器可执行文件完整路径（N1）—— 与调度端同落点（<c>{app}</c> 根目录），
    /// 调度端按 <see cref="InstalledRoot"/>（= 自身所在目录）就近解析。
    /// </summary>
    public string NotifyBrokerExecutablePath => Path.Combine(InstalledRoot, NotifyBrokerExecutableName);

    /// <summary>
    /// 临时交换目录（D144）：<c>%TEMP%\DelayStart\</c>，放卸载器与提权进程之间的结果文件。
    /// </summary>
    /// <remarks>
    /// 🔴 它是卸载还原链路的**唯一**落点，必须与 Inno 卸载器的
    /// <c>ExpandConstant('{tmp}\DelayStart')</c> 逐字一致 —— 两边不一致的表现是
    /// 提权进程判"路径不在白名单内"而拒绝写，卸载器则等满 60 秒再问用户
    /// "恢复程序没有返回结果"，一次卸载白等一分钟。
    /// <para>
    /// 为什么落在 TEMP 而不是 <see cref="LocalRoot"/>：它只在卸载期间存在、卸载结束即删，
    /// 是一次性交付物而不是运行时数据；而 <c>{tmp}</c> 是卸载器与被拉起的提权进程
    /// **双方都必然可写**的唯一目录（提权后环境变量不变）。
    /// </para>
    /// <para>
    /// 🔴 **刻意是静态**：它不依赖任何根目录覆盖变量（<c>DELAYSTART_LOCAL_DIR</c> 等），
    /// 挂成实例成员只会被 CA1822 判为"没有访问实例数据"——那说明它本来就不该挂在实例上。
    /// </para>
    /// </remarks>
    public static string TempExchangeRoot => Path.Combine(Path.GetTempPath(), FolderName);

    /// <summary>全部**允许写入**的根目录。安装目录不在此列，这是 NFR-6.7 的可执行表述。</summary>
    public IReadOnlyList<string> WritableRoots => [LocalRoot, ConfigRoot, TempExchangeRoot];

    /// <summary>取某次运行的归档文件路径（<c>scheduler\archive\{runId}.json</c>，D116）。</summary>
    /// <param name="runId">运行标识，格式 <c>yyyyMMdd-HHmmss</c>。</param>
    /// <returns>归档文件完整路径。</returns>
    public string GetRunFilePath(string runId) => Path.Combine(SchedulerArchiveRoot, $"{runId}.json");

    /// <summary>取某一年份的法定日历文件路径（文件不一定存在）。</summary>
    /// <param name="year">年份，如 2026。</param>
    /// <returns>在 <see cref="HolidaysRoot"/> 下的完整路径。</returns>
    public string GetHolidayFilePath(int year) => Path.Combine(HolidaysRoot, $"{year}.json");

    /// <summary>
    /// 节假日自动检查的时间戳文件（§6.5：同一年 7 天内不重复联网）。
    /// </summary>
    /// <remarks>
    /// 放在 <see cref="HolidaysRoot"/> 下而不是别处：它与那份数据同生共死 ——
    /// 用户手工清空数据目录时，时间戳也该一起消失（否则下次启动会以为"刚查过"而不去补）。
    /// 文件名以 <c>.</c> 开头，与 <c>{year}.json</c> 一眼可分，不会被当成某年的数据。
    /// </remarks>
    public string HolidayCheckStampPath => Path.Combine(HolidaysRoot, ".last-check");

    /// <summary>
    /// 创建全部运行时目录（幂等）。**不会**创建或触碰安装目录。
    /// </summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(LocalRoot);
        Directory.CreateDirectory(ConfigRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(SchedulerRoot);
        Directory.CreateDirectory(SchedulerArchiveRoot);
    }

    private static string ResolveRoot(string? explicitValue, string environmentVariable, Environment.SpecialFolder fallback)
    {
        if (!string.IsNullOrWhiteSpace(explicitValue))
        {
            return Path.GetFullPath(explicitValue);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        return Path.Combine(Environment.GetFolderPath(fallback), FolderName);
    }

    private static string NormalizeDirectory(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
