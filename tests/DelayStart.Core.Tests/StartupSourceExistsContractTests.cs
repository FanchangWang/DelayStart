using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Core.Tests.Fakes;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Services;
using DelayStart.Management.Sources;

namespace DelayStart.Core.Tests;

/// <summary>
/// <c>IStartupSource.Exists</c> 的<strong>契约</strong>测试（G1 / D127）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 这四个实现是全仓库唯一一处"判源在不在"的地方，而它判错方向的代价<strong>不对称</strong>：
/// </para>
/// <list type="bullet">
/// <item><description>答 <c>true</c>（当成还在）⇒ 最坏是多做一次无副作用的空操作；</description></item>
/// <item><description>答 <c>false</c> ⇒ <b>永久丢失还原依据</b> ——
/// <c>TakeoverService.Release</c> 会跳过系统恢复并删掉配置记录，
/// 而那是日后把用户自启动项变回原样的唯一东西。</description></item>
/// </list>
/// <para>
/// 所以契约必须被钉住，而不只是"实现大概对"。
/// </para>
/// <para>
/// 🔴 本类钉的是<strong>契约</strong>，不是探测结果：四个实现各自的探测细节要真的
/// 注册表键 / 真的计划任务 / 真的 UWP 包，按硬约束 10 不能在单测里造。
/// 于是分成两类断言：① 能在进程内验的（参数校验、异常不被吞、只读打开）直接调；
/// ② 只能验"实现形状"的（用了精确匹配还是候选名回退）读源码文本断言 ——
/// 那一条略显别扭，但比"注释说它精确所以它精确"可靠得多：
/// 注释与实现对不上时，红的是这个测试。
/// </para>
/// </remarks>
public sealed class StartupSourceExistsContractTests
{
    [Fact]
    public void Exists_NullEntry_ThrowsOnEverySource()
    {
        // 🔴 传 null 抛 ArgumentNullException 而不是"答 true"：那是**调用方的编码错误**，
        //   不是"看不真切"。把它当成"还在"会把错误静默吞掉，而那种错在真机上
        //   表现为"某个来源的条目永远不判失效"—— 没有任何提示。
        var log = new FakeLogSink();
        var clock = new FakeClock();
        var resolver = new ForbiddenResolver();

        Assert.Throws<ArgumentNullException>(
            () => new RegistryStartupSource(StartupScope.Hkcu, clock, log).Exists(null!));

        Assert.Throws<ArgumentNullException>(
            () => new StartupFolderSource(StartupScope.UserFolder, resolver, clock, log).Exists(null!));

        Assert.Throws<ArgumentNullException>(
            () => new ScheduledTaskSource(log).Exists(null!));

        Assert.Throws<ArgumentNullException>(
            () => new UwpStartupSource(log).Exists(null!));
    }

    [Theory]
    [InlineData("RegistryStartupSource")]
    [InlineData("StartupFolderSource")]
    [InlineData("ScheduledTaskSource")]
    [InlineData("UwpStartupSource")]
    public void Exists_DoesNotSwallowExceptions(string sourceName)
    {
        // 🔴 关键契约：**不 catch**。`Exists` 抛异常 ⇒ `TakeoverService.Release` 保留配置 +
        //   返回失败（G2.2）；而答 `false` ⇒ 跳过系统恢复 + 删配置（G2.1）。
        //   两条路的代价完全不对等，所以"打不开"必须走异常那条。
        //   四个实现都让异常自然往上抛。
        //
        // ⚠️ 只看 **Exists 方法体**：这些类里的别的方法（收集 / 建条目 / 写状态）
        //     legitimately 要 catch —— 按全文断言会把正确的代码判成错的。
        var body = ReadMethodBody(sourceName, "Exists");

        // 🔴 匹配的是**真正的 catch 子句**，而不是"catch"这个词 ——
  // 否则方法体里任何一句提到 catch 的注释都会让这条变红，
        // 而那是一次无害的注释改动。剥注释是另一半：不然"没 catch"这条
  // 会被一句提到它的注释变成红的。
        Assert.DoesNotMatch(@"\bcatch\s*[\(\{]", body);
    }

    [Theory]
    [InlineData("RegistryStartupSource")]
    [InlineData("UwpStartupSource")]
    public void Exists_OpensSubKeysReadOnly(string sourceName)
    {
        // 🔴 探测存在性**绝不能用写模式**：`OpenSubKey(path, writable: true)` 会真的
        //   改键的 ACL 与最后写入时间，权限不足时还会抛 UnauthorizedAccessException ——
        //   于是"读一下在不在"变成了"改一下系统"，即**读操作产生了系统写入**
        //   （违反硬约束 7）。
        // 用正则容忍 `writable:false` 与 `writable: false` 两种写法 ——
    // 同一份代码，两种拼写，行为完全一样。
        Assert.Matches(@"writable:\s*false", ReadMethodBody(sourceName, "Exists"));
    }

    [Theory]
    [InlineData("RegistryStartupSource")]
    [InlineData("StartupFolderSource")]
    public void Exists_UsesCandidateNames_NotExactMatch(string sourceName)
    {
        // 🔴 注册表与启动文件夹**刻意保留**三级候选名的启发式（坑 1：注册表值名会被
        //   程序改写、`.lnk` 会被重命名）。改成精确匹配的话，一次重命名之后
        //   所有条目都会被判成"源没了" ⇒ 跳过系统恢复 + 删掉还原依据。
        //
        //   注意这与"拿不准就抛"的保守化方向**相反**，但在这里是安全的：
        //   多试几个候选名只会更常常答 true（更宽松），不会误判 false。
  Assert.Contains("GetCandidateNames", ReadMethodBody(sourceName, "Exists"), StringComparison.Ordinal);
    }

