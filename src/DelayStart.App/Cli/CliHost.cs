using System.Globalization;

using DelayStart.Core.Logging;
using DelayStart.Core.Models;
using DelayStart.Management.Models;

namespace DelayStart.App.Cli;

/// <summary>
/// headless 子命令的处理（D32）。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由：Phase 2 交付的写入链路（软禁用 / 接管 / 恢复 / 计划任务）**没有 UI 消费者** ——
/// 界面在 Phase 3。若不做这一层，<c>design.md</c> NFR-3.2 安全验收的"三个 <c>Run</c> 键值数量与内容零变化"
/// 这条验收就只能等到 Phase 3 之后才可能执行，整个 Phase 2 的产物一次都没真跑过。
/// </para>
/// <para>
/// 其中 <c>--restore-all</c> 与 <c>--reinstall-task</c> **本来就必须存在**：
/// D22 规定卸载时由 Inno 的 <c>[Code] InitializeUninstall()</c> 调用前者并检查退出码
/// （非 0 中止卸载），管理端首次启动则调用后者做幂等注册。
/// </para>
/// </remarks>
internal static class CliHost
{
    private const string CommandPrefix = "--";

    private const int ExitSuccess = 0;
    private const int ExitFailure = 1;
    private const int ExitUsage = 2;

