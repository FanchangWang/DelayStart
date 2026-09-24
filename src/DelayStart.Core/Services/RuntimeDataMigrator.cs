using DelayStart.Core.Abstractions;

namespace DelayStart.Core.Services;

/// <summary>
/// 旧版运行时数据目录的一次性迁移（D116）。
/// </summary>
/// <remarks>
/// <para>
/// D116 把运行时数据**按进程归堆**：<c>state\current-run.json</c> 与 <c>runs\*.json</c>
/// 分别迁入 <c>scheduler\current-run.json</c> 与 <c>scheduler\archive\</c>（文件名不变，
/// <c>yyyyMMdd-HHmmss</c> 本来就是排序键）。升级时旧文件还在原位 —— 本类在管理端启动
/// （CLI 与 GUI 共用组合根，各自入口先跑这一步）、任何读写之前把它们按新名字与新结构搬过去；
/// 调度端**不跑迁移**，它只按新路径写。
/// </para>
/// <para>
/// 🔴 [可移除] 运行几个版本（旧目录已随升级消失）后，可整体删除本类、
/// <c>ServiceRegistration</c> 与两个宿主入口里的接线、以及对应测试 —— 届时
/// 什么都不用改：本类对"旧目录不存在"的处理就是直接返回（幂等，无版本标记）。
/// </para>
/// <para>
/// 失败语义（FR-1.4）：任何一步失败只记日志，不抛、不阻塞启动 —— 残留的旧文件
/// 下次启动会再试一遍。已知窗口：升级后调度端先跑（写新路径）而管理端一直没开，
/// 调度日志页会暂时少旧记录；管理端一开、迁移一跑就补齐。
/// </para>
/// </remarks>
public sealed class RuntimeDataMigrator
{
    /// <summary>旧实时状态目录（D116 前的 <c>state\</c>）。</summary>
    private const string LegacyStateDirectoryName = "state";

    /// <summary>旧运行归档目录（D116 前的 <c>runs\</c>）。</summary>
    private const string LegacyRunsDirectoryName = "runs";

    private readonly PathService _paths;
    private readonly ILogSink _log;

    /// <summary>构造迁移器。</summary>
    /// <param name="paths">路径解析服务。</param>
    /// <param name="log">日志接收端（管理端日志）。</param>
    public RuntimeDataMigrator(PathService paths, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);

        _paths = paths;
        _log = log;
    }

    /// <summary>
    /// 把旧目录里的运行时数据迁到新结构（幂等）。旧目录不存在时什么都不做。
    /// </summary>
    /// <remarks>
    /// 🔴 只在管理端启动路径上调用（<c>App.OnLaunched</c> / <c>CliHost.TryExecute</c>），
    /// 在任何读写 <c>current-run.json</c> 与归档的动作之前。
    /// </remarks>
    public void MigrateIfNeeded()
    {
        MigrateCurrentRun();
        MigrateRunArchives();
        DeleteEmptyLegacyDirectories();
    }

    /// <summary>迁 <c>state\current-run.json</c> → <c>scheduler\current-run.json</c>。</summary>
    private void MigrateCurrentRun()
    {
        var legacyDirectory = Path.Combine(_paths.LocalRoot, LegacyStateDirectoryName);
        var legacyFile = Path.Combine(legacyDirectory, "current-run.json");

        if (!File.Exists(legacyFile))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_paths.SchedulerRoot);

            if (File.Exists(_paths.CurrentRunFilePath))
            {
                // 目标已存在 = 调度端已经在按新路径写过实时状态（它不迁移、只写新路径），
                // 旧文件必然是过期现场 —— 丢弃而不是盖掉新状态。
                File.Delete(legacyFile);
            }
            else
            {
                File.Move(legacyFile, _paths.CurrentRunFilePath);
            }

            _log.Info($"已迁移调度实时状态：{legacyFile} → {_paths.CurrentRunFilePath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(ex, $"迁移调度实时状态失败（不影响启动，下次启动重试）：{legacyFile}");
        }
    }

    /// <summary>把 <c>runs\*.json</c> 逐个搬进 <c>scheduler\archive\</c>（同卷 Move，文件名不变）。</summary>
    private void MigrateRunArchives()
    {
        var legacyDirectory = Path.Combine(_paths.LocalRoot, LegacyRunsDirectoryName);
        if (!Directory.Exists(legacyDirectory))
        {
            return;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(legacyDirectory, "*.json");
            Directory.CreateDirectory(_paths.SchedulerArchiveRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(ex, $"迁移运行归档失败（不影响启动，下次启动重试）：{legacyDirectory}");
            return;
        }

        var moved = 0;
        foreach (var file in files)
        {
            var target = Path.Combine(_paths.SchedulerArchiveRoot, Path.GetFileName(file));
            try
            {
                if (File.Exists(target))
                {
                    // 归档名是开始时间戳，同名冲突意味着两边本来就有同一份 —— 旧的过期，丢弃。
                    File.Delete(file);
                }
                else
                {
                    File.Move(file, target);
                }

                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单份搬不动不拖累其余的：继续搬，残留下次启动再试。
                _log.Warn(ex, $"迁移运行归档失败（该份下次启动重试）：{file}");
            }
        }

        if (moved > 0)
        {
            _log.Info($"已迁移运行归档 {moved} 份：{legacyDirectory} → {_paths.SchedulerArchiveRoot}");
        }
    }

    /// <summary>删掉已搬空的旧目录（<see cref="Directory.Delete(string?)"/> 非空时会抛，正好当"还剩东西"处理）。</summary>
    private void DeleteEmptyLegacyDirectories()
    {
        TryDeleteEmptyDirectory(Path.Combine(_paths.LocalRoot, LegacyStateDirectoryName));
        TryDeleteEmptyDirectory(Path.Combine(_paths.LocalRoot, LegacyRunsDirectoryName));
    }

    private static void TryDeleteEmptyDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 里面还有没搬完的文件（或目录被占用）：留给下次启动，不值得多记一行。
        }
    }
}
