using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Models;

namespace DelayStart.Management.Services;

/// <summary>
/// 对已有的延时条目做**条目级修改**并原子保存（FR-4.5 / FR-4.6 / FR-4.7 与手动添加）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="TakeoverService"/> 的分工：那个服务管"接管 / 释放"这种**会动到系统**的
/// 复合事务，本服务只管"改配置里已经存在的那条记录"。边界画在这里，是因为后者
/// 有三个动作（改延时、切开关、调顺序）都**不碰系统任何东西** ——
/// 把它们塞进 <see cref="TakeoverService"/> 会让那个类的每个方法都要回答
/// "这次要不要禁用系统项"，而答案永远是"不要"。
/// </para>
/// <para>
/// 🔴 <b>唯一的例外是 <see cref="AddManual"/></b>：手动条目没有系统项可禁用，
/// 但它**需要调度器真的去启动它**，所以那一步必须确保计划任务存在（同 FR-3.3）。
/// </para>
/// <para>
/// 失败一律抛 <see cref="StartupOperationException"/> 并带上 <c>EntryId</c>。
/// 不返回布尔结果码：调用方（ViewModel）要的是"给用户看的一句话"，
/// 而异常消息天然就是那句话，结果码反而要再映射一次。
/// </para>
/// </remarks>
public sealed class ConfigEditService
{
    private readonly IAppConfigStore _configStore;
    private readonly ISchedulerTaskRegistrar _taskRegistrar;
    private readonly ILogSink _log;

    /// <summary>构造条目编辑服务。</summary>
    /// <param name="configStore">配置读写端。</param>
    /// <param name="taskRegistrar">调度计划任务注册端，仅手动添加新条目时用到。</param>
    /// <param name="log">日志接收端。</param>
    public ConfigEditService(
        IAppConfigStore configStore,
        ISchedulerTaskRegistrar taskRegistrar,
        ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(taskRegistrar);
        ArgumentNullException.ThrowIfNull(log);

        _configStore = configStore;
        _taskRegistrar = taskRegistrar;
        _log = log;
    }

    /// <summary>
    /// 手动添加一个条目（FR-3.4：不做任何系统改动）。
    /// </summary>
    /// <param name="values">用户在编辑器里填的内容。</param>
    /// <returns>新建的条目（含已分配的主键）。</returns>
    /// <remarks>
    /// <para>
    /// 主键用 <c>ItemKeyBuilder.ForManual()</c> 随机生成，**不用路径做主键**：
    /// 同一个 exe 被添加两次应当得到两个独立条目（用户可能就是想让它启动两次），
    /// 而按路径去重会让第二次添加静默失败。
    /// </para>
    /// <para>
    /// 计划任务注册失败时**回滚**（删掉刚加的条目）。不回滚的话用户会看到一个
    /// 永远不会启动的条目 —— 因为它没有系统自启动项，唯一的启动者就是调度端。
    /// </para>
    /// </remarks>
    public DelayedItem AddManual(DelayItemValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (string.IsNullOrWhiteSpace(values.Path))
        {
            throw new StartupOperationException(
                StartupFailureReason.TargetMissing,
                entryId: string.Empty,
                message: "请先选择要延时启动的程序。");
        }

        var config = _configStore.Load();

        var item = new DelayedItem
        {
            Id = ItemKeyBuilder.ForManual(),
            Name = string.IsNullOrWhiteSpace(values.Name)
                ? System.IO.Path.GetFileNameWithoutExtension(values.Path)
                : values.Name.Trim(),
            Path = values.Path,
            Arguments = values.Arguments,
            WorkingDirectory = values.WorkingDirectory,
            DelaySeconds = values.DelaySeconds,
            RunAsAdmin = values.RunAsAdmin,
            Enabled = true,

            // 手动条目在系统中的三个定位分量全是空的 —— 这正是"系统里没有对应物"的表达方式。
            Source = StartupSource.Manual,
            Scope = StartupScope.None,
            SourceKey = string.Empty,
            SourceDetail = string.Empty,

            // 手动条目从不经历"接管"，也就没有"接管前的状态"要还原。取 true 是为了让
            // 万一走到的恢复分支表现成"按用户配置的身份启动"，而不是"保持禁用"。
            OriginalState = new OriginalState { WasEnabled = true },
        };

        // 排在同延时组的最后：用户刚添加的条目不该插到已有条目前面去。
        item.SortOrder = NextSortOrder(config.Items, item.DelaySeconds);

        config.Items.Add(item);
        _configStore.Save(config);

        try
        {
            _taskRegistrar.RegisterOrUpdate();
        }
        catch (StartupOperationException ex)
        {
            _log.Error(ex, $"手动添加『{item.Name}』失败：计划任务注册失败，已回滚");
            RollbackAdd(item.Id);
            throw new StartupOperationException(
                ex.Reason,
                item.Id,
                $"注册调度计划任务失败，已撤销本次添加：{ex.Message}");
        }

        _log.Info($"已手动添加『{item.Name}』，延时 {item.DelaySeconds} 秒（{item.Id}）");
        return item;
    }