    /// <summary>
    /// 若命令行命中子命令则执行并给出退出码。
    /// </summary>
    /// <param name="services">共享容器，由调用方在进程启动时构建一次。</param>
    /// <param name="args">命令行参数。</param>
    /// <param name="exitCode">执行结果对应的退出码。</param>
    /// <returns>
    /// 命中子命令（已执行）时为 <see langword="true"/>；
    /// 命令行没有 <c>--</c> 参数时为 <see langword="false"/>，调用方应正常启动图形界面。
    /// </returns>
    /// <remarks>
    /// 🔴 容器由**调用方**传入而不是这里自己建：同一个进程里 GUI 与 CLI 只会走一条路，
    /// 但它们共用 <see cref="DelayStart.Core.Services.PathService"/> 与
    /// <see cref="DelayStart.Core.Abstractions.ILogSink"/>，容器必须是同一个
    /// （两个 <see cref="DelayStart.Core.Logging.FileLogger"/> 打开同一个日志文件会共享冲突）。
    /// </remarks>
    public static bool TryExecute(IServiceProvider services, string[] args, out int exitCode)
    {
        exitCode = ExitSuccess;

        if (args.Length == 0 || !args[0].StartsWith(CommandPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        NativeConsole.EnsureAttached();

        var command = args[0].ToLowerInvariant();

        try
        {
            if (command == "--help")
            {
                exitCode = PrintHelp();
                return true;
            }

            var cli = CliServices.Create(services);

            exitCode = command switch
            {
                "--scan" => RunScan(cli),
                "--takeover" => RunTakeover(cli, args),
                "--release" => RunRelease(cli, args),
                "--restore-all" => RunRestoreAll(cli, args),
                "--reinstall-task" => RunReinstallTask(cli),
                _ => UnknownCommand(command),
            };
        }
        catch (StartupOperationException ex)
        {
            // 语义异常自带条目标识，直接呈现即可（R6）。
            Console.Error.WriteLine($"失败：{ex.Message}");
            exitCode = ExitFailure;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"命令执行失败：{ex.Message}");
            exitCode = ExitFailure;
        }

        return true;
    }

    private static int RunScan(CliServices services)
    {
        var result = services.Scanner.Scan();

        Console.WriteLine($"共 {result.TotalCount} 项（已接管 {result.TakenOverCount}，仍会正常自启 {result.ActiveCount}）");
        Console.WriteLine();

        foreach (var entry in result.Entries)
        {
            Console.WriteLine(
                $"[{DescribeState(entry)}] {entry.Name} | {entry.Source}/{entry.Scope} | {entry.Path} | {entry.Id}");
        }

        if (!result.HasFailures)
        {
            return ExitSuccess;
        }

        Console.WriteLine();
        Console.WriteLine("⚠ 以下来源整体扫描失败，上面的列表**不完整**（FR-1.4）：");
        foreach (var failure in result.Failures)
        {
            Console.WriteLine($"  - {failure.DisplayName}：{failure.Message}");
        }

        return ExitFailure;
    }

    private static int RunTakeover(CliServices services, string[] args)
    {
        if (args.Length < 2)
        {
            return Usage("--takeover <条目主键> [延时秒数]");
        }

        var delay = DelayedItem.DefaultDelaySeconds;
        if (args.Length >= 3
            && !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out delay))
        {
            return Usage($"延时秒数不是整数：{args[2]}");
        }

        var entry = services.Scanner.Scan().Entries
            .FirstOrDefault(candidate => string.Equals(candidate.Id, args[1], StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            Console.Error.WriteLine($"未找到条目：{args[1]}。可先用 --scan 查看可用主键。");
            return ExitFailure;
        }

        var outcome = services.Takeover.Takeover(entry, new TakeoverOptions { DelaySeconds = delay });

        if (outcome.Succeeded)
        {
            Console.WriteLine($"已接管『{entry.Name}』，延时 {delay} 秒。原自启动项已软禁用（未删除任何数据）。");
            return ExitSuccess;
        }

        Console.Error.WriteLine($"接管失败：{outcome.Message}");
        if (!outcome.RolledBack)
        {
            Console.Error.WriteLine("⚠ 回滚未完全成功，系统可能处于半完成状态 —— 请查看日志确认具体环节。");
        }

        return ExitFailure;
    }

    private static int RunRelease(CliServices services, string[] args)
    {
        if (args.Length < 2)
        {
            return Usage("--release <条目主键>");
        }

        var item = services.ConfigStore.Load().Items
            .FirstOrDefault(candidate => string.Equals(candidate.Id, args[1], StringComparison.OrdinalIgnoreCase));

        if (item is null)
        {
            Console.Error.WriteLine($"延时启动列表中没有该条目：{args[1]}。可先用 --scan 查看。");
            return ExitFailure;
        }

        var outcome = services.Takeover.Release(item);

        if (outcome.Succeeded)
        {
            Console.WriteLine($"已移出延时启动：『{item.Name}』。系统启动项已恢复原状。");
            return ExitSuccess;
        }

        Console.Error.WriteLine($"移出失败：{outcome.Message}");
        return ExitFailure;
    }

    /// <summary>
    /// <c>--restore-all</c>：还原全部接管项并删除计划任务（D22 的卸载路径）。
    /// </summary>
    /// <param name="services">共享服务。</param>
    /// <param name="args">原始命令行，用于取可选的 <c>--result-file</c>。</param>
    /// <remarks>
    /// <para>
    /// 🔴 D61（2026-09-21 真机实测）：本命令**必须提权才能跑**（app.manifest 是
    /// <c>requireAdministrator</c>），而 Inno 的卸载器以 <c>PrivilegesRequired=lowest</c>
    /// 运行，其 <c>Exec</c>（CreateProcess）打不开提权目标 —— 直接 740
    /// （ERROR_ELEVATION_REQUIRED），还原动作根本不会发生。
    /// </para>
    /// <para>
    /// 卸载流程因此改用 <c>ShellExec('runas', ...)</c> 拉起（弹一次 UAC），代价是
    /// **拿不到退出码**。所以约定：用 <c>--result-file &lt;路径&gt;</c> 把退出码写进文件，
    /// 卸载器起完进程后回读该文件 —— D22「非 0 就中止卸载」的保证靠它维持。
    /// </para>
    /// </remarks>
    private static int RunRestoreAll(CliServices services, string[] args)
    {
        var outcome = services.Takeover.RestoreAll();

        Console.WriteLine($"还原完成：成功 {outcome.RestoredCount} 项，失败 {outcome.FailedCount} 项。");
        Console.WriteLine(outcome.TaskDeleted
            ? "调度计划任务已删除。"
            : "⚠ 调度计划任务未能删除，请手动检查。");

        foreach (var failure in outcome.Failures)
        {
            Console.Error.WriteLine($"  - {failure}");
        }

        if (!outcome.Succeeded)
        {
            Console.Error.WriteLine("⚠ 存在未还原的条目 —— 卸载流程**必须中止**，否则这些程序将永久失去自启动且用户毫不知情。");
        }

        WriteResultFile(args, outcome.ExitCode);

        return outcome.ExitCode;
    }

    /// <summary>
    /// 把退出码写进 <c>--result-file &lt;路径&gt;</c> 指定的文件（D61）。
    /// </summary>
    /// <param name="args">原始命令行。</param>
    /// <param name="exitCode">本次执行的退出码。</param>
    /// <remarks>
    /// 供"以提升权限拉起、拿不到退出码"的调用方（Inno 卸载器）回读。
    /// 没传参数、或写盘失败都**静默忽略** —— 本命令的主职责是还原系统启动项，
    /// 结果文件只是给卸载器的旁路通道，绝不能因为它挡掉退出码。
    /// 文件内容就是十进制整数（无换行），卸载器按 <c>StrToIntDef</c> 解析。
    /// </remarks>
    private static void WriteResultFile(string[] args, int exitCode)
    {
        // 只在本方法用到，就近声明（选项名与 iss 的 InitializeUninstall 必须同步）。
        const string ResultFileOption = "--result-file";

        var index = Array.FindIndex(
            args,
            arg => string.Equals(arg, ResultFileOption, StringComparison.OrdinalIgnoreCase));

        // 约定：选项后面紧跟路径；缺路径就当没传过。
        if (index < 0 || index + 1 >= args.Length)
        {
            return;
        }

        try
        {
            File.WriteAllText(args[index + 1], exitCode.ToString(CultureInfo.InvariantCulture));
        }
        catch (IOException)
        {
            // 结果文件写不了不影响退出码。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }

    private static int RunReinstallTask(CliServices services)
    {
        services.TaskRegistrar.RegisterOrUpdate();
        Console.WriteLine($"调度计划任务已就绪：{services.TaskRegistrar.TaskPath}");
        return ExitSuccess;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("DelayStart — 命令行工具");
        Console.WriteLine();
        Console.WriteLine("用法：DelayStart.exe <命令> [参数]");
        Console.WriteLine();
        Console.WriteLine("命令：");
        Console.WriteLine("  --scan                      扫描全部自启动项并列出（含稳定主键）");
        Console.WriteLine("  --takeover <主键> [秒数]     接管指定条目，默认 30 秒");
        Console.WriteLine("  --release <主键>             移出延时启动，恢复系统原状");
        Console.WriteLine("  --restore-all               还原全部接管项并删除计划任务（卸载时调用）");
        Console.WriteLine("  --result-file <路径>         把退出码写到该文件（供提权拉起方回读，见 D61）");
        Console.WriteLine("  --reinstall-task            幂等注册 / 更新调度计划任务");
        Console.WriteLine("  --help                      显示本帮助");
        Console.WriteLine();
        Console.WriteLine("退出码：0 成功 · 1 失败 · 2 参数错误");
        Console.WriteLine("不带任何 -- 参数时启动图形界面。");

        return ExitSuccess;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}");
        Console.Error.WriteLine("可用命令见 --help。");
        return ExitUsage;
    }

    private static int Usage(string usage)
    {
        Console.Error.WriteLine($"参数错误。用法：{usage}");
        return ExitUsage;
    }

    private static string DescribeState(StartupEntry entry)
    {
        if (entry.IsTakenOver)
        {
            return "已接管";
        }

        if (entry.IsMissing)
        {
            return "已失效";
        }

        if (entry.IsProtected)
        {
            return "只读";
        }

        return entry.IsEnabled ? "启用" : "已禁用";
    }
}
