using DelayStart.Core.Models;

using Microsoft.Win32;

namespace DelayStart.Management.Services;

/// <summary>
/// <c>StartupApproved</c> 软禁用标记的读写 —— 机制 2 与机制 4 的唯一实现（FR-2.3–FR-2.5）。
/// </summary>
/// <remarks>
/// <para>
/// 这是整个"软件永不改用户数据"承诺的技术底座：禁用一个自启动项时，
/// 我们**不碰** <c>Run</c> 下的原值、**不移动**启动文件夹里的文件，
/// 只在并行的 <c>StartupApproved</c> 子键下写一个 12 字节标记 ——
/// 与任务管理器 / MSCONFIG 的做法完全一致（<c>docs/api-analysis.md</c> 1.2）。
/// </para>
/// <para>
/// 🔴 标记写错位置是**静默失效**：没有任何报错，用户以为禁用了其实没有。
/// 所以位置由 <see cref="StartupScope"/> 显式枚举决定（机制 3），绝不靠字符串猜 hive（坑 5）。
/// </para>
/// </remarks>
public static class StartupApprovedStore
{
    /// <summary>标记长度（字节），格式见 <see cref="CreateDisabledMarker"/>。</summary>
    public const int MarkerLength = 12;

    /// <summary>字节 0 取该值表示**已禁用**。</summary>
    public const byte DisabledFlag = 0x03;

    /// <summary>字节 0 取该值表示**显式启用**（与"无标记"效果相同）。</summary>
    public const byte EnabledFlag = 0x02;

    private const string MarkerRootSubKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>
    /// 生成禁用标记的 12 字节内容（FR-2.4）。
    /// </summary>
    /// <param name="now">写入时刻，取其 UTC FILETIME。注入而非内部取时钟，便于测试断言。</param>
    /// <returns>
    /// <c>[0]=0x03</c>，<c>[1..3]=0</c>，<c>[4..11]</c> 为小端 UTC FILETIME。
    /// </returns>
    public static byte[] CreateDisabledMarker(DateTimeOffset now)
    {
        var marker = new byte[MarkerLength];
        marker[0] = DisabledFlag;

        // DateTimeOffset.UtcDateTime 的 Kind 为 Utc，ToFileTime() 才是正确的 UTC 语义。
        _ = BitConverter.TryWriteBytes(marker.AsSpan(4, 8), now.UtcDateTime.ToFileTime());
        return marker;
    }

    /// <summary>
    /// 取得键名匹配的候选序列（机制 2 的三级回退 / 坑 1）。
    /// </summary>
    /// <param name="valueName">注册表值名或启动文件夹里的文件名。</param>
    /// <returns>按优先级排列、已去重的候选名。</returns>
    /// <remarks>
    /// 任务管理器写标记时用的名字与原值名**不保证一致**（有时补了 <c>.exe</c>，有时又去掉）。
    /// 少试一个候选就会把已禁用的项误报为"启用"，用户会看到自己明明禁过的程序又"自己开了"。
    /// </remarks>
    public static IReadOnlyList<string> GetCandidateNames(string valueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);

        var candidates = new List<string>(3);
        AddCandidate(candidates, valueName);
        AddCandidate(candidates, valueName + ".exe");
        AddCandidate(candidates, Path.GetFileNameWithoutExtension(valueName));

