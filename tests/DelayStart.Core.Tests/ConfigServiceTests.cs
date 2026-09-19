using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Core.Tests.Fakes;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="ConfigService"/> 的单元测试：加载、损坏恢复、v1→v2 迁移与原子写（FR-4.8 / FR-12）。
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
        // 根节点不是对象 → 备份 + 重建，不能硬解析下去
        WriteConfigFile("[1, 2, 3]");

        var config = _service.Load();

        Assert.Empty(config.Items);
        Assert.Single(Directory.GetFiles(_paths.ConfigRoot, CorruptBackupSearchPattern));
    }

    [Fact]
    public void Load_CorruptJson_PreservesCopyThenRebuilds()
    {
        // E10 / FR-12.3：配置损坏必须**先留副本**再重建。
        // 直接覆盖是灾难 —— 副本里存着"哪些系统自启动项被接管了"，丢了就再也还原不回去。
        WriteConfigFile("{ this is definitely not json ");

        var config = _service.Load();

        Assert.Equal(AppConfig.CurrentVersion, config.Version);
        Assert.Empty(config.Items);

        var backups = Directory.GetFiles(_paths.ConfigRoot, CorruptBackupSearchPattern);
        Assert.Single(backups);
        Assert.Contains("20260919-084112", backups[0], StringComparison.Ordinal);
        Assert.True(File.Exists(backups[0]));
        Assert.True(_log.Contains(LogLevel.Error, "解析失败"));
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
    public void Load_LegacyV1_MigratesScopeEnabledAndOriginalState()
    {
        // FR-12.1：v1（demo 格式）→ v2
        WriteConfigFile(LegacyV1Json);

        var config = _service.Load();

        // Assert：版本升上来了，条目都在
        Assert.Equal(AppConfig.CurrentVersion, config.Version);
        Assert.Equal(2, config.Items.Count);

        // 注册表项：补 scope、改名 arguments / sourceKey、补 enabled 与 originalState
        var registry = config.Items[0];
        Assert.Equal("legacy-hkcu", registry.Id);
        Assert.Equal(StartupSource.Registry, registry.Source);
        Assert.Equal(StartupScope.Hkcu, registry.Scope);
        Assert.Equal("-silent", registry.Arguments);
        Assert.Equal("Weixin", registry.SourceKey);
        Assert.Equal(30, registry.DelaySeconds);
        Assert.True(registry.Enabled);
        // v1 条目接管前是正常自启动的 → 迁移后必须记成"原本启用"，
        // 否则「移出延时启动」会把它永久留在禁用状态（见 ConfigService 迁移方法的 remarks）。
        Assert.True(registry.OriginalState.WasEnabled);

        // 启动文件夹项：scope 推断为系统启动文件夹
        var folder = config.Items[1];
        Assert.Equal(StartupSource.StartupFolder, folder.Source);
        Assert.Equal(StartupScope.SystemFolder, folder.Scope);
        Assert.Equal("sync.lnk", folder.SourceKey);
        Assert.True(folder.RunAsAdmin);
    }

    [Fact]
    public void Load_LegacyV1_MigrationIsRecordedInLog()
    {
        WriteConfigFile(LegacyV1Json);

        _ = _service.Load();

        Assert.True(_log.Contains(LogLevel.Info, "迁移"));
    }

    [Theory]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", StartupScope.Hkcu)]
    [InlineData(@"HKLM\Software\Microsoft\Windows\CurrentVersion\Run", StartupScope.Hklm)]
    [InlineData(@"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", StartupScope.HklmWow)]
    [InlineData("某个不认识的位置", StartupScope.None)]
    public void Load_LegacyV1RegistryDetail_InfersScope(string sourceDetail, StartupScope expectedScope)
    {
        // 迁移期的一次性推断：v1 没存 scope，只能从位置描述里读出来并用枚举固化
        WriteConfigFile(BuildLegacyJson("registry", EscapeForJson(sourceDetail)));

        var config = _service.Load();

        Assert.Single(config.Items);
        Assert.Equal(expectedScope, config.Items[0].Scope);
    }

    [Theory]
    [InlineData("用户启动文件夹", StartupScope.UserFolder)]
    [InlineData("系统启动文件夹", StartupScope.SystemFolder)]
    public void Load_LegacyV1StartupFolderDetail_InfersScope(string sourceDetail, StartupScope expectedScope)
    {
        WriteConfigFile(BuildLegacyJson("startup_folder", sourceDetail));

        var config = _service.Load();

        Assert.Single(config.Items);
        Assert.Equal(expectedScope, config.Items[0].Scope);
    }

    [Theory]
    [InlineData("scheduled_task", StartupSource.ScheduledTask)]
    [InlineData("uwp", StartupSource.Uwp)]
    [InlineData("完全不认识的来源", StartupSource.Manual)]
    public void Load_LegacyV1Source_IsMappedToEnum(string source, StartupSource expected)
    {
        WriteConfigFile(BuildLegacyJson(source, "任意位置"));

        var config = _service.Load();

        Assert.Single(config.Items);
        Assert.Equal(expected, config.Items[0].Source);
        Assert.Equal(StartupScope.None, config.Items[0].Scope);
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
    public void Load_NegativeSettingsValues_AreClampedToZero()
    {
        WriteConfigFile(
            """{ "version": 2, "items": [], "settings": { "maxDelaySeconds": -1, "retryCount": -5, "trayKeepSeconds": -3 } }""");

        var config = _service.Load();

        Assert.Equal(0, config.Settings.MaxDelaySeconds);
        Assert.Equal(0, config.Settings.RetryCount);
        Assert.Equal(0, config.Settings.TrayKeepSeconds);
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

        int[] expected = [0, 10, 30, 60, 120];
        Assert.Equal(expected, config.Settings.DelayPresets);
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
        config.Settings.TrayKeepSeconds = 10;
        config.Settings.ShowTrayIcon = false;
        config.Settings.PreferDeElevatedLaunch = false;
        config.Settings.FallbackOnDeElevationFailure = false;
        config.Settings.MaxDelaySeconds = 3600;
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
        Assert.Equal(10, loaded.Settings.TrayKeepSeconds);
        Assert.False(loaded.Settings.ShowTrayIcon);
        Assert.False(loaded.Settings.PreferDeElevatedLaunch);
        Assert.False(loaded.Settings.FallbackOnDeElevationFailure);
        Assert.Equal(3600, loaded.Settings.MaxDelaySeconds);
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

    private const string LegacyV1Json = """
    {
      "Version": 1,
      "Items": [
        {
          "Id": "legacy-hkcu",
          "Name": "微信",
          "Path": "C:\\Program Files\\WeChat\\WeChat.exe",
          "Args": "-silent",
          "DelaySeconds": 30,
          "RunAsAdmin": false,
          "SortOrder": 1,
          "Source": "registry",
          "SourceDetail": "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run",
          "SourceKeyName": "Weixin"
        },
        {
          "Id": "legacy-folder",
          "Name": "同步盘",
          "Path": "C:\\App\\sync.exe",
          "Args": "",
          "DelaySeconds": 60,
          "RunAsAdmin": true,
          "SortOrder": 2,
          "Source": "startup_folder",
          "SourceDetail": "系统启动文件夹",
          "SourceKeyName": "sync.lnk"
        }
      ]
    }
    """;

    private static string BuildLegacyJson(string source, string escapedSourceDetail) => $$"""
    {
      "Version": 1,
      "Items": [
        {
          "Id": "legacy-item",
          "Name": "测试项",
          "Path": "C:\\App\\a.exe",
          "Args": "",
          "DelaySeconds": 10,
          "RunAsAdmin": false,
          "SortOrder": 0,
          "Source": "{{source}}",
          "SourceDetail": "{{escapedSourceDetail}}",
          "SourceKeyName": "test"
        }
      ]
    }
    """;

    private static string EscapeForJson(string value)
        => value.Replace(@"\", @"\\", StringComparison.Ordinal);

    private void WriteConfigFile(string json)
    {
        Directory.CreateDirectory(_paths.ConfigRoot);
        File.WriteAllText(_service.ConfigFilePath, json);
    }
}