    /// <summary>
    /// 把编辑器的内容写回一个已存在的条目。
    /// </summary>
    /// <param name="itemId">条目主键。</param>
    /// <param name="values">用户在编辑器里填的内容。</param>
    /// <remarks>
    /// <b>系统来源的条目忽略名称 / 路径 / 工作目录</b>（见 <see cref="DelayItemValues"/> 的说明）。
    /// 界面层已经把这三个框置为只读，这里是第二道防线 ——
    /// 服务层不该假设调用方一定守规矩。
    /// </remarks>
    public void ApplyEdit(string itemId, DelayItemValues values)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        ArgumentNullException.ThrowIfNull(values);

        var config = _configStore.Load();
        var item = Find(config, itemId);

        var delayChanged = item.DelaySeconds != values.DelaySeconds;

        item.DelaySeconds = values.DelaySeconds;
        item.RunAsAdmin = values.RunAsAdmin;
        item.Arguments = values.Arguments;

        if (item.IsManual)
        {
            item.Name = string.IsNullOrWhiteSpace(values.Name) ? item.Name : values.Name.Trim();
            item.Path = values.Path;
            item.WorkingDirectory = values.WorkingDirectory;
        }

        if (delayChanged)
        {
            // 换到另一个延时的组：排到那一组的末尾，而不是带着旧的 SortOrder 插队。
            item.SortOrder = NextSortOrder(config.Items, item.DelaySeconds, exclude: item);
        }

