using System.Runtime.InteropServices;
using System.Text;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Interop;

namespace DelayStart.Management.Services;

/// <summary>
/// 用 <c>IShellLinkW</c> + <c>IPersistFile</c> 解析 <c>.lnk</c> 的目标与参数（FR-1.7 / D9）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不用 <c>ShellLink</c> 之类的第三方轻量封装：本层已经为了 FR-11 引入了一个包，
/// 再为一件只有一个方法的功能引入第二个包不划算，而 COM 声明的维护成本是一次性的。
/// </para>
/// <para>
/// 🔴 <b>失败一律返回 <see langword="null"/>，绝不抛异常</b>。快捷方式可能损坏、指向已卸载的程序、
/// 或由第三方工具写出非标准结构 —— 这些都不该让整次扫描失败（FR-1.4）。调用方拿到
/// <see langword="null"/> 后回退显示快捷方式自身。
/// </para>
/// </remarks>
public sealed class ShellLinkResolver : IShellLinkResolver
{
    private const int MaxPath = 1024;

    private readonly ILogSink _log;

    /// <summary>构造解析器。</summary>
    /// <param name="log">日志接收端，用于记录解析失败的原因（不抛出，只留痕）。</param>
    public ShellLinkResolver(ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <inheritdoc />
    public ParsedCommandLine? Resolve(string shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) || !File.Exists(shortcutPath))
        {
            return null;
        }

        try
        {
            var shellLink = ComFactory.CreateInstance<IShellLinkW>(ShellLinkClassId.Value);
            try
            {
                // 先把文件内容加载进 COM 对象；不加这一步 GetPath 只会拿到空串。
                ((IPersistFile)shellLink).Load(shortcutPath, ShellLinkFlags.StgmRead);

                var pathBuffer = new StringBuilder(MaxPath);
                shellLink.GetPath(pathBuffer, pathBuffer.Capacity, IntPtr.Zero, ShellLinkFlags.RawPath);

                var argsBuffer = new StringBuilder(MaxPath);
                shellLink.GetArguments(argsBuffer, argsBuffer.Capacity);

                var target = pathBuffer.ToString().Trim();
                if (string.IsNullOrWhiteSpace(target))
                {
                    // 例如指向 UWP 应用或"这台电脑"的快捷方式，没有可执行路径。
                    // 不算错误，静默返回让调用方回退。
                    return null;
                }

                return new ParsedCommandLine(target, argsBuffer.ToString().Trim());
            }
            finally
            {
                // 🔴 必须释放 COM 对象。扫描一次可能解析几十个快捷方式，
                // 靠 GC 回收这些 RCW 会把句柄留到下一次 GC，在长时间运行的进程里不可接受。
                Marshal.ReleaseComObject(shellLink);
            }
        }
        // InvalidOperationException 来自 ComFactory（组件创建失败）；
        // COMException 则是 COM 调用本身（Load / GetPath）失败时由运行时抛出的。
        catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException
            or FileNotFoundException or ArgumentException)
        {
            _log.Warn(ex, $"解析快捷方式失败，回退显示快捷方式本身：{shortcutPath}");
            return null;
        }
    }
}
