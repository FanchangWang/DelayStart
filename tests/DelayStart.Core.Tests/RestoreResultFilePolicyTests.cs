using DelayStart.Core.Services;
using DelayStart.Core.Tests.Fakes;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="RestoreResultFilePolicy"/> 的单元测试（D144）：卸载还原结果文件的路径白名单与独占写入。
/// </summary>
/// <remarks>
/// 这条通道一旦被伪造，卸载器就会把一次没做完的还原当成成功 —— 用户永久失去被接管自启动项的
/// 还原入口，且没有任何痕迹。所以这里的用例全部是"必须判否"的负向判定，
/// 少一条就等于把一条抢先路径放回去。
/// </remarks>
public sealed class RestoreResultFilePolicyTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <summary>扮演交换目录（真实运行时是 <c>%TEMP%\DelayStart</c>）。</summary>
    private string ExchangeRoot => Path.Combine(_temp.Path, "DelayStart");

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void IsAllowedPath_路径在交换目录下_判真()
    {
        // Arrange
        var candidate = Path.Combine(ExchangeRoot, "restore-123-456.txt");

        // Act
        var allowed = RestoreResultFilePolicy.IsAllowedPath(candidate, ExchangeRoot);

        // Assert
        Assert.True(allowed, "交换目录下的结果文件必须放行，否则卸载流程永远拿不到结果");
    }

    [Fact]
    public void IsAllowedPath_路径在子目录下_判真()
    {
        // 交换目录将来若再分子层，白名单不该顺手把子层也堵掉。
        var candidate = Path.Combine(ExchangeRoot, "sub", "restore-123-456.txt");

        Assert.True(RestoreResultFilePolicy.IsAllowedPath(candidate, ExchangeRoot));
    }

    [Fact]
    public void IsAllowedPath_路径恰好是交换目录本身_判否()
    {
        // 目录本身不是文件：放行它只会换来一次 FileStream 抛 UnauthorizedAccessException 的写入失败，
        // 在闸门口判否才能把"你给错了路径"这件事说清楚，而不是伪装成"写不进去"。
        Assert.False(RestoreResultFilePolicy.IsAllowedPath(ExchangeRoot, ExchangeRoot));
    }

    [Fact]
    public void IsAllowedPath_路径带尾部分隔符且等于交换目录_判否()
    {
        // 归一化会去掉尾部分隔符，否则 `<base>\` 与 `<base>` 会因为多一个字符而绕过上面对照判定。
        var withSeparator = ExchangeRoot + Path.DirectorySeparatorChar;

        Assert.False(RestoreResultFilePolicy.IsAllowedPath(withSeparator, ExchangeRoot));
    }

    [Fact]
    public void IsAllowedPath_父级穿越逃出交换目录_判否()
    {
        // `\..\evil.txt` 归一化后落在 %TEMP% 下 —— 朴素前缀比较（先比 base 再看剩余段）
        // 会放它过；先 GetFullPath 再比才是正确的做法。
        var candidate = Path.Combine(ExchangeRoot, "..", "evil.txt");

        Assert.False(RestoreResultFilePolicy.IsAllowedPath(candidate, ExchangeRoot));
    }

    [Fact]
    public void IsAllowedPath_多次穿越后回到交换目录内_判真()
    {
        // 归一化的目的是判定"最终落在哪"，不是见着 `..` 就拒 —— 绕一圈回到目录内是合法路径。
        var candidate = Path.Combine(ExchangeRoot, "sub", "..", "restore-123-456.txt");

        Assert.True(RestoreResultFilePolicy.IsAllowedPath(candidate, ExchangeRoot));
    }

    [Fact]
    public void IsAllowedPath_大小写不同的目录与文件名_判真()
    {
        // Windows 路径不区分大小写：`%TEMP%\DELAYSTART\A.TXT` 与交换目录指向同一个文件，
        // 判否会把合法调用挡在门外。这里用 OrdinalIgnoreCase 比对，断言的是**语义**。
        var candidate = Path.Combine(
            _temp.Path.ToUpperInvariant(),
            "delaystart",
            "RESTORE-123-456.TXT");

        Assert.True(RestoreResultFilePolicy.IsAllowedPath(candidate, ExchangeRoot));
    }

    [Fact]
    public void IsAllowedPath_兄弟目录仅前缀相同_判否()
    {
        // `…\DelayStart-evil` 与 `…\DelayStart` 用朴素前缀比会判"在里面"，
        // 它其实是**兄弟目录** —— 比对时必须自己补一个分隔符。
        var candidate = _temp.Path + @"\DelayStart-evil\restore.txt";

        Assert.False(RestoreResultFilePolicy.IsAllowedPath(candidate, ExchangeRoot));
    }

    [Fact]
    public void IsAllowedPath_路径在交换目录之外_判否()
    {
        var candidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "DelayStart",
            "restore.txt");

        Assert.False(RestoreResultFilePolicy.IsAllowedPath(candidate, ExchangeRoot));
    }

    [Fact]
    public void IsAllowedPath_相对路径_判否()
    {
        // 相对路径的 GetFullPath 以**当前工作目录**为基准 —— 而提权进程的当前工作目录
        // 由启动方给定（这里是 {app}），判否可以杜绝"碰巧落在允许目录里"这种不可预期的放行。
        Assert.False(RestoreResultFilePolicy.IsAllowedPath(@".\restore.txt", ExchangeRoot));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsAllowedPath_候选路径为空串或空白_判否(string? candidate)
    {
        // 空串不是"路径"：拿它去 GetFullPath 得到的是当前工作目录，
        // 那等于把工作目录悄悄当成合法落点。
        Assert.False(RestoreResultFilePolicy.IsAllowedPath(candidate, ExchangeRoot));
    }

    [Fact]
    public void IsAllowedPath_路径含NUL字符_判否()
    {
        // 🔴 用**真的** NUL，不是字面量 "\u0000"：逐字符验证过的坑 —— 逐字面量里的反斜杠
        //    是分隔符，剩下的 \u0000 是五个合法字符，路径完全正常，判否断言会假绿。
        //    真 NUL 的危险在于 Win32 会在那里截断：`…\restore\u0000.txt` 落到磁盘上就是
    //    `<base>\restore`，与调用方以为写到的位置不是同一个东西。
        var candidate = ExchangeRoot + "\restore\u0000.txt";

        Assert.False(RestoreResultFilePolicy.IsAllowedPath(candidate, ExchangeRoot));
    }

    [Fact]
    public void IsAllowedPath_交换目录为null或空白_抛参数异常()
    {
        // 白名单的基准是调用方给的，不是用户输入 —— 它为空属于编程错误，必须当场炸出来，
        // 不能退化成"什么路径都判否"（那会让卸载流程永远拿不到结果，且没人知道为什么）。
        Assert.Throws<ArgumentNullException>(() => RestoreResultFilePolicy.IsAllowedPath("x.txt", null!));
        Assert.Throws<ArgumentException>(() => RestoreResultFilePolicy.IsAllowedPath("x.txt", "   "));
    }

    [Fact]
    public void WriteExclusive_目标不存在_写入内容()
    {
        var path = Path.Combine(ExchangeRoot, "restore-123-456.txt");

        RestoreResultFilePolicy.WriteExclusive(path, "0");

        Assert.Equal("0", File.ReadAllText(path));
    }

    [Fact]
    public void WriteExclusive_目标已存在_抛异常且不覆盖原内容()
    {
        // 🔴 本条是整批整改的核心：预置一个填着 "0" 的同名文件曾经能让卸载器读到"还原成功"。
        //    独占创建把这个结果变成一次**可见的失败**，而不是一次无声的覆盖。
        var path = Path.Combine(ExchangeRoot, "restore-123-456.txt");
        Directory.CreateDirectory(ExchangeRoot);
        File.WriteAllText(path, "0");

        Assert.Throws<IOException>(() => RestoreResultFilePolicy.WriteExclusive(path, "1"));
        Assert.Equal("0", File.ReadAllText(path));
    }

    [Fact]
    public void WriteExclusive_内容含非零退出码_按原样落盘()
    {
        var path = Path.Combine(ExchangeRoot, "restore-123-456.txt");

        RestoreResultFilePolicy.WriteExclusive(path, "1");

        Assert.Equal("1", File.ReadAllText(path));
    }

    // ── 2026-10-03 真机踩中：两侧对「临时根在哪」的认知不一致 ────────────────────
    //
    // 卸载器原先用 Inno 的 {tmp}，展开成 %TEMP%\is-XXXXXX.tmp\ —— 比
    // Path.GetTempPath() 多一层。程序按「必须落在 %TEMP%\DelayStart\ 下」拒写，
    // 卸载器等满 60 秒后弹「恢复程序没有返回结果（可能取消了 UAC 提权，或程序已损坏）」。
    // 用户看到的是「没有任何进程在跑，卸载器却在等」—— 而真相只是两侧路径写法不一致。
    //
    // 修法两处：卸载器改用 {env:TEMP}；本策略的目录那一道从「某一级子目录」放宽到整个
    // 临时根，由新增的**文件名格式**那一道兜住。下面几条钉住这个取舍。

    [Fact]
    public void IsAllowedPath_多出一层安装器临时子目录_仍判真()
    {
        // 复刻真实故障的路径形状：根对不上时不应再拒写，否则卸载直接走不通。
        var candidate = Path.Combine(
            _temp.Path, "is-SHGBF20IMY.tmp", "DelayStart", "restore-791205105-864841340.txt");

        Assert.True(RestoreResultFilePolicy.IsAllowedPath(candidate, _temp.Path));
    }

    [Fact]
    public void IsAllowedPath_文件名不是本协议格式_判否()
    {
        // 🔴 目录放宽之后，这道是唯一还挡着「借这条通道写别的文件」的闸，必须钉死。
        //   攻击者想借它覆盖 app.json.tmp（配置写入链路）或任何别的名字，都必须在这里判否。
        foreach (var badName in new[]
                 {
                     "app.json.tmp",
                     "restore.txt",
                     "restore-abc-def.txt",
                     "restore-1-2-3.txt",
                     "restore-1-2.txt.evil",
                 })
        {
            var candidate = Path.Combine(_temp.Path, badName);
            Assert.False(RestoreResultFilePolicy.IsAllowedPath(candidate, _temp.Path), badName);
        }
    }

    [Theory]
    [InlineData("restore-1-2.txt")]
    [InlineData("restore-791205105-864841340.txt")]
    [InlineData("RESTORE-1-2.TXT")]
    public void IsExpectedFileName_符合格式_判真(string fileName)
    {
        Assert.True(RestoreResultFilePolicy.IsExpectedFileName(fileName));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("restore-.txt")]
    [InlineData("restore-1.txt")]
    [InlineData("other-1-2.txt")]
    public void IsExpectedFileName_不符合格式_判否(string? fileName)
    {
        Assert.False(RestoreResultFilePolicy.IsExpectedFileName(fileName));
    }

    [Fact]
    public void TempRoot_是TempExchangeRoot的上级()
    {
        // 两个常量必须保持这个关系，否则白名单根与实际写入目录会悄悄错位 —— 本次故障的同款。
        // 🔴 比对前必须 TrimEndingDirectorySeparator：`Path.GetTempPath()` **带**尾分隔符，
        //    而 Path.Combine 之后取父目录得到的是**不带**的，不归一化就会恒不相等。
        //    （IsAllowedPath 内部也做了同样的归一化，两处必须一致。）
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetDirectoryName(PathService.TempExchangeRoot)!)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(PathService.TempRoot)),
            ignoreCase: true);
    }

    [Fact]
    public void WriteExclusive_父目录不存在_自动创建()
    {
        // 与 AtomicFileWriter 同一约定：目录由写入方按需创建，
        // 否则卸载器那侧少一个 CreateDir 就会变成一次"写不出来"。
        var path = Path.Combine(ExchangeRoot, "restore-123-456.txt");

        RestoreResultFilePolicy.WriteExclusive(path, "0");

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void TempExchangeRoot_落在系统临时目录下的DelayStart子目录()
    {
        // 卸载器侧的 ExpandConstant('{tmp}\DelayStart') 必须与它逐字一致：
        // 不一致的表现是提权进程拒绝写、卸载器白等 60 秒。
        var expected = Path.Combine(Path.GetTempPath(), "DelayStart");

        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(PathService.TempExchangeRoot), ignoreCase: true);
    }

    [Fact]
    public void TempExchangeRoot_属于允许写入的根目录()
    {
        // 它确实是写入目标，写进 WritableRoots 才不破坏该属性的含义（"全部允许写入的根目录"）。
        var paths = new PathService(_temp.Combine("local"), _temp.Combine("config"), _temp.Path);

        Assert.Contains(PathService.TempExchangeRoot, paths.WritableRoots);
    }
}