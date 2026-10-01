using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="TargetPrefilter"/> 的单元测试（S1.1：调度端目标存活预筛）。
/// </summary>
/// <remarks>
/// <para>
/// 这一段是纯逻辑，且判错方向的代价很大：误判"目标没了"会让用户以为自己的配置失效，
/// 进而把好好的条目删掉。所以四条分支（两堆各自为空 / 各自非空）都要钉住。
/// </para>
/// <para>
/// 🔴 全部用**注入的判据**，不碰真实文件系统（硬约束 10）。只有最后一组专门验默认判据
/// 的"更宽松"方向（D87 / D90：兜底只能更宽松，绝不更严格），那一组必须用真实路径。
/// </para>
/// </remarks>
public sealed class TargetPrefilterTests
{
    [Fact]
    public void Split_EmptyPlan_ReturnsTwoEmptyLists()
    {
        var result = TargetPrefilter.Split([], _ => false);

        Assert.Empty(result.Launchable);
        Assert.Empty(result.MissingTargets);
    }

    [Fact]
    public void Split_AllPresent_AllLaunchableAndNoMissing()
    {
        var plan = Plan("a", "b", "c");

        var result = TargetPrefilter.Split(plan, _ => false);

        Assert.Equal(["a", "b", "c"], Ids(result.Launchable));
        Assert.Empty(result.MissingTargets);
    }

    [Fact]
    public void Split_AllMissing_NothingLaunchableAndAllMissing()
    {
        var plan = Plan("a", "b");

        var result = TargetPrefilter.Split(plan, _ => true);

        Assert.Empty(result.Launchable);
        Assert.Equal(["a", "b"], Ids(result.MissingTargets));
    }

    [Fact]
    public void Split_Mixed_PartitionsWithoutLosingOrReordering()
    {
        // 判据按**目标路径的文件名**走（Plan 把 id 拼进了路径）：a、c 判"在"，b、d 判"不在"。
        var plan = Plan("a", "b", "c", "d");

        var result = TargetPrefilter.Split(plan, IsMissingNamed("b", "d"));

        Assert.Equal(["a", "c"], Ids(result.Launchable));
        Assert.Equal(["b", "d"], Ids(result.MissingTargets));
    }

    [Fact]
    public void Split_DuplicateNames_KeepsBothSidesStable()
    {
        // 同一个条目出现两次（防御外部注入的畸形计划）：不能因为名字相同就少算一个。
        var plan = Plan("dup", "other", "dup");

        var result = TargetPrefilter.Split(plan, IsMissingNamed("dup"));

        Assert.Equal(["other"], Ids(result.Launchable));
        Assert.Equal(["dup", "dup"], Ids(result.MissingTargets));
    }

    [Fact]
    public void Split_NullPlan_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TargetPrefilter.Split(null!, _ => false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Split_BlankPath_StaysLaunchableByDefault(string? path)
    {
        // 🔴 空路径 = "不知道指向哪儿"，**不是**"目标没了"。
        //   误判"已不存在"会造成大量误报，而那些条目本来跑得好好的。
        var entry = new ScheduleEntry(new DelayedItem { Id = "x", Name = "x", Path = path! }, TimeSpan.Zero);

        var result = TargetPrefilter.Split([entry]);

        Assert.Single(result.Launchable);
        Assert.Empty(result.MissingTargets);
    }

    [Theory]
    [InlineData("chrome.exe")]
    [InlineData(@"relative\tool.exe")]
    [InlineData("OneDrive")]
    [InlineData("SecurityHealth")]
    public void Split_NotFullyQualifiedPath_StaysLaunchableByDefault(string path)
    {
        // 🔴 裸命令名由 PATH 解析，UWP 由包名解析 —— 对它们查文件系统必然判"不存在"，
        //   直接照单全收会造成大规模误杀。这是 D87 / D90 "兜底只能更宽松"的直接体现。
        var entry = new ScheduleEntry(new DelayedItem { Id = "x", Name = "x", Path = path }, TimeSpan.Zero);

        var result = TargetPrefilter.Split([entry]);

        Assert.Single(result.Launchable);
        Assert.Empty(result.MissingTargets);
    }

    [Fact]
    public void Split_ByDefault_FullyQualifiedMissingPathIsDetected()
    {
        // 反向：默认判据**该**发现的还是要发现，否则预筛形同虚设。
        var missing = Path.Combine(Path.GetTempPath(), "delaystart-prefilter-not-exist-" + Guid.NewGuid().ToString("N") + ".exe");
        var entry = new ScheduleEntry(new DelayedItem { Id = "x", Name = "x", Path = missing }, TimeSpan.Zero);

        var result = TargetPrefilter.Split([entry]);

        Assert.Empty(result.Launchable);
        Assert.Single(result.MissingTargets);
    }

    /// <summary>
    /// 造一个"按目标文件名判断在不在"的判据。🔴 判据吃的是 <c>Path</c> 而不是 <c>Name</c> ——
    /// 真实判据 <see cref="TargetFileProbe.IsMissing"/> 看的也是路径，拿名字去断言会得到
    /// 一个"全都判在"的假绿用例。
    /// </summary>
    private static Func<string?, bool> IsMissingNamed(params string[] missingNames)
    {
        var set = new HashSet<string>(missingNames, StringComparer.OrdinalIgnoreCase);
        return path => path is not null
            && set.Contains(System.IO.Path.GetFileNameWithoutExtension(path));
    }

    private static List<ScheduleEntry> Plan(params string[] ids)
        => [.. ids.Select(id => new ScheduleEntry(
            new DelayedItem { Id = id, Name = id, Path = $@"C:\{id}.exe" },
            TimeSpan.Zero))];

    private static string[] Ids(IReadOnlyList<ScheduleEntry> entries)
        => [.. entries.Select(static entry => entry.Item.Id)];
}
