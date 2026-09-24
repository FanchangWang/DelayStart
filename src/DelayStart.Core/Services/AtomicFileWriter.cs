using System.Text;

namespace DelayStart.Core.Services;

/// <summary>
/// 原子文本文件写入（机制 8 / NFR-2.2）。
/// </summary>
/// <remarks>
/// <para>
/// 统一路径：写同目录 <c>&lt;name&gt;.tmp</c> → <c>File.Replace</c>。
/// <c>File.Replace</c> 在目标不存在时会抛异常，因此先用 <see cref="File.Exists(string)"/>
/// 分支到 <see cref="File.Move(string, string, bool)"/>。
/// </para>
/// <para>
/// 刻意**不做**失败后的临时文件清理：如果 <c>Replace</c> 失败而 <c>.tmp</c> 残留，
/// 下一次写入会以同名覆盖它，不会累积；反过来，为清理而吞掉异常会让调用方
/// 拿到一个被掩盖的失败原因，得不偿失。
/// </para>
/// <para>
/// 编码固定 UTF-8 **无 BOM**，与 <c>design.md</c> 9.2 一致。
/// </para>
/// </remarks>
public static class AtomicFileWriter
{
    private const string TemporarySuffix = ".tmp";

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 原子写入文本。目录不存在时自动创建。
    /// </summary>
    /// <param name="path">目标文件完整路径。</param>
    /// <param name="contents">要写入的文本。</param>
    /// <exception cref="IOException">写入或替换失败。</exception>
    public static void WriteAllText(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = fullPath + TemporarySuffix;
        File.WriteAllText(temporaryPath, contents, Utf8WithoutBom);

        if (File.Exists(fullPath))
        {
            File.Replace(temporaryPath, fullPath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
    }

    /// <summary>
    /// 读取文本。文件不存在时返回 <see langword="null"/>，让调用方自行决定是"用默认值"还是"报错"。
    /// </summary>
    /// <remarks>
    /// 🔴 不用 <see cref="File.Exists(string)"/> 预判：权限不足 / 文件被锁会让它返回
    /// <see langword="false"/>，把"读不到"误报成"不存在"（B1）。直接读，只把真正的不存在吞掉。
    /// </remarks>
    /// <param name="path">文件完整路径。</param>
    /// <returns>文件内容；不存在时为 <see langword="null"/>。权限 / 锁定等 I/O 错误照样抛出。</returns>
    public static string? ReadAllTextOrNull(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return File.ReadAllText(path, Utf8WithoutBom);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
