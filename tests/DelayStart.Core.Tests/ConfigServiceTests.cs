using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Core.Tests.Fakes;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="ConfigService"/> 的单元测试：加载、损坏恢复与原子写（FR-4.8 / FR-12.3）。
/// </summary>
public sealed class ConfigServiceTests : IDisposable
{
    private const string CorruptBackupSearchPattern = "config.json.corrupt-*";

    private readonly TempDirectory _temp = new();
    private readonly FakeLogSink _log = new();
    private readonly FakeClock _clock = new();
    private readonly PathService _paths;
    private readonly ConfigService _service;

    /// <summary>搭好一套隔离在临时目录里的配置服务。</summary>
    public ConfigServiceTests()
    {
        _paths = new PathService(_temp.Combine("local"), _temp.Combine("config"), _temp.Path);
        _service = new ConfigService(_paths, _log, _clock);
    }

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Load_ConfigFileMissing_ReturnsDefaultConfig()
    {
        // 首次运行的正常路径：不该报错，也不该记 Error
        var config = _service.Load();

        Assert.Equal(AppConfig.CurrentVersion, config.Version);
        Assert.Empty(config.Items);
        Assert.Equal(NotifyMode.FailuresOnly, config.Settings.NotifyMode);
        Assert.Equal(0, _log.Count);
    }

    [Fact]
    public void Load_BlankFile_ReturnsDefaultConfig()
    {
        WriteConfigFile("   ");

        var config = _service.Load();

        Assert.Empty(config.Items);
        Assert.Equal(AppConfig.CurrentVersion, config.Version);
    }

    [Fact]
    public void Load_JsonArrayRoot_IsTreatedAsCorrupt()
    {
        // 根节点不是对象 → 与其他损坏形态同等对待：留副本 + 抛异常，绝不降级成空配置。
        WriteConfigFile("[1, 2, 3]");

        var exception = Assert.Throws<StartupOperationException>(() => _service.Load());

        Assert.Equal(StartupFailureReason.ConfigCorrupted, exception.Reason);
        Assert.Single(Directory.GetFiles(_paths.ConfigRoot, CorruptBackupSearchPattern));
    }

    [Fact]
    public void Load_ConfigFromNewerVersion_Throws()
    {
        // 版本比自己新 → 拒绝加载。硬解析未知结构会把配置写坏，而配置写坏就没救了。
        WriteConfigFile("""{ "version": 99, "items": [] }""");

        var exception = Assert.Throws<StartupOperationException>(() => _service.Load());

        Assert.Equal(StartupFailureReason.ConfigVersionUnsupported, exception.Reason);
    }

    [Fact]
    public void Load_ConfigWithoutVersionField_IsTreatedAsCorrupt()
    {
        // 🔴 D121：缺 `version` 字段不再"视为当前版本"。那等于替一份来路不明的文件背书 ——
        // 而本项目没有迁移逻辑，真正该做的是明确拒绝，让人删掉重建。
        // AppConfig.Version 的默认值是 0，所以缺字段会落到"低于当前版本"这一支。
        WriteConfigFile("""{ "items": [], "settings": {} }""");

        var exception = Assert.Throws<StartupOperationException>(() => _service.Load());

        Assert.Equal(StartupFailureReason.ConfigCorrupted, exception.Reason);
    }

