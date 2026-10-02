using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Core.Tests.Fakes;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="RunStateService"/> 的单元测试：归档读写、宽松 schema 与"坏归档不许拖垮整批"
/// （D19 / FR-5.12 / FR-5.13 / FR-1.4）。
/// </summary>
/// <remarks>
/// <para>
/// 落点是 <see cref="PathService"/> 解析出的临时目录，因此"覆盖写""目录不存在""文件被写坏"
/// 三条路径都能真实走一遍文件系统，而不是靠替身推断。
/// </para>
/// <para>
/// 🔴 造数据时 **runId 的字典序必须与时间序一致**（<c>yyyyMMdd-HHmmss</c> 恰好满足），
/// 否则"按字典序倒排 == 按时间倒排"这条用例会退化成恒真断言。
/// </para>
/// </remarks>
public sealed class RunStateServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeLogSink _log = new();
    private readonly PathService _paths;
    private readonly RunStateService _service;

    /// <summary>搭好一套隔离在临时目录里的运行状态服务。</summary>
    public RunStateServiceTests()
    {
        _paths = new PathService(_temp.Combine("local"), _temp.Combine("config"), _temp.Path);
        _service = new RunStateService(_paths, _log);
    }

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Archive_写入后可读回_字段完全一致()
    {
        // Arrange
        var record = SampleRecord("20260919-080100", plannedCount: 2);

        // Act
        _service.Archive(record);
        var read = _service.ReadRecent(1);

        // Assert
        var actual = Assert.Single(read);
        Assert.Equal("20260919-080100", actual.RunId);
        Assert.Equal(new DateTimeOffset(2026, 9, 19, 8, 1, 0, TimeSpan.FromHours(8)), actual.StartedAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 19, 8, 6, 0, TimeSpan.FromHours(8)), actual.FinishedAt);
        Assert.True(actual.CompletedNormally);
        Assert.Equal(2, actual.PlannedCount);

        var item = Assert.Single(actual.Items);
        Assert.Equal("item-1", item.Id);
        Assert.Equal("微信", item.Name);
        Assert.Equal(30, item.Delay);
        Assert.Equal(RunItemState.Done, item.State);
    }

    [Fact]
    public void ReadRecent_上限为非正数_返回空列表()
    {
        // Arrange
        _service.Archive(SampleRecord("20260919-080100", plannedCount: 1));

        // Act / Assert：0 与负数都表示"不读"，不该抛也不该返回全量。
        Assert.Empty(_service.ReadRecent(0));
        Assert.Empty(_service.ReadRecent(-1));
    }

    [Fact]
    public void ReadRecent_归档目录不存在_返回空列表()
    {
        // 空态也是一种状态："还没有哪一轮跑完过"正是它要表达的事（D125）。
        Assert.False(Directory.Exists(_service.ArchiveRoot));
        Assert.Empty(_service.ReadRecent(10));
    }

    [Fact]
    public void ReadRecent_归档多于上限_只取最近N份且按新到旧排序()
    {
        // Arrange：5 份归档，runId 递增 ⇒ 字典序与时间序一致。
        foreach (var minute in Enumerable.Range(1, 5))
        {
            _service.Archive(SampleRecord($"20260919-080{minute}00", plannedCount: minute));
        }

        // Act
        var read = _service.ReadRecent(3);

        // Assert：条数被上限截断，且顺序是**新 → 旧**（不是落盘顺序）。
        Assert.Equal(
            ["20260919-080500", "20260919-080400", "20260919-080300"],
            read.Select(static record => record.RunId));
    }

    [Fact]
    public void ReadRecent_同一份归档重复读取_结果稳定不随读取次数变化()
    {
        // "读即删"是 ui-request 通道的语义，不是归档的：读归档不该有副作用。
        _service.Archive(SampleRecord("20260919-080100", plannedCount: 1));

        Assert.Single(_service.ReadRecent(5));
        Assert.Single(_service.ReadRecent(5));
        Assert.True(File.Exists(_paths.GetRunFilePath("20260919-080100")));
    }

    [Fact]
    public void Archive_归档超过三十份_裁剪后恰好保留最新三十份()
    {
        // Arrange / Act：35 次写入，每次写入后都会触发裁剪。
        foreach (var minute in Enumerable.Range(1, 35))
        {
            _service.Archive(SampleRecord($"20260919-{minute / 60:00}-{minute % 60:00}", plannedCount: 1));
        }

        // Assert
        var remaining = Directory.GetFiles(_service.ArchiveRoot, "*.json");
        Assert.Equal(RunStateService.MaxArchivedRuns, remaining.Length);
        Assert.Equal(30, RunStateService.MaxArchivedRuns);

        // 留下的必须是**最新的 30 份**（字典序最大的 30 个）。
        Assert.Equal(
            Enumerable.Range(6, 30).Select(minute => $"20260919-{minute / 60:00}-{minute % 60:00}").Order(),
            remaining.Select(static path => Path.GetFileNameWithoutExtension(path)).Order());

        // 最旧的几份确实被删掉了（不是"没写过"）。
        Assert.False(File.Exists(_paths.GetRunFilePath("20260919-00-01")));
        Assert.True(File.Exists(_paths.GetRunFilePath("20260919-00-35")));
    }

    [Fact]
    public void Archive_归档未超过上限_一份都不删()
    {
        foreach (var minute in Enumerable.Range(1, 30))
        {
            _service.Archive(SampleRecord($"20260919-0800{minute:00}", plannedCount: 1));
        }

        Assert.Equal(30, Directory.GetFiles(_service.ArchiveRoot, "*.json").Length);
        Assert.True(File.Exists(_paths.GetRunFilePath("20260919-080001")));
        Assert.Equal(0, _log.Count);
    }

    [Fact]
    public void Archive_同一runId重复写入_覆盖为一份且以最后一次为准()
    {
        // RunId 是幂等键（FR-5.13）：同一轮重试收尾不会堆出两份归档。
        _service.Archive(SampleRecord("20260919-080100", plannedCount: 1));
        _service.Archive(SampleRecord("20260919-080100", plannedCount: 9));

        var actual = Assert.Single(_service.ReadRecent(5));
        Assert.Single(Directory.GetFiles(_service.ArchiveRoot, "*.json"));
        Assert.Equal(9, actual.PlannedCount);
    }

    [Fact]
    public void Archive_记录为null_抛ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _service.Archive(null!));
    }

    [Fact]
    public void Archive_runId为空白_抛ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => _service.Archive(new RunRecord { RunId = "   " }));
    }

    [Fact]
    public void ReadRecent_存在一份内容不合法的归档_跳过该份并记警告其余照常返回()
    {
        // Arrange：两份好归档夹一份被写坏的文件（模拟崩溃 / 半截写入）。
        _service.Archive(SampleRecord("20260919-080100", plannedCount: 1));
        _service.Archive(SampleRecord("20260919-080201", plannedCount: 2));
        var brokenPath = _paths.GetRunFilePath("20260919-080302");
        AtomicFileWriter.WriteAllText(brokenPath, "这不是 JSON，只是一段被截断的文本");

        // Act
        var read = _service.ReadRecent(10);

        // Assert：🔴 单份失败不许让整个调度日志页空掉（FR-1.4 的同一条原则）。
        Assert.Equal(["20260919-080201", "20260919-080100"], read.Select(static record => record.RunId));

        // 且必须留下 Warn —— 否则"历史凭空少了一条"这件事永远查不出来。
        Assert.True(_log.Contains(LogLevel.Warn, "运行归档无法读取"), "坏归档必须记一条 Warn");
        Assert.True(_log.Contains(LogLevel.Warn, brokenPath), "Warn 里要带上出问题的文件路径");
        Assert.True(_log.HasExceptionAt(LogLevel.Warn), "必须走带异常的重载，否则栈会丢");
    }

    [Fact]
    public void ReadRecent_坏归档排在上限之内_只影响它自己不减少其余条目的可见性()
    {
        // 上限 2、最新的一份恰好是坏的 ⇒ 用户看到的是"少一条"而不是"整页空"。
        _service.Archive(SampleRecord("20260919-080100", plannedCount: 1));
        _service.Archive(SampleRecord("20260919-080201", plannedCount: 2));
        AtomicFileWriter.WriteAllText(_paths.GetRunFilePath("20260919-080302"), "{ broken");

        var read = _service.ReadRecent(2);

        Assert.Equal(["20260919-080201"], read.Select(static record => record.RunId));
    }

    [Fact]
    public void CreateRunId_给定本地时间_产出yyyyMMddHHmmss格式()
    {
        // 格式本身就是契约：它同时是归档文件名，而清理按字符串序进行（D19）。
        var runId = RunStateService.CreateRunId(
            new DateTimeOffset(2026, 9, 19, 8, 41, 12, TimeSpan.FromHours(8)));

        Assert.Equal("20260919-084112", runId);
        Assert.Equal(runId, Path.GetFileNameWithoutExtension(_paths.GetRunFilePath(runId)));
    }

    [Fact]
    public void CreateRunId_同一时刻两次调用_结果相同()
    {
        // 🔴 这是**幂等**不是"每次都不同"：runId 由开始时刻决定，同一轮重试收尾时
        // 再算一次必须得到同一个值，否则 Archive 的幂等键就失效了。
        var moment = new DateTimeOffset(2026, 9, 19, 8, 41, 12, TimeSpan.FromHours(8));

        Assert.Equal(
            RunStateService.CreateRunId(moment),
            RunStateService.CreateRunId(moment));
    }

    [Fact]
    public void CreateRunId_相差一秒的两次调用_结果不同()
    {
        // 唯一性来自"一秒一个 runId"这个粒度；两轮只要不在同一秒启动就不会撞。
        var earlier = new DateTimeOffset(2026, 9, 19, 8, 41, 12, TimeSpan.FromHours(8));
        var later = earlier.AddSeconds(1);

        Assert.NotEqual(RunStateService.CreateRunId(earlier), RunStateService.CreateRunId(later));
    }

    [Fact]
    public void ReadRecent_归档含未知字段_仍解析出已知字段()
    {
        // 🔴 #25：JsonContext 刻意**不加严**未映射字段（UnmappedMemberHandling）。
        // 加严的后果不是"多一个字段报错"，而是来自更高版本、多了字段的归档被
        // ReadRecent 整条跳过 —— 用户看到的是历史凭空消失。对照组见
        // ConfigServiceTests.Load_UnknownField_Throws_AndPreservesCopy（配置侧是加严的）。
        Directory.CreateDirectory(_service.ArchiveRoot);
        AtomicFileWriter.WriteAllText(
            _paths.GetRunFilePath("20260919-100000"),
            """
            {
              "runId": "20260919-100000",
              "startedAt": "2026-09-19T10:00:00+08:00",
              "finishedAt": "2026-09-19T10:05:00+08:00",
              "completedNormally": true,
              "plannedCount": 2,
              "futureVersionOnlyField": { "nested": [1, 2, 3] },
              "items": [
                {
                  "id": "item-1",
                  "name": "微信",
                  "delay": 30,
                  "state": "Done",
                  "anotherUnknownItemField": 42
                }
              ]
            }
            """);

        var actual = Assert.Single(_service.ReadRecent(5));

        Assert.Equal("20260919-100000", actual.RunId);
        Assert.True(actual.CompletedNormally);
        Assert.Equal(2, actual.PlannedCount);
        Assert.Equal(new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.FromHours(8)), actual.StartedAt);
        Assert.Equal("微信", Assert.Single(actual.Items).Name);
        Assert.Equal(0, _log.Count);
    }

    [Fact]
    public void ReadRecent_归档JSON为null字面量_该份被跳过且不记警告()
    {
        // "null" 能解析成功但没有对象：走的是 record is null 分支而不是异常分支，
        // 所以不该记 Warn（那会把"正常跳过"和"文件损坏"混成一条线索）。
        Directory.CreateDirectory(_service.ArchiveRoot);
        AtomicFileWriter.WriteAllText(_paths.GetRunFilePath("20260919-100001"), "null");
        _service.Archive(SampleRecord("20260919-100002", plannedCount: 1));

        var read = _service.ReadRecent(5);

        Assert.Equal(["20260919-100002"], read.Select(static record => record.RunId));
        Assert.Equal(0, _log.Count);
    }

    private static RunRecord SampleRecord(string runId, int plannedCount) => new()
    {
        RunId = runId,
        StartedAt = new DateTimeOffset(2026, 9, 19, 8, 1, 0, TimeSpan.FromHours(8)),
        FinishedAt = new DateTimeOffset(2026, 9, 19, 8, 6, 0, TimeSpan.FromHours(8)),
        CompletedNormally = true,
        PlannedCount = plannedCount,
        Items =
        [
            new RunItemResult
            {
                Id = "item-1",
                Name = "微信",
                Delay = 30,
                State = RunItemState.Done,
                Attempts = 1,
            },
        ],
    };
}