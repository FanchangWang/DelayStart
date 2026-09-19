using DelayStart.App.Cli;
using DelayStart.App.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DelayStart.App;

/// <summary>
/// 进程入口（D32）。取代 XamlCompiler 自动生成的 <c>Main</c>
/// （见 <c>DelayStart.App.csproj</c> 的 <c>DISABLE_XAML_GENERATED_MAIN</c>）。
/// </summary>
    /// <remarks>
    /// <para>
    /// 分流逻辑：命令行命中 headless 子命令 → 执行完直接返回退出码，**完全不初始化 WinUI**；
    /// 否则走标准的 WinUI 启动流程。
    /// </para>
    /// <para>
    /// 🔴 顺序不能颠倒。若先 <see cref="Application.Start"/> 再判命令行，headless 调用会
    /// 先弹出窗口再退出 —— 用户在卸载过程中会看到一个窗口一闪而过，而且
    /// <c>Application.Start</c> 是阻塞的，根本走不到后面的判断。
    /// </para>
    /// <para>
    /// 🔴 **容器在分流之前构建，且全进程只有一个。** CLI 与 GUI 共用
    /// <c>PathService</c> 与 <c>ILogSink</c> —— 两个 <c>FileLogger</c> 同时打开
    /// <c>manager.log</c> 会共享冲突。放在分流之前还保证 headless 路径
    /// 也不会漏掉"建目录树"这个副作用。
    /// </para>
    /// </remarks>
public static class Program
{
    /// <summary>进程入口。</summary>
    /// <param name="args">命令行参数。</param>
    /// <returns>进程退出码。headless 子命令的退出码会被卸载脚本检查（D22）。</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        var services = new ServiceCollection();
        services.AddDelayStartServices();

        // using：Application.Start 是阻塞的，返回即进程结束，此时才释放容器。
        // 容器的 Dispose 会连带释放它创建的 IDisposable（含 FileLogger 的文件句柄）。
        using var provider = services.BuildServiceProvider();

        if (CliHost.TryExecute(provider, args, out var exitCode))
        {
            return exitCode;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();

        // ⚠️ lambda 参数**不能**命名为 `_`：那样下面本想"丢弃结果"的 `_ = new App()`
        // 会被编译器理解成给该参数赋值，报 CS0029。所以这里用 `(p)`。
        //
        // 另一处刻意：写 `_ = new App()` 而不是模板原样的裸 `new App();` —— 后者在本仓库的
        // 分析器设置下会报 CA1806（创建了实例却从未使用）。App 实例由 WinUI 内部持有
        // （Application.Current），这里的作用只是"把它启动起来"。
        Application.Start((p) =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(provider);
        });

        return 0;
    }
}