        _configStore.Save(config);
        _log.Info($"已更新条目『{item.Name}』（{item.Id}）");
    }

    /// <summary>
    /// 切换条目级开关（FR-4.6）。
    /// </summary>
    /// <param name="itemId">条目主键。</param>
    /// <param name="enabled">是否参与本次登录的启动。</param>
    /// <returns>配置确实被改动时为 <see langword="true"/>；值没变则不写文件。</returns>
    /// <remarks>
    /// 🔴 关掉开关**不动系统的自启动项**：它仍处于软禁用状态，只是调度端跳过它。
    /// 这条必须成立，否则"临时不让它启动"就变成了一次不可逆的系统改动。
    /// </remarks>
    public bool SetEnabled(string itemId, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(itemId);

        var config = _configStore.Load();
        var item = Find(config, itemId);

        if (item.Enabled == enabled)
        {
            return false;
        }

        item.Enabled = enabled;
        _configStore.Save(config);
        _log.Info($"条目『{item.Name}』已{(enabled ? "启用" : "停用")}（{itemId}）");
        return true;
    }

    /// <summary>
    /// 在**同一延时**的组内上移 / 下移一位（FR-4.7）。
    /// </summary>
    /// <param name="itemId">条目主键。</param>
    /// <param name="delta">位移量，<c>-1</c> 上移、<c>+1</c> 下移。</param>
    /// <returns>
    /// 顺序确实变了并为 <see langword="true"/>；已在组内首 / 末位（或组内只有它一个）时为
    /// <see langword="false"/>，**不写文件**。
    /// </returns>
    /// <remarks>
    /// <para>
    /// 为什么不能用"延时不同也允许调"：列表顺序只在同一延时内才有意义 ——
    /// 延时不同的两项由时间本身决定先后，拖动它们会得到一个与显示顺序矛盾的配置。
    /// </para>
    /// <para>
    /// 每次移动前先把组内 <see cref="DelayedItem.SortOrder"/> **重新编号成 0..n-1**。
    /// 不这么做的话，历史配置里常见的"全部为 0"会让交换变成无效操作
    /// （两个 0 交换还是两个 0），用户点了上移却看不到任何变化。
    /// </para>
    /// </remarks>
    public bool Move(string itemId, int delta)
    {
        ArgumentNullException.ThrowIfNull(itemId);

        if (delta is not (-1 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(delta), delta, "位移量只能是 -1 或 +1。");
        }

        var config = _configStore.Load();
        var item = Find(config, itemId);

        var group = config.Items
            .Where(candidate => candidate.DelaySeconds == item.DelaySeconds)
            .OrderBy(static candidate => candidate, StartupSortComparer.Instance)
            .ToList();

        if (group.Count < 2)
        {
            return false;
        }

        for (var index = 0; index < group.Count; index++)
        {
            group[index].SortOrder = index;
        }

        var from = group.IndexOf(item);
        var to = from + delta;

        if (to < 0 || to >= group.Count)
        {
            // 已经在端点上。重编号只发生在内存里，不保存就不留痕迹。
            return false;
        }

        (group[from].SortOrder, group[to].SortOrder) = (group[to].SortOrder, group[from].SortOrder);

        _configStore.Save(config);
        _log.Info($"条目『{item.Name}』在 {item.DelaySeconds} 秒组内移动了 {delta} 位（{itemId}）");
        return true;
    }

    /// <summary>按主键取条目；找不到就抛带 <c>EntryId</c> 的异常。</summary>
    /// <param name="config">配置。</param>
    /// <param name="itemId">条目主键。</param>
    /// <returns>找到的条目。</returns>
    private static DelayedItem Find(AppConfig config, string itemId)
    {
        var item = config.Items.Find(candidate => string.Equals(candidate.Id, itemId, StringComparison.Ordinal));

        if (item is null)
        {
            throw new StartupOperationException(
                StartupFailureReason.Unknown,
                itemId,
                $"配置里找不到该条目（{itemId}）。它可能已被移出，请刷新列表后重试。");
        }

        return item;
    }

    /// <summary>取同延时组内的下一个可用序号。</summary>
    /// <param name="items">全部条目。</param>
    /// <param name="delaySeconds">目标延时。</param>
    /// <param name="exclude">计算时排除的条目（改延时的场景要排除它自己）。</param>
    /// <returns>组内最大序号 + 1；组内为空时为 0。</returns>
    private static int NextSortOrder(
        List<DelayedItem> items,
        int delaySeconds,
        DelayedItem? exclude = null)
    {
        var max = -1;

        foreach (var candidate in items)
        {
            if (candidate.DelaySeconds != delaySeconds || ReferenceEquals(candidate, exclude))
            {
                continue;
            }

            if (candidate.SortOrder > max)
            {
                max = candidate.SortOrder;
            }
        }

        return max + 1;
    }

    /// <summary>撤销一次手动添加（计划任务注册失败时）。</summary>
    /// <param name="itemId">刚加入的条目主键。</param>
    private void RollbackAdd(string itemId)
    {
        try
        {
            var config = _configStore.Load();
            if (config.Items.RemoveAll(candidate => string.Equals(candidate.Id, itemId, StringComparison.Ordinal)) > 0)
            {
                _configStore.Save(config);
            }
        }
        catch (Exception ex)
        {
            // 已经在异常路径上，再抛会盖掉原始错误。
            _log.Error(ex, $"回滚手动添加失败，配置里可能残留条目 {itemId}");
        }
    }
}