    [Fact]
    public void Load_ConfigFromOlderVersion_IsTreatedAsCorrupt()
    {
        // 低于当前版本 = 不认识的格式。硬解析出来的多半是字段名对不上的半截数据，
        // 写回去就是毁掉用户配置 —— 没有迁移逻辑就不该"尽力解析"。
        WriteConfigFile("""{ "version": 1, "Items": [ { "Id": "legacy", "Name": "旧条目" } ] }""");

        var exception = Assert.Throws<StartupOperationException>(() => _service.Load());

        Assert.Equal(StartupFailureReason.ConfigCorrupted, exception.Reason);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyConfig()
    {
        // 文件不存在 = 合法的空配置，允许写入
        var config = _service.Load();

        Assert.Equal(AppConfig.CurrentVersion, config.Version);
        Assert.Empty(config.Items);
    }

    [Fact]
    public void Load_CorruptJson_ThrowsAndKeepsOriginal()
    {
        // E10 / FR-12.3：配置损坏时**抛异常**，绝不降级成空配置。
        // 降级意味着程序在"自己什么都不知道"的状态下继续工作：守卫安静地什么都不纠正、
        // 界面把接管项全显示成未接管、任何写操作都会覆盖掉仅存的副本。静默的破坏比明确的失败危险得多。
        // 抛之前先留带时间戳的副本 —— 里面存着"哪些系统自启动项被接管了"，那是唯一的还原依据。
        WriteConfigFile("{ this is definitely not json ");

        var exception = Assert.Throws<StartupOperationException>(() => _service.Load());

        Assert.Equal(StartupFailureReason.ConfigCorrupted, exception.Reason);
        Assert.True(File.Exists(_service.ConfigFilePath), "原文件必须保留，不能被覆盖");

        var backups = Directory.GetFiles(_paths.ConfigRoot, CorruptBackupSearchPattern);
        Assert.Single(backups);
        Assert.Contains("20260919-084112", backups[0], StringComparison.Ordinal);
        Assert.True(_log.Contains(LogLevel.Error, "解析失败"));
    }

    [Fact]
    public void Load_ValidConfig_ReturnsNormalizedSnapshot()
    {
        WriteConfigFile("""{ "version": 2, "items": [], "settings": { "retryCount": -5 } }""");

        var config = _service.Load();

        Assert.Equal(AppConfig.CurrentVersion, config.Version);
        Assert.Equal(0, config.Settings.RetryCount);
    }

    [Fact]
    public void Load_RetryCountAboveCeiling_IsClamped()
    {
        // 🔴 B7：原先只夹下界。手改成 1000000 就是"失败一百万次"——每个失败都要走一遍
        // 降权链 / 计划任务调用，一轮下来能把登录后几分钟全占死，而用户只看到"登录后卡很久"。
        WriteConfigFile("""{ "version": 2, "items": [], "settings": { "retryCount": 1000000 } }""");

        var config = _service.Load();

        Assert.Equal(ConfigService.MaximumRetryCount, config.Settings.RetryCount);
    }

    [Fact]
    public void Load_RetryCountWithinRange_IsPreserved()
    {
        // 区间内的值不能被夹：夹了就成了"设置页改了没反应"。
        WriteConfigFile("""{ "version": 2, "items": [], "settings": { "retryCount": 3 } }""");

        var config = _service.Load();

        Assert.Equal(3, config.Settings.RetryCount);
    }

    [Fact]
    public void RetryCountBounds_MatchSettingsPageNumberBox()
    {
        // 🔴 上界必须与设置页 NumberBox 的 Maximum 一致（见 SettingsPage.xaml 的注释）：
        // 两处各写一个数就会出现"界面显示 5、配置实际是 8"的错位。
        // x:Bind 拿不到 C# 常量，所以这条断言就是那道人工同步的护栏 —— 改了一边必须改另一边。
        const int numberBoxMaximum = 5;
        const int numberBoxMinimum = 0;

        Assert.Equal(numberBoxMaximum, ConfigService.MaximumRetryCount);
        Assert.Equal(numberBoxMinimum, ConfigService.MinimumRetryCount);
    }

    [Theory]
    [InlineData(99, NotifyMode.FailuresOnly)]
    [InlineData(-1, NotifyMode.FailuresOnly)]
    [InlineData(0, NotifyMode.FailuresOnly)]
    [InlineData(1, NotifyMode.Always)]
    [InlineData(2, NotifyMode.Never)]
    public void Load_UnknownNotifyMode_FallsBackToDefault(int raw, NotifyMode expected)
    {
        // 枚举强转不抛异常：手改配置写个 7 进来不会报错，只会让收尾判定走到一个
        // 既不是"总是"也不是"从不"的分支，行为变得不可预期。
        WriteConfigFile($$"""{ "version": 2, "items": [], "settings": { "notifyMode": {{raw}} } }""");

        var config = _service.Load();

        Assert.Equal(expected, config.Settings.NotifyMode);
    }

    [Theory]
    [InlineData(99, GuardNotifyMode.OnChange)]
    [InlineData(-1, GuardNotifyMode.OnChange)]
    [InlineData(0, GuardNotifyMode.OnChange)]
    [InlineData(1, GuardNotifyMode.Never)]
    public void Load_UnknownGuardNotifyMode_FallsBackToDefault(int raw, GuardNotifyMode expected)
    {
        WriteConfigFile($$"""{ "version": 2, "items": [], "settings": { "guardNotifyMode": {{raw}} } }""");

        var config = _service.Load();

        Assert.Equal(expected, config.Settings.GuardNotifyMode);
    }

    [Fact]
    public void Save_ConfigPathOccupiedByDirectory_ThrowsStartupOperationException()
    {
        // 4.7：保存失败必须包装成 StartupOperationException（I/O、权限、文件系统能力），
        // 让 UI 有统一的 catch 形状，而不是把裸 IOException 撒到事件处理器里。
        Directory.CreateDirectory(_paths.ConfigFilePath);

        var exception = Assert.Throws<StartupOperationException>(() => _service.Save(new AppConfig()));

        Assert.Equal(StartupFailureReason.Unknown, exception.Reason);
    }

    [Fact]
    public void Load_ExplicitNullCollections_AreRebuilt()
    {
        // 源生成反序列化遇到显式 null 会赋 null，绕过属性默认值 —— 必须兜住
        WriteConfigFile("""{ "version": 2, "items": null, "settings": null }""");

        var config = _service.Load();

        Assert.NotNull(config.Items);
        Assert.NotNull(config.Settings);
        Assert.Empty(config.Items);
    }

    [Fact]
    public void Load_NegativeRetryCount_IsClampedToZero()
    {
        // 2026-09-19 批复后设置只剩 retryCount 一个数值项需要收敛；
        // maxDelay / trayKeep 等字段已随设置精简移除。
        WriteConfigFile("""{ "version": 2, "items": [], "settings": { "retryCount": -5 } }""");

        var config = _service.Load();

        Assert.Equal(0, config.Settings.RetryCount);
    }

    [Fact]
    public void Load_PresetsWithDuplicatesAndNegatives_AreNormalized()
    {
        // FR-4.2：预设值按升序去重（设置页里是逗号分隔的自由输入，用户什么都能打）
        WriteConfigFile(
            """{ "version": 2, "items": [], "settings": { "delayPresets": [60, 10, 10, -5, 30] } }""");

        var config = _service.Load();

        int[] expected = [10, 30, 60];
        Assert.Equal(expected, config.Settings.DelayPresets);
    }

    [Fact]
    public void Load_EmptyPresets_FallBackToDefaults()
    {
        WriteConfigFile("""{ "version": 2, "items": [], "settings": { "delayPresets": [] } }""");

        var config = _service.Load();

        int[] expected = [0, 5, 10, 15, 20, 30, 60];
        Assert.Equal(expected, config.Settings.DelayPresets);
    }

    [Fact]
    public void Load_DefaultPresetNotInList_FallsBackToFirstPreset()
    {
        // 默认预设必须是列表成员：手改配置删掉它时兜底到第一项
        WriteConfigFile(
            """{ "version": 2, "items": [], "settings": { "delayPresets": [30, 60], "defaultPreset": 90 } }""");

        var config = _service.Load();

        Assert.Equal(30, config.Settings.DefaultPreset);
    }

    [Fact]
    public void Save_UsesCamelCaseKeysAndStringEnumValues()
    {
        // 配置文件是给人看的，也是跨版本演进的格式：camelCase + 字符串枚举
        var config = new AppConfig();
        config.Items.Add(new DelayedItem
        {
            Id = "registry:hkcu:weixin",
            Name = "微信",
            Path = "C:\\a.exe",
            DelaySeconds = 30,
            Source = StartupSource.Registry,
            Scope = StartupScope.Hkcu,
        });
        config.Settings.NotifyMode = NotifyMode.Always;

        _service.Save(config);

        var json = File.ReadAllText(_paths.ConfigFilePath);
        Assert.Contains("\"delaySeconds\": 30", json, StringComparison.Ordinal);
        Assert.Contains("\"scope\": \"Hkcu\"", json, StringComparison.Ordinal);
        Assert.Contains("\"notifyMode\": \"Always\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_LeavesNoTemporaryFileBehind()
    {
        // 机制 8：写 .tmp → File.Replace，正常路径下不该留下临时文件
        _service.Save(new AppConfig());

        Assert.Empty(Directory.GetFiles(_paths.ConfigRoot, "*.tmp"));
    }

    [Fact]
    public void Save_IsIdempotent()
    {
        // 覆盖已存在的文件（走 File.Replace 分支）也必须成功
        var config = new AppConfig();

        _service.Save(config);
        _service.Save(config);

        Assert.Single(Directory.GetFiles(_paths.ConfigRoot, "config.json"));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        // Arrange：每个字段都塞一个非默认值，防止某个字段漏了序列化
        var config = new AppConfig();
        config.Items.Add(new DelayedItem
        {
            Id = "registry:hkcu:weixin",
            Name = "微信",
            Path = "C:\\Program Files\\WeChat\\WeChat.exe",
            Arguments = "-silent",
            DelaySeconds = 45,
            SortOrder = 2,
            RunAsAdmin = true,
            Enabled = false,
            Source = StartupSource.Registry,
            Scope = StartupScope.Hkcu,
            SourceKey = "Weixin",
            SourceDetail = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
            OriginalState = new OriginalState { WasEnabled = true, Extra = "Highest" },
        });
        config.Settings.NotifyMode = NotifyMode.Never;
        config.Settings.RetryCount = 2;
        config.Settings.DelayPresets = [0, 15, 45];
        config.Settings.DefaultPreset = 15;
        config.Settings.Theme = ThemePreference.Dark;
        config.Settings.LastRunId = "20260919-084112";

        // Act
        _service.Save(config);
        var loaded = _service.Load();

        // Assert
        Assert.Single(loaded.Items);
        var item = loaded.Items[0];
        Assert.Equal("registry:hkcu:weixin", item.Id);
        Assert.Equal("微信", item.Name);
        Assert.Equal("C:\\Program Files\\WeChat\\WeChat.exe", item.Path);
        Assert.Equal("-silent", item.Arguments);
        Assert.Equal(45, item.DelaySeconds);
        Assert.Equal(2, item.SortOrder);
        Assert.True(item.RunAsAdmin);
        Assert.False(item.Enabled);
        Assert.Equal(StartupSource.Registry, item.Source);
        Assert.Equal(StartupScope.Hkcu, item.Scope);
        Assert.Equal("Weixin", item.SourceKey);
        Assert.True(item.OriginalState.WasEnabled);
        Assert.Equal("Highest", item.OriginalState.Extra);

        Assert.Equal(NotifyMode.Never, loaded.Settings.NotifyMode);
        Assert.Equal(2, loaded.Settings.RetryCount);

        int[] expectedPresets = [0, 15, 45];
        Assert.Equal(expectedPresets, loaded.Settings.DelayPresets);
        Assert.Equal(15, loaded.Settings.DefaultPreset);
        Assert.Equal(ThemePreference.Dark, loaded.Settings.Theme);
        Assert.Equal("20260919-084112", loaded.Settings.LastRunId);
    }

    [Fact]
    public void Load_ManualItem_KeepsManualScopeNone()
    {
        // 手动条目在配置里往返后必须仍是"手动"，否则移除时会去找一个不存在的系统项
        var config = new AppConfig();
        config.Items.Add(new DelayedItem
        {
            Id = "manual:none:abc",
            Name = "记事本",
            Path = "C:\\Windows\\notepad.exe",
            Source = StartupSource.Manual,
            Scope = StartupScope.None,
        });
        _service.Save(config);

        var loaded = _service.Load();

        Assert.True(loaded.Items[0].IsManual);
    }

    [Fact]
    public void Save_NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _service.Save(null!));
    }

    [Fact]
    public void Constructor_NullDependencies_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new ConfigService(null!, _log, _clock));
        Assert.Throws<ArgumentNullException>(() => new ConfigService(_paths, null!, _clock));
        Assert.Throws<ArgumentNullException>(() => new ConfigService(_paths, _log, null!));
    }

    private void WriteConfigFile(string json)
    {
        Directory.CreateDirectory(_paths.ConfigRoot);
        File.WriteAllText(_service.ConfigFilePath, json);
    }
}
