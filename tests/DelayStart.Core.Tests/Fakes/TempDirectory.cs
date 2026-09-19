namespace DelayStart.Core.Tests.Fakes;

/// <summary>
/// 一次性的临时目录，用于隔离文件系统测试（不碰用户的真实 <c>%APPDATA%</c> / <c>%LOCALAPPDATA%</c>）。
/// </summary>
/// <remarks>
/// 用 <see cref="System.IO.Path.GetTempPath"/> 而不是仓库内的目录，
/// 是为了让测试在任何工作目录下都能跑，也不会把文件留在 Git 工作区里。
/// </remarks>
internal sealed class TempDirectory : IDisposable
{
    /// <summary>创建目录。</summary>
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "DelayStart.Tests",
            Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));

        Directory.CreateDirectory(Path);
    }

    /// <summary>目录完整路径。</summary>
    public string Path { get; }

    /// <summary>在目录下拼一个子路径。</summary>
    /// <param name="name">相对名称。</param>
    /// <returns>完整路径。</returns>
    public string Combine(string name) => System.IO.Path.Combine(Path, name);

    /// <inheritdoc />
    public void Dispose()
    {
        // 清理是"尽力而为"：临时目录可能被杀毒或索引服务短暂占用。
        // 残留一个临时目录不影响任何断言，为此让测试报失败反而误导排查方向。
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[TempDirectory] 清理失败：{Path} —— {ex.Message}");
        }
    }
}
