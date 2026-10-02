using DelayStart.Core.Launch;
using DelayStart.Core.Models;
using DelayStart.Core.Tests.Fakes;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="DeElevatedProcessLauncher"/> 的 UWP / UIAccess 目标判据（D41 / D70）。
/// </summary>
/// <remarks>
/// <para>
/// 这两条判据原先私有在 900 行的启动器里、零覆盖，而它们各自对应一个"真机才暴露"的 bug：
/// D41（只按路径前缀判 UWP ⇒ UWP 被当成普通 exe，报"目标文件不存在"）与
/// D70（不认 <c>uiAccess="true"</c> ⇒ 直接 CPWT 拉起报 740）。
/// </para>
/// <para>
/// 解析层（剥注释、逐个标签查 <c>uiAccess</c>）由
/// <see cref="UiAccessManifestTests"/> 覆盖；本类覆盖的是**判据本身**：
/// 以什么判 UWP、什么路径才值得去读清单、清单读不出来怎么办。
/// </para>
/// <para>
/// 🔴 全程不创建任何进程：清单按文本喂进去，"读 exe 资源"那层只用一次
/// <c>LoadLibraryEx(AS_DATAFILE)</c> —— 那是把 PE 当数据文件映射，不执行任何目标代码，
/// 且只针对临时目录里自己造的垃圾文件。
/// </para>
/// </remarks>
public sealed class UiAccessTargetTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    /// <summary>造一个只有 <see cref="DelayedItem.Source"/> 与 <see cref="DelayedItem.Path"/> 有值的条目。</summary>
    private static DelayedItem CreateItem(StartupSource source, string path)
        => new() { Name = "测试条目", Path = path, Source = source };

    /// <summary>
    /// 一份照 D70 实测形状构造的清单：Quicker.exe 的 RT_MANIFEST 里带着 VS 模板注释，
    /// 注释里躺着几行 <c>uiAccess="false"</c> 的示例标签，生效的那行是 <c>uiAccess="true"</c>。
    /// </summary>
    private const string QuickerStyleManifest = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
          <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
            <security>
              <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
                <!-- UAC 清单选项（VS 模板注释）：
                     <requestedExecutionLevel level="asInvoker" uiAccess="false" />
                     <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
                     <requestedExecutionLevel level="highestAvailable" uiAccess="false" /> -->
                <requestedExecutionLevel level="asInvoker" uiAccess="true" />
              </requestedPrivileges>
            </security>
          </trustInfo>
        </assembly>
        """;

    [Fact]
    public void IsUwpItem_来源为Uwp且路径是裸AUMID_判为UWP()
    {
        // Arrange：D41 的正例 —— UWP 条目的 Path 存的是裸 AUMID，没有 shell:AppsFolder 前缀。
        var item = CreateItem(StartupSource.Uwp, "Microsoft.X_8wekyb3d8bbwe!App");

        // Act
        var result = DeElevatedProcessLauncher.IsUwpItem(item);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsUwpItem_来源为手工且路径是裸AUMID_判为非UWP()
    {
        // Arrange：🔴 D41 的关键反例。只按路径前缀判的话这条会误判成 UWP，
        // 于是被塞进"UWP 一律降权委托"分支 —— 手工加的 AUMID 条目反而启动不了。
        var item = CreateItem(StartupSource.Manual, "Microsoft.X_8wekyb3d8bbwe!App");

        // Act
        var result = DeElevatedProcessLauncher.IsUwpItem(item);

        // Assert：主判据是 Source，路径前缀只是兜底。
        Assert.False(result);
    }

    [Fact]
    public void IsUwpItem_来源非Uwp但路径是完整解析名_按兜底判为UWP()
    {
        // Arrange：手工条目直接填了完整解析名（Source = Manual），路径兜底必须生效。
        var item = CreateItem(StartupSource.Manual, @"shell:AppsFolder\Microsoft.X_8wekyb3d8bbwe!App");

        // Act
        var result = DeElevatedProcessLauncher.IsUwpItem(item);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsUwpItem_来源为Uwp但路径为空_仍按来源判为UWP()
    {
        // Arrange：两个入参里只要有一个命中就算 UWP（逻辑或），不能要求两者同时成立。
        var item = CreateItem(StartupSource.Uwp, string.Empty);

        // Act
        var result = DeElevatedProcessLauncher.IsUwpItem(item);

        // Assert
        Assert.True(result);
    }

    [Theory]
    // 光秃秃的前缀（后面没有分隔符）不算解析名 —— 它不是任何应用的解析名。
    [InlineData("shell:AppsFolder", false)]
    [InlineData(@"C:\Program Files\Quicker\Quicker.exe", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsUwpItem_来源非Uwp且路径无解析名前缀_判为非UWP(string path, bool expected)
    {
        // Arrange / Act
        var result = DeElevatedProcessLauncher.IsUwpItem(CreateItem(StartupSource.Registry, path));

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(@"C:\Tools\Quicker.exe", true)]
    [InlineData(@"C:\Tools\Quicker.EXE", true)]
    [InlineData(@"C:\Tools\Quicker.Exe", true)]
    [InlineData("quicker.exe", true)]
    // 下面这些一律不该去读清单：后缀不是 .exe 就短路，绝不 LoadLibraryEx。
    [InlineData(@"C:\Users\me\Desktop\微信.lnk", false)]
    [InlineData(@"C:\Tools\run.bat", false)]
    [InlineData(@"C:\Tools\Quicker.exe.bak", false)]
    [InlineData(@"C:\Tools\Quickerexe", false)]
    [InlineData("Microsoft.X_8wekyb3d8bbwe!App", false)]
    [InlineData("", false)]
    public void IsUiAccessCandidatePath_按后缀忽略大小写判定_只认exe(string path, bool expected)
    {
        // Arrange / Act
        var result = DeElevatedProcessLauncher.IsUiAccessCandidatePath(path);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsUiAccessTargetFromManifest_Quicker实测形状的清单_判为UIAccess目标()
    {
        // Arrange / Act：整条链的终点 —— 生效行 uiAccess="true"，注释里的 false 示例不算数。
        var result = DeElevatedProcessLauncher.IsUiAccessTargetFromManifest(QuickerStyleManifest);

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData("""<requestedExecutionLevel level="asInvoker" uiAccess="false" />""")]
    [InlineData("""<requestedExecutionLevel level="requireAdministrator" uiAccess="false" />""")]
    // 最容易被手滑改坏的分支：没有 uiAccess 属性 ≠ 声明了 false，但结果必须同样是"不是"，
    // 且不能因为"没找到属性"而走到别的兜底分支上误判成 true。
    [InlineData("""<requestedExecutionLevel level="highestAvailable" />""")]
    [InlineData("""<requestedExecutionLevel level="asInvoker" />""")]
    public void IsUiAccessTargetFromManifest_未声明或声明为false_判为非UIAccess目标(string manifest)
    {
        // Arrange / Act
        var result = DeElevatedProcessLauncher.IsUiAccessTargetFromManifest(manifest);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsUiAccessTargetFromManifest_uiAccess只出现在注释里_判为非UIAccess目标()
    {
        // Arrange：🔴 反向的 Quicker 形状 —— VS 模板注释里带 uiAccess="true"，
        // 生效行却是 false。不剥注释就会把这一条误判成 UIAccess 目标，
        // 于是绕道中转器降权链去启动一个本来不需要 uiAccess 的程序。
        const string manifest = """
            <assembly>
              <!--
                <requestedExecutionLevel level="asInvoker" uiAccess="true" />
              -->
              <requestedExecutionLevel level="asInvoker" uiAccess="false" />
            </assembly>
            """;

        // Act
        var result = DeElevatedProcessLauncher.IsUiAccessTargetFromManifest(manifest);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData("""<requestedExecutionLevel level="asInvoker" uiAccess="TRUE" />""")]
    [InlineData("""<requestedExecutionLevel level='asInvoker' uiAccess='true' />""")]
    [InlineData("""<requestedExecutionLevel uiAccess="true" level="asInvoker" />""")]
    [InlineData("""<requestedExecutionLevel UIACCESS = "true" />""")]
    public void IsUiAccessTargetFromManifest_属性大小写引号与位置不同_仍判为UIAccess目标(string manifest)
    {
        // Arrange / Act
        var result = DeElevatedProcessLauncher.IsUiAccessTargetFromManifest(manifest);

        // Assert：清单里三种等价写法都得认 —— 漏认就退回报 740。
        Assert.True(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not xml at all")]
    [InlineData("<assembly><trustInfo /><unclosed>")]
    public void IsUiAccessTargetFromManifest_清单读不到或已损坏_判为非UIAccess目标且不抛(string? manifest)
    {
        // Arrange / Act
        var exception = Record.Exception(() => DeElevatedProcessLauncher.IsUiAccessTargetFromManifest(manifest));

        // Assert：null 是"读不到资源"，其余是"清单坏掉"；两者都沿用普通降权路径，绝不抛。
        Assert.Null(exception);
        Assert.False(DeElevatedProcessLauncher.IsUiAccessTargetFromManifest(manifest));
    }

    [Fact]
    public void TryReadEmbeddedManifest_目标不是PE文件_返回空且不抛()
    {
        // Arrange：在临时目录造一个纯文本"假 exe"。它会走到 LoadLibraryEx(AS_DATAFILE)，
        // 映射失败即返回 0 —— 不弹框、不执行任何东西（AS_DATAFILE 不执行 DllMain）。
        var fakeExe = _temp.Combine("not-a-pe.exe");
        File.WriteAllText(fakeExe, "这不是 PE 文件");

        // Act
        var exception = Record.Exception(() => LaunchNative.TryReadEmbeddedManifest(fakeExe));

        // Assert
        Assert.Null(exception);
        Assert.Null(LaunchNative.TryReadEmbeddedManifest(fakeExe));
    }

    [Fact]
    public void TryReadEmbeddedManifest_目标文件不存在_返回空且不抛()
    {
        // Arrange：调度端读到一条指向已删除文件的条目时走这条路径。
        var missing = _temp.Combine("never-created.exe");

        // Act / Assert
        Assert.Null(LaunchNative.TryReadEmbeddedManifest(missing));
    }

    [Fact]
    public void TryReadEmbeddedManifest_目标是真实PE_读回清单文本()
    {
        // Arrange：拿当前进程的可执行文件当样本。LoadLibraryEx 以 AS_DATAFILE 映射它，
        // 只读 RT_MANIFEST 资源 —— 不创建进程、不执行入口代码（这正是 D70 选这条路的理由）。
        var hostExe = Environment.ProcessPath;
        Assert.NotNull(hostExe);

        // Act
        var manifest = LaunchNative.TryReadEmbeddedManifest(hostExe);

        // Assert：读到了资源，且解出来确实是清单文本。
        Assert.NotNull(manifest);
        Assert.Contains("requestedExecutionLevel", manifest, StringComparison.Ordinal);
    }
}
