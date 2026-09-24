using DelayStart.Core.Services;
using DelayStart.Core.Tests.Fakes;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="PathService"/> 的单元测试：路径解析、覆盖优先级与"安装目录只读"（D23 / NFR-6.7）。
/// </summary>
public sealed class PathServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ExplicitRoots_AreUsedVerbatim()
    {
        // Arrange
        var localRoot = _temp.Combine("local");
        var configRoot = _temp.Combine("config");

        // Act
        var paths = new PathService(localRoot, configRoot, _temp.Path);

        // Assert
        Assert.Equal(Path.GetFullPath(localRoot), paths.LocalRoot, ignoreCase: true);
        Assert.Equal(Path.GetFullPath(configRoot), paths.ConfigRoot, ignoreCase: true);
    }

    [Fact]
    public void RootsWithTrailingSeparator_AreNormalized()
    {
        // 尾部斜杠会让后续 Path.Combine 生成双斜杠路径，干扰字符串比对与计划任务参数
        var localRoot = _temp.Combine("local") + Path.DirectorySeparatorChar;

        var paths = new PathService(localRoot, _temp.Combine("config"), _temp.Path);

        Assert.Equal(Path.GetFullPath(_temp.Combine("local")), paths.LocalRoot, ignoreCase: true);
    }

    [Fact]
    public void ConfigRootAndLocalRoot_AreKeptApart()
    {
        // D23：配置走 Roaming（可漫游的个人偏好），日志与状态走 Local（机器相关）。
        // 两者必须分家，否则漫游配置会把机器相关的日志一起搬走。
        var paths = new PathService(_temp.Combine("local"), _temp.Combine("config"), _temp.Path);

        Assert.NotEqual(paths.LocalRoot, paths.ConfigRoot);
        Assert.True(
            paths.ConfigFilePath.StartsWith(paths.ConfigRoot, StringComparison.OrdinalIgnoreCase),
            "config.json 必须落在 Roaming 根下");
        Assert.True(
            paths.SchedulerLogPath.StartsWith(paths.LocalRoot, StringComparison.OrdinalIgnoreCase),
            "日志必须落在 Local 根下");
        Assert.True(
            paths.CurrentRunFilePath.StartsWith(paths.LocalRoot, StringComparison.OrdinalIgnoreCase),
            "实时状态必须落在 Local 根下");
    }

    [Fact]
    public void WritableRoots_DoNotContainInstalledRoot()
    {
        // NFR-6.7 的可执行表述：安装目录永远不是写入目标
        var installedRoot = _temp.Combine("install");
        var paths = new PathService(_temp.Combine("local"), _temp.Combine("config"), installedRoot);

        Assert.DoesNotContain(paths.InstalledRoot, paths.WritableRoots);
        Assert.Contains(paths.LocalRoot, paths.WritableRoots);
        Assert.Contains(paths.ConfigRoot, paths.WritableRoots);
    }

    [Fact]
    public void DerivedPaths_AreBuiltFromTheirRoots()
    {
        // Arrange
        var paths = new PathService(_temp.Combine("local"), _temp.Combine("config"), _temp.Path);

        // Act / Assert
        Assert.Equal(Path.Combine(paths.ConfigRoot, "config.json"), paths.ConfigFilePath);
        Assert.Equal(Path.Combine(paths.LogsRoot, "scheduler.log"), paths.SchedulerLogPath);
        Assert.Equal(Path.Combine(paths.LogsRoot, "manager.log"), paths.ManagerLogPath);
        Assert.Equal(Path.Combine(paths.SchedulerRoot, "current-run.json"), paths.CurrentRunFilePath);
        Assert.Equal(Path.Combine(paths.LocalRoot, "logs"), paths.LogsRoot);
        Assert.Equal(Path.Combine(paths.LocalRoot, "scheduler"), paths.SchedulerRoot);
        Assert.Equal(Path.Combine(paths.SchedulerRoot, "archive"), paths.SchedulerArchiveRoot);
        Assert.Equal(Path.Combine(paths.GuardRoot, "inspections"), paths.GuardInspectionsRoot);
    }

    [Fact]
    public void GetRunFilePath_PlacesArchiveUnderSchedulerArchiveRoot()
    {
        // D116：运行归档收进 scheduler\archive\。
        var paths = new PathService(_temp.Combine("local"), _temp.Combine("config"), _temp.Path);

        var runFilePath = paths.GetRunFilePath("20260919-084112");

        Assert.Equal(Path.Combine(paths.SchedulerArchiveRoot, "20260919-084112.json"), runFilePath);
    }

    [Fact]
    public void ExecutablePaths_PointIntoInstalledRoot()
    {
        // FR-11.1：计划任务的 action 指向调度端 exe，路径必须落在安装目录下
        var paths = new PathService(_temp.Combine("local"), _temp.Combine("config"), _temp.Combine("install"));

        Assert.Equal(Path.Combine(paths.InstalledRoot, "DelayStart.Scheduler.exe"), paths.SchedulerExecutablePath);
        Assert.Equal(Path.Combine(paths.InstalledRoot, "DelayStart.exe"), paths.ManagerExecutablePath);
    }

    [Fact]
    public void EnsureCreated_CreatesEveryRuntimeDirectory()
    {
        // Arrange
        var paths = new PathService(_temp.Combine("local"), _temp.Combine("config"), _temp.Combine("install"));

        // Act
        paths.EnsureCreated();

        // Assert
        Assert.True(Directory.Exists(paths.LocalRoot));
        Assert.True(Directory.Exists(paths.ConfigRoot));
        Assert.True(Directory.Exists(paths.LogsRoot));
        Assert.True(Directory.Exists(paths.SchedulerRoot));
        Assert.True(Directory.Exists(paths.SchedulerArchiveRoot));
    }

    [Fact]
    public void EnsureCreated_DoesNotTouchInstalledRoot()
    {
        // NFR-6.7：安装目录只读，程序不得在那里创建任何东西
        var installedRoot = _temp.Combine("install");
        var paths = new PathService(_temp.Combine("local"), _temp.Combine("config"), installedRoot);

        paths.EnsureCreated();

        Assert.False(Directory.Exists(Path.GetFullPath(installedRoot)));
    }

    [Fact]
    public void EnsureCreated_IsIdempotent()
    {
        // 每次启动都会调它，重复调用必须是安全的
        var paths = new PathService(_temp.Combine("local"), _temp.Combine("config"), _temp.Path);

        paths.EnsureCreated();
        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.LocalRoot));
    }

    [Fact]
    public void ExplicitOverride_WinsOverEnvironmentVariable()
    {
        // Arrange
        var explicitRoot = _temp.Combine("explicit");
        var previous = Environment.GetEnvironmentVariable(PathService.LocalRootOverrideVariable);

        try
        {
            Environment.SetEnvironmentVariable(
                PathService.LocalRootOverrideVariable, _temp.Combine("from-env"));

            // Act
            var paths = new PathService(explicitRoot, _temp.Combine("config"), _temp.Path);

            // Assert
            Assert.Equal(Path.GetFullPath(explicitRoot), paths.LocalRoot, ignoreCase: true);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathService.LocalRootOverrideVariable, previous);
        }
    }

    [Fact]
    public void EnvironmentVariable_IsUsedWhenNoExplicitOverride()
    {
        // 调试覆盖开关（docs/design.md §1.5 规则 3）：只用于开发与测试
        var fromEnvironment = _temp.Combine("from-env");
        var previous = Environment.GetEnvironmentVariable(PathService.LocalRootOverrideVariable);

        try
        {
            Environment.SetEnvironmentVariable(PathService.LocalRootOverrideVariable, fromEnvironment);

            // Act
            var paths = new PathService(localRoot: null, configRoot: _temp.Combine("config"), installedRoot: _temp.Path);

            // Assert
            Assert.Equal(Path.GetFullPath(fromEnvironment), paths.LocalRoot, ignoreCase: true);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathService.LocalRootOverrideVariable, previous);
        }
    }
}
