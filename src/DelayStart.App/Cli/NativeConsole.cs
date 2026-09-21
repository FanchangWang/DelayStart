using System.Runtime.InteropServices;
using System.Text;

namespace DelayStart.App.Cli;

/// <summary>
/// 让 WinExe 进程能往调用者的控制台写输出。
/// </summary>
/// <remarks>
/// <para>
/// 管理端的 <c>OutputType</c> 是 <c>WinExe</c>（必须如此，否则每次启动都会闪一个黑框）。
/// 但 WinExe 进程**默认不附加任何控制台**，<see cref="Console.WriteLine(string)"/> 写出去的内容
/// 会被直接丢弃 —— 于是 <c>--restore-all</c> 的失败原因、<c>--scan</c> 的清单全都看不见。
/// </para>
/// <para>
/// <see cref="AttachConsole"/> 把本进程挂到父进程（PowerShell / cmd）的控制台上，
/// 之后标准输出/错误流才有去处。父进程没有控制台时（例如从资源管理器双击）附加会失败，
/// 此时静默降级 —— 日志文件仍然完整，命令行交互本来也不是那种场景下的用法。
/// </para>
/// </remarks>
internal static partial class NativeConsole
{
    /// <summary><c>ATTACH_PARENT_PROCESS</c>：附加到父进程的控制台。</summary>
    private const int AttachParentProcess = -1;

    /// <summary>
    /// 尝试附加到父进程控制台并改好输出编码。失败时不做任何事。
    /// </summary>
    public static void EnsureAttached()
    {
        try
        {
            if (!AttachConsole(AttachParentProcess))
            {
                return;
            }

            // 🔴 必须重设编码：中文 Windows 控制台默认是 GBK，而我们的输出与日志一律 UTF-8，
            // 不设就会让所有中文变成乱码 —— 而这恰恰是给用户看的失败原因。
            Console.OutputEncoding = Encoding.UTF8;

            var stdout = new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true };
            Console.SetOut(stdout);

            var stderr = new StreamWriter(Console.OpenStandardError(), Encoding.UTF8) { AutoFlush = true };
            Console.SetError(stderr);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // 拿不到控制台不影响任何业务逻辑，忽略。
        }
    }

    /// <remarks>
    /// 坑 9（<c>pitfalls.md</c> 四）：<c>LibraryImport</c> 不会自动封送 <c>bool</c> 返回值，
    /// 必须显式写 <c>[return: MarshalAs(UnmanagedType.Bool)]</c>，否则读到的值永远是错的。
    /// </remarks>
    [LibraryImport("kernel32.dll", EntryPoint = "AttachConsole", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int dwProcessId);
}