    [Fact]
    public void Uwp_UsesExactStateSubKey_NotCandidateNames()
    {
        // UWP 的状态子键路径由 `SourceDetail` + `SourceKey` 精确拼出 ——
        // 包族名与 AppId 都是系统给的稳定标识，没有回退余地。
        var body = ReadMethodBody("UwpStartupSource", "Exists");
        Assert.Contains("BuildStateSubKeyPath", body, StringComparison.Ordinal);
        Assert.DoesNotContain("GetCandidateNames", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduledTask_UsesExactPathMatch_NotCandidateNames()
    {
        // 计划任务按完整路径精确匹配。`OrdinalIgnoreCase` 是对的 —— Windows 路径本身
        // 大小写不敏感，那是**精确匹配**而不是启发式。
        var body = ReadMethodBody("ScheduledTaskSource", "Exists");
        Assert.Contains("TryGetTask", body, StringComparison.Ordinal);
        Assert.DoesNotContain("GetCandidateNames", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// 只在构造阶段占位的链接解析器：<c>Exists</c> 根本不该调它。
    /// </summary>
    /// <remarks>
    /// 做成"被调就抛"而不是返回 null，是为了在有人给 <c>Exists</c> 加了解析器调用时
    /// 立刻炸出来 —— 那条调用是多余的（<c>Exists</c> 只看启动项在不在，不看它指向哪），
    /// 而多余的一次 COM 调用在真机上就是一次可能失败的系统调用。
    /// </remarks>
    private sealed class ForbiddenResolver : IShellLinkResolver
    {
        public ParsedCommandLine? Resolve(string shortcutPath)
            => throw new InvalidOperationException($"Exists 不该调用链接解析器（{shortcutPath}）。");
    }

  private static string ReadMethodBody(string sourceName, string methodName)
    {
    var path = LocateSource(sourceName);
        var lines = File.ReadAllLines(path);
   var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
  if (lines[i].Contains($"public bool {methodName}(", StringComparison.Ordinal))
     {
          start = i;
       break;
            }
     }

        Assert.True(start >= 0, $"{sourceName}.{methodName} 不在 {path} 里");

        // 从签名行往后数花括号，直到配平。
        var depth = 0;
        var opened = false;
        for (var i = start; i < lines.Length; i++)
  {
          foreach (var c in lines[i])
       {
          if (c == '{') { depth++; opened = true; }
           else if (c == '}') { depth--; }
          }

    if (opened && depth == 0)
   {
    return StripComments(string.Join('\n', lines[start..(i + 1)]));
 }
    }

        throw new InvalidOperationException($"{sourceName}.{methodName} 的方法体没配平（源码格式异常）。");
    }

    /// <summary>剥掉行注释与块注释，只留代码本身。</summary>
    /// <param name="source">源码文本。</param>
    /// <returns>去掉注释后的文本。</returns>
    /// <remarks>
    /// 🔴 断言"实现形状"时**必须**先剥注释，否则一句提到 <c>catch</c> 或 <c>writable</c>
    /// 的说明文字就会把断言带偏 —— 那既会让正确的代码因为注释改动而变红，
    /// 也会让"没有 catch"这条被一句提到它的注释满足。
    /// 断言要问的是代码做了什么，不是注释说了什么。
    /// </remarks>
    private static string StripComments(string source)
    {
    var withoutBlock = System.Text.RegularExpressions.Regex.Replace(
            source,
     @"/\*.*?\*/",
            " ",
    System.Text.RegularExpressions.RegexOptions.Singleline);

        return string.Join(
            '\n',
     withoutBlock.Split('\n').Select(static line =>
            {
                var idx = line.IndexOf("//", StringComparison.Ordinal);
    return idx >= 0 ? line[..idx] : line;
          }));
    }

    /// <summary>定位某个来源类的源码文件。</summary>
  /// <param name="sourceName">来源类名。</param>
    /// <returns>完整路径。</returns>
    /// <remarks>
    /// 🔴 <b>从 <see cref="AppContext.BaseDirectory"/> 往上找，而不是数固定层数的 <c>..</c></b>。
    /// 数层数的那种写法（原先是 <c>../../../../../src/...</c>）会在下面这些情况下
    /// **因非结构性原因**失败，而每次失败都长得像"契约被破坏了"：
    /// ① 输出目录多一层或少一层（<c>--artifacts-path</c>、换 TFM）；
    /// ② 跑的是发布出来的测试二进制；
    /// ③ 单仓库之外的打包方式。
    /// <para>
    /// 认的是 <c>src\DelayStart.Management\Sources\{name}.cs</c> 这个**实际存在的东西**，
    /// 找到为止；找不到就明确报出来，而不是让后面那句读取抛一个含义不明的异常。
    /// </para>
    /// </remarks>
    private static string LocateSource(string sourceName)
    {
      var relative = Path.Combine("src", "DelayStart.Management", "Sources", sourceName + ".cs");

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
    var candidate = Path.Combine(dir.FullName, relative);
     if (File.Exists(candidate))
  {
       return candidate;
       }
   }

        throw new FileNotFoundException(
            $"从 {AppContext.BaseDirectory} 往上找不到 {relative}（本测试需要仓库源码）。");
    }
}
