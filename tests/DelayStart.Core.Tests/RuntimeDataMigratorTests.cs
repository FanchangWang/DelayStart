using DelayStart.Core.Services;
using DelayStart.Core.Tests.Fakes;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="RuntimeDataMigrator"/> 的迁移测试（D116）：旧目录 → 新结构的搬运、
/// 幂等、目标冲突时丢弃旧文件，以及"搬不动时不能阻塞启动"。
/// </summary>
/// <remarks>
/// 全部走真实文件系统（临时目录）：<see cref="PathService"/> 的覆盖构造让
/// <c>LocalRoot</c> 落进临时目录，迁移读写的就是被测路径本身。
/// </remarks>
public sealed class RuntimeDataMigratorTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    private PathService CreatePaths()
        => new(_temp.Combine("local"), _temp.Combine("config"), _temp.Path);

    private static RuntimeDataMigrator CreateMigrator(PathService paths)
        => new(paths, new FakeLogSink());

    [Fact]
    public void MigrateIfNeeded_NoLegacyDirectories_DoesNothing()
    {
        var paths = CreatePaths();

        // 全新安装：没有旧目录，必须是安静的空操作（幂等哨兵，不能抛）。
        CreateMigrator(paths).MigrateIfNeeded();

        Assert.False(Directory.Exists(paths.SchedulerRoot));
    }

    [Fact]
    public void MigrateIfNeeded_MovesCurrentRunIntoScheduler()
    {
        var paths = CreatePaths();
        var legacyState = Path.Combine(paths.LocalRoot, "state");
        Directory.CreateDirectory(legacyState);
        File.WriteAllText(
            Path.Combine(legacyState, "current-run.json"),
            """{"runId":"20260920-084112"}""");

        CreateMigrator(paths).MigrateIfNeeded();

        Assert.True(File.Exists(paths.CurrentRunFilePath));
        Assert.Contains("20260920-084112", File.ReadAllText(paths.CurrentRunFilePath));
        Assert.False(Directory.Exists(legacyState), "搬空后的旧目录应当被删除");
    }

    [Fact]
    public void MigrateIfNeeded_NewCurrentRunAlreadyExists_DropsLegacyFile()
    {
        // 升级后调度端先跑过一次（写新路径），旧文件是过期现场 —— 必须丢弃而不是盖掉新状态。
        var paths = CreatePaths();
        var legacyState = Path.Combine(paths.LocalRoot, "state");
        Directory.CreateDirectory(legacyState);
        File.WriteAllText(
            Path.Combine(legacyState, "current-run.json"),
            """{"runId":"20260919-084112"}""");
        Directory.CreateDirectory(paths.SchedulerRoot);
        File.WriteAllText(paths.CurrentRunFilePath, """{"runId":"20260924-160000"}""");

        CreateMigrator(paths).MigrateIfNeeded();

        Assert.True(File.Exists(paths.CurrentRunFilePath));
        Assert.Contains("20260924-160000", File.ReadAllText(paths.CurrentRunFilePath));
        Assert.False(Directory.Exists(legacyState), "旧文件被丢弃后，空目录也应一并删除");
    }

    [Fact]
    public void MigrateIfNeeded_MovesRunArchivesKeepingFileNames()
    {
        var paths = CreatePaths();
        var legacyRuns = Path.Combine(paths.LocalRoot, "runs");
        Directory.CreateDirectory(legacyRuns);
        File.WriteAllText(Path.Combine(legacyRuns, "20260920-084112.json"), """{"runId":"20260920-084112"}""");
        File.WriteAllText(Path.Combine(legacyRuns, "20260921-090000.json"), """{"runId":"20260921-090000"}""");

        CreateMigrator(paths).MigrateIfNeeded();

        Assert.True(File.Exists(Path.Combine(paths.SchedulerArchiveRoot, "20260920-084112.json")));
        Assert.True(File.Exists(Path.Combine(paths.SchedulerArchiveRoot, "20260921-090000.json")));
        Assert.False(Directory.Exists(legacyRuns), "搬空后的旧目录应当被删除");
    }

    [Fact]
    public void MigrateIfNeeded_SecondCall_IsIdempotent()
    {
        var paths = CreatePaths();
        var legacyState = Path.Combine(paths.LocalRoot, "state");
        var legacyRuns = Path.Combine(paths.LocalRoot, "runs");
        Directory.CreateDirectory(legacyState);
        File.WriteAllText(Path.Combine(legacyState, "current-run.json"), """{}""");
        Directory.CreateDirectory(legacyRuns);
        File.WriteAllText(Path.Combine(legacyRuns, "20260920-084112.json"), """{}""");

        var migrator = CreateMigrator(paths);
        migrator.MigrateIfNeeded();
        migrator.MigrateIfNeeded();

        // 第二遍：旧目录已不存在，必须什么都不做、也不抛。
        Assert.True(File.Exists(paths.CurrentRunFilePath));
        Assert.Single(Directory.GetFiles(paths.SchedulerArchiveRoot, "*.json"));
    }

    [Fact]
    public void MigrateIfNeeded_StrangerFileKeepsLegacyRunsDirectory_WithoutThrowing()
    {
        // 旧目录里混进非归档文件（用户自己放的）：只搬 .json；目录因非空删不掉，
        // 但迁移必须吞掉这个失败继续启动（FR-1.4），残留下次再试。
        var paths = CreatePaths();
        var legacyRuns = Path.Combine(paths.LocalRoot, "runs");
        Directory.CreateDirectory(legacyRuns);
        var stranger = Path.Combine(legacyRuns, "note.txt");
        File.WriteAllText(stranger, "用户自己的文件");
        File.WriteAllText(Path.Combine(legacyRuns, "20260920-084112.json"), """{}""");

        CreateMigrator(paths).MigrateIfNeeded();

        Assert.True(File.Exists(Path.Combine(paths.SchedulerArchiveRoot, "20260920-084112.json")));
        Assert.True(File.Exists(stranger), "不是归档的文件不属于迁移的处置范围");
        Assert.True(Directory.Exists(legacyRuns), "目录非空时删除会失败，只能留给用户处置");
    }
}
