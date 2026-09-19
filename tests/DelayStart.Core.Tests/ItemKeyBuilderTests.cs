using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="ItemKeyBuilder"/> 的单元测试：稳定主键的生成与规范化（机制 1 / FR-1.6）。
/// </summary>
public sealed class ItemKeyBuilderTests
{
    [Fact]
    public void Build_RegistryHkcu_ProducesThreeSegmentKey()
    {
        // Arrange / Act
        var key = ItemKeyBuilder.Build(StartupSource.Registry, StartupScope.Hkcu, "Weixin");

        // Assert：格式与 docs/architecture.md §4.5 的示例一致
        Assert.Equal("registry:hkcu:weixin", key);
    }

    [Fact]
    public void Build_SourceKeyWithSurroundingWhitespace_NormalizesIt()
    {
        // Arrange / Act
        var key = ItemKeyBuilder.Build(StartupSource.Registry, StartupScope.Hklm, "  Weixin  ");

        // Assert
        Assert.Equal("registry:hklm:weixin", key);
    }

    [Fact]
    public void Build_SourceKeyWithMixedCase_ProducesLowercase()
    {
        // 注册表值名的大小写不敏感，主键也必须不敏感，否则同一项会被当成两项
        var key = ItemKeyBuilder.Build(StartupSource.StartupFolder, StartupScope.UserFolder, "MY-TOOL.LNK");

        Assert.Equal("startupfolder:userfolder:my-tool.lnk", key);
    }

    [Theory]
    [InlineData(StartupScope.Hkcu, "hkcu")]
    [InlineData(StartupScope.Hklm, "hklm")]
    [InlineData(StartupScope.HklmWow, "hklmwow")]
    public void Build_SameNameInDifferentScopes_ProducesDifferentKeys(StartupScope scope, string expectedScopeToken)
    {
        // 坑 6：同名不同位置必须区分开。用 (Name, Source) 二元组会把它们混成一个。
        var key = ItemKeyBuilder.Build(StartupSource.Registry, scope, "Weixin");

        Assert.Equal($"registry:{expectedScopeToken}:weixin", key);
    }

    [Fact]
    public void Build_SameNameInDifferentSources_ProducesDifferentKeys()
    {
        // 同一个名字同时出现在注册表和启动文件夹里，是两项而不是一项
        var registryKey = ItemKeyBuilder.Build(StartupSource.Registry, StartupScope.Hkcu, "Weixin");
        var folderKey = ItemKeyBuilder.Build(StartupSource.StartupFolder, StartupScope.UserFolder, "Weixin");

        Assert.NotEqual(registryKey, folderKey);
    }

    [Fact]
    public void Build_EmptySourceKey_StillProducesWellFormedKey()
    {
        // 不抛异常：扫描时偶尔会遇到空值名，让上层按"读到一项但无法标识"处理
        var key = ItemKeyBuilder.Build(StartupSource.Registry, StartupScope.Hkcu, string.Empty);

        Assert.Equal("registry:hkcu:", key);
    }

    [Fact]
    public void Build_NullSourceKey_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ItemKeyBuilder.Build(StartupSource.Registry, StartupScope.Hkcu, null!));
    }

    [Fact]
    public void ForManual_ProducesManualNonePrefix()
    {
        // 手动条目没有系统来源可锚定，但仍保持与其他来源一致的三段形状
        var key = ItemKeyBuilder.ForManual();

        Assert.StartsWith("manual:none:", key);
    }

    [Fact]
    public void ForManual_TwoCallsWithSameGuid_ProduceSameKey()
    {
        // Arrange
        var id = Guid.NewGuid();

        // Act
        var first = ItemKeyBuilder.ForManual(id);
        var second = ItemKeyBuilder.ForManual(id);

        // Assert
        Assert.Equal(first, second);
    }

    [Fact]
    public void ForManual_TwoCalls_ProduceDifferentKeys()
    {
        // 手动添加同一个 exe 两次应当得到两个独立条目（用户可能就是要启动两次）
        var first = ItemKeyBuilder.ForManual();
        var second = ItemKeyBuilder.ForManual();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ForManual_KeyIsLowercase()
    {
        var key = ItemKeyBuilder.ForManual(Guid.Parse("AABBCCDD-1122-3344-5566-77889900AABB"));

        Assert.Equal("manual:none:aabbccdd11223344556677889900aabb", key);
    }

    [Theory]
    [InlineData(StartupSource.Registry, "registry")]
    [InlineData(StartupSource.StartupFolder, "startupfolder")]
    [InlineData(StartupSource.ScheduledTask, "scheduledtask")]
    [InlineData(StartupSource.Uwp, "uwp")]
    [InlineData(StartupSource.Manual, "manual")]
    public void SourceToken_AllValues_MatchFrozenTokens(StartupSource source, string expected)
    {
        // 🔴 回归闸门：这些令牌已经写进用户的 config.json。
        // 改动它们会让所有既有条目匹配不上，等于把所有接管项"弄丢"（系统项保持禁用但没人管）。
        Assert.Equal(expected, ItemKeyBuilder.SourceToken(source));
    }

    [Theory]
    [InlineData(StartupScope.None, "none")]
    [InlineData(StartupScope.Hkcu, "hkcu")]
    [InlineData(StartupScope.Hklm, "hklm")]
    [InlineData(StartupScope.HklmWow, "hklmwow")]
    [InlineData(StartupScope.UserFolder, "userfolder")]
    [InlineData(StartupScope.SystemFolder, "systemfolder")]
    public void ScopeToken_AllValues_MatchFrozenTokens(StartupScope scope, string expected)
    {
        // 🔴 同上：令牌是持久化契约，不是内部实现细节。
        Assert.Equal(expected, ItemKeyBuilder.ScopeToken(scope));
    }
}