        return candidates;
    }

    /// <summary>
    /// 判断某项当前是否处于软禁用状态。
    /// </summary>
    /// <param name="source">来源类型，决定标记所在的子键（Run / Run32 / StartupFolder）。</param>
    /// <param name="scope">作用域，决定 hive（HKCU / HKLM）。</param>
    /// <param name="valueName">注册表值名或启动文件夹文件名。</param>
    /// <returns>命中禁用标记时为 <see langword="true"/>；无标记、标记为启用、或该来源不适用标记时为 <see langword="false"/>。</returns>
    public static bool IsDisabled(StartupSource source, StartupScope scope, string valueName)
    {
        if (GetMarkerSubKeyPath(source, scope) is not { } subKeyPath)
        {
            return false;
        }

        return WithMarkerKey(
            scope,
            subKeyPath,
            writable: false,
            key =>
            {
                if (key is null)
                {
                    // 连 StartupApproved 子键都不存在 → 从未被任何工具禁用过。
                    return false;
                }

                foreach (var candidate in GetCandidateNames(valueName))
                {
                    if (key.GetValue(candidate) is byte[] { Length: > 0 } data)
                    {
                        return data[0] == DisabledFlag;
                    }
                }

                return false;
            });
    }

    /// <summary>
    /// 写入禁用标记（FR-2.1 / FR-2.5）。
    /// </summary>
    /// <param name="source">来源类型。</param>
    /// <param name="scope">作用域。</param>
    /// <param name="valueName">注册表值名或启动文件夹文件名。</param>
    /// <param name="now">写入时刻。</param>
    /// <exception cref="StartupOperationException">
    /// 该来源不支持软禁用标记，或写入被 ACL / 组策略拒绝（FR-2.6）。
    /// </exception>
    public static void Disable(StartupSource source, StartupScope scope, string valueName, DateTimeOffset now)
    {
        var subKeyPath = GetMarkerSubKeyPath(source, scope)
            ?? throw new StartupOperationException(
                StartupFailureReason.ScheduledTaskFailed,
                entryId: BuildDiagnosticId(source, scope, valueName),
                message: $"{DescribeSource(source, scope)} 不支持 StartupApproved 标记，无法软禁用『{valueName}』。");

        WriteMarker(scope, subKeyPath, valueName, CreateDisabledMarker(now), source);
    }

    /// <summary>
    /// 删除标记，把该项恢复为系统默认状态（FR-2.2）。
    /// </summary>
    /// <param name="source">来源类型。</param>
    /// <param name="scope">作用域。</param>
    /// <param name="valueName">注册表值名或启动文件夹文件名。</param>
    /// <remarks>
    /// 删除时**三个候选名都要删**，不能只删命中的那一个：历史遗留的标记可能同时存在多个形式
    /// （用户先后用任务管理器和别的工具操作过），留下任何一个都会让该项继续保持禁用。
    /// 值不存在时静默跳过，因此本操作可重复执行。
    /// </remarks>
    /// <exception cref="StartupOperationException">删除被拒绝时抛出。</exception>
    public static void Enable(StartupSource source, StartupScope scope, string valueName)
    {
        if (GetMarkerSubKeyPath(source, scope) is not { } subKeyPath)
        {
            return;
        }

        try
        {
            WithMarkerKey(
                scope,
                subKeyPath,
                writable: true,
                key =>
                {
                    if (key is null)
                    {
                        return true;
                    }

                    foreach (var candidate in GetCandidateNames(valueName))
                    {
                        key.DeleteValue(candidate, throwOnMissingValue: false);
                    }

                    return true;
                });
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            throw new StartupOperationException(
                StartupFailureReason.AccessDenied,
                entryId: BuildDiagnosticId(source, scope, valueName),
                message: $"删除『{valueName}』的软禁用标记被拒绝（{DescribeSource(source, scope)}）：{ex.Message}",
                innerException: ex);
        }
    }

    /// <summary>
    /// 取标记所在的注册表子键路径（机制 4 的矩阵）。
    /// </summary>
    /// <param name="source">来源类型。</param>
    /// <param name="scope">作用域。</param>
    /// <returns>子键路径；该来源不使用标记（计划任务 / UWP / 手动）时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// ⚠️ <see cref="StartupScope.HklmWow"/> 用的是 <c>Run32</c> 而**不是** <c>Run</c>（坑 2）。
    /// 写错到 <c>Run</c> 会完全失效且不报错。
    /// </remarks>
    public static string? GetMarkerSubKeyPath(StartupSource source, StartupScope scope) => (source, scope) switch
    {
        (StartupSource.Registry, StartupScope.Hkcu) => MarkerRootSubKey + @"\Run",
        (StartupSource.Registry, StartupScope.Hklm) => MarkerRootSubKey + @"\Run",
        (StartupSource.Registry, StartupScope.HklmWow) => MarkerRootSubKey + @"\Run32",
        (StartupSource.StartupFolder, StartupScope.UserFolder) => MarkerRootSubKey + @"\StartupFolder",
        (StartupSource.StartupFolder, StartupScope.SystemFolder) => MarkerRootSubKey + @"\StartupFolder",
        _ => null,
    };

    private static void WriteMarker(
        StartupScope scope,
        string subKeyPath,
        string valueName,
        byte[] marker,
        StartupSource source)
    {
        try
        {
            WithMarkerKey(
                scope,
                subKeyPath,
                writable: true,
                key =>
                {
                    // CreateSubKey(writable: true) 已保证子键存在（FR-2.5）；
                    // 这里 key 为 null 只可能是 hive 本身打不开。
                    if (key is null)
                    {
                        throw new UnauthorizedAccessException($"无法创建或打开注册表子键：{subKeyPath}");
                    }

                    key.SetValue(valueName, marker, RegistryValueKind.Binary);
                    return true;
                });
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            throw new StartupOperationException(
                StartupFailureReason.AccessDenied,
                entryId: BuildDiagnosticId(source, scope, valueName),
                message: $"写入『{valueName}』的软禁用标记被拒绝（{DescribeSource(source, scope)}）：{ex.Message}"
                    + "。若该项由组策略下发，请从组策略侧调整。",
                innerException: ex);
        }
    }

    /// <summary>
    /// 在正确 hive 上打开标记子键并执行操作，保证句柄被释放。
    /// </summary>
    /// <remarks>
    /// 全部走 <see cref="RegistryView.Registry64"/>：即使进程是 64 位，显式指定视图也能避免
    /// 在 32 位宿主下被重定向到 WOW6432Node（<c>api-analysis.md</c> 1.1 的读取要点）。
    /// WOW6432Node 的位置是用**显式子键路径**表达的，不靠视图切换。
    /// </remarks>
    private static T WithMarkerKey<T>(StartupScope scope, string subKeyPath, bool writable, Func<RegistryKey?, T> action)
    {
        var hive = scope switch
        {
            StartupScope.Hkcu or StartupScope.UserFolder => RegistryHive.CurrentUser,
            StartupScope.Hklm or StartupScope.HklmWow or StartupScope.SystemFolder => RegistryHive.LocalMachine,
            _ => (RegistryHive?)null,
        };

        if (hive is not { } resolvedHive)
        {
            return action(null);
        }

        using var baseKey = RegistryKey.OpenBaseKey(resolvedHive, RegistryView.Registry64);
        using var key = writable
            ? baseKey.CreateSubKey(subKeyPath, writable: true)
            : baseKey.OpenSubKey(subKeyPath, writable: false);

        return action(key);
    }

    private static void AddCandidate(List<string> candidates, string? candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate)
            && !candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(candidate);
        }
    }

    private static string BuildDiagnosticId(StartupSource source, StartupScope scope, string valueName)
        => $"{source}:{scope}:{valueName}";

    private static string DescribeSource(StartupSource source, StartupScope scope) => (source, scope) switch
    {
        (StartupSource.Registry, StartupScope.Hkcu) => "当前用户的注册表启动项",
        (StartupSource.Registry, StartupScope.Hklm) => "所有用户的注册表启动项",
        (StartupSource.Registry, StartupScope.HklmWow) => "所有用户的 32 位注册表启动项",
        (StartupSource.StartupFolder, StartupScope.UserFolder) => "当前用户的启动文件夹",
        (StartupSource.StartupFolder, StartupScope.SystemFolder) => "所有用户的启动文件夹",
        _ => source.ToString(),
    };
}
