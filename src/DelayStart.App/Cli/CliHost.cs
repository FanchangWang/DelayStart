using System.Globalization;

using DelayStart.Core.Abstractions;

using DelayStart.Core.Logging;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Models;

using Microsoft.Extensions.DependencyInjection;

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

        // D147：启动身份取同一条 Core 判定，与图形界面的接管对话框口径一致 ——
        // 此前这里恒为默认 false（TakeoverOptions.RunAsAdmin 的默认值），
        // 于是同一项在命令行接管时被降成普通身份、在界面上却是管理员。
        var outcome = services.Takeover.Takeover(entry, new TakeoverOptions
        {
            DelaySeconds = delay,
            RunAsAdmin = LaunchIdentityPolicy.DefaultRunAsAdmin(entry),
        });

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
        var exitCode = ExitFailure;
        try
        {
            var outcome = services.Takeover.RestoreAll();

            Console.WriteLine($"还原完成：成功 {outcome.RestoredCount} 项，失败 {outcome.FailedCount} 项。");
            Console.WriteLine(outcome.TaskDeleted
                ? "调度计划任务已删除。"
                : "⚠ 调度计划任务未能删除，请手动检查。");

            // 守卫计划任务（D74）：卸载时一并删除。
            // 🔴 删除失败**不参与退出码**（与调度任务刻意不同的判据）：残留的守卫任务最多
            // 在下次触发时报一条"找不到程序"的系统日志，不会让用户的自启动项无法还原 ——
            // 为它中止卸载得不偿得。catch 放宽到 Exception：它要是漏出去，会把"不影响退出码"
            // 的小事升级成"整个卸载保证失效"。
            try
            {
                services.GuardRegistrar.Delete();
                Console.WriteLine("守卫计划任务已删除。");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"⚠ 守卫计划任务未能删除（不影响卸载）：{ex.Message}");
            }

            foreach (var failure in outcome.Failures)
            {
                Console.Error.WriteLine($"  - {failure}");
            }

            if (!outcome.Succeeded)
            {
                Console.Error.WriteLine("⚠ 存在未还原的条目 —— 卸载流程**必须中止**，否则这些程序将永久失去自启动且用户毫不知情。");
            }

            exitCode = outcome.ExitCode;
        }
        catch (Exception ex)
        {
            // 🔴 卸载路径（D22）：本命令由 Inno 卸载器 `ShellExec('runas', ...)` 拉起，
            // **拿不到退出码**，只能回读 `--result-file` 指定的结果文件；
            // 文件不存在 → 卸载器轮询 60s 超时 → 走"无结果文件"分支。
            // 也就是说"出错"绝不能表达成"没写文件"，必须表达成"写了一个非 0 的码"，
            // 否则 D22「非 0 就中止卸载」的保证会在这条路径上悄悄失效。
            Console.Error.WriteLine($"还原过程中发生未预期错误：{ex.Message}");
            Console.Error.WriteLine("⚠ 还原未完成 —— 卸载流程**必须中止**，否则被接管的程序将永久失去自启动。");
            exitCode = ExitFailure;
        }
        finally
        {
            // 🔴 放 finally 而不是"正常路径的最后一行的尾巴"：结果文件的写入是这条
            // 卸载保证的**唯一交付物**，它的存在性不能依赖任何一条代码路径走到最后。
            //
            // 🔴 而"写不出去"必须**改写退出码**：交付失败就是失败。原来写盘失败被静默吞掉，
            // 表现是退出码 0 却没有任何结果文件 —— 卸载器等满 60 秒后问用户
            // "恢复程序没有返回结果"，而日志与控制台一个字都没有（本批次要修的那类静默失败）。
            exitCode = WriteResultFile(services, args, exitCode);
        }

        return exitCode;
    }

    /// <summary>
    /// 把退出码写进 <c>--result-file &lt;路径&gt;</c> 指定的文件（D61 / D144）。
    /// </summary>
    /// <param name="services">共享容器，用于取交换目录与日志接收端。</param>
    /// <param name="args">原始命令行。</param>
    /// <param name="exitCode">本次执行的退出码。</param>
    /// <returns>结果文件**成功交付**时的退出码，否则失败码。</returns>
    /// <remarks>
    /// 供"以提升权限拉起、拿不到退出码"的调用方（Inno 卸载器）回读。
    /// 文件内容就是十进制整数（无换行），卸载器按 <c>StrToIntDef</c> 解析。
    /// <para>
    /// 🔴 没传 <c>--result-file</c> 时原样返回退出码（手工跑 <c>--restore-all</c> 的正常情形）；
    /// 传了却写不成，**一律判失败并留下痕迹**（stderr + 日志），绝不静默吞掉：
    /// 这个文件是卸载保证的唯一交付物，"没交出去"与"还原成功"对卸载器是同一种输入 ——
    /// 都会让它把一次没做完的还原当成成功，而用户将永久失去被接管自启动项的还原入口（D22）。
    /// </para>
    /// <para>
    /// 写盘本身的两道闸（白名单 + 独占创建）在
    /// <see cref="RestoreResultFilePolicy"/> 里 —— 放在 Core 是因为 App 层没有测试工程，
    /// 判定逻辑必须落在能单测的那一层（design.md §7.1）。
    /// </para>
    /// </remarks>
    private static int WriteResultFile(CliServices services, string[] args, int exitCode)
    {
        // 只在本方法用到，就近声明（选项名与 iss 的 InitializeUninstall 必须同步）。
        const string ResultFileOption = "--result-file";

        var index = Array.FindIndex(
            args,
            arg => string.Equals(arg, ResultFileOption, StringComparison.OrdinalIgnoreCase));

        // 约定：选项后面紧跟路径；缺路径就当没传过。
        if (index < 0 || index + 1 >= args.Length)
        {
            return exitCode;
        }

        var path = args[index + 1];
        var tempRoot = PathService.TempRoot;

        if (!RestoreResultFilePolicy.IsAllowedPath(path, tempRoot))
        {
            // 🔴 拒绝时**绝不**改写到"安全位置"：悄悄换个地方写，卸载器就永远等不到这个文件，
            //    而现场看不出任何异样 —— 那正是本批次要消灭的静默失败。
            //    两道闸：落在 %TEMP% 之下 + 文件名形如 restore-<数字>-<数字>.txt。
            ReportResultFileFailure(
                services,
                $"⚠ 结果文件路径不被接受，拒绝写入：{path}" +
                $"（允许范围：{tempRoot} 之下、且文件名形如 restore-<数字>-<数字>.txt）。"
                + "卸载流程拿不到结果将中止卸载。");
            return ExitFailure;
        }

        try
        {
            // 🔴 独占创建（FileMode.CreateNew）：文件已存在即失败，不覆盖。
            //    隐式覆盖会把"路径受控"这一层保障架空 —— 同用户的中完整性进程抢先放好一个
            //    填着 0 的同名文件，就能让卸载器读到"还原成功"而放行。
            RestoreResultFilePolicy.WriteExclusive(path, exitCode.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportResultFileFailure(services, $"⚠ 结果文件写入失败：{path} —— {ex.Message}");
            return ExitFailure;
        }

        return exitCode;
    }

    /// <summary>
    /// 报告结果文件交付失败：控制台与日志**各留一条**（硬约束 7：失败必须可见）。
    /// </summary>
    /// <param name="services">共享容器。</param>
    /// <param name="message">失败原因，中文。</param>
    /// <remarks>
    /// 写日志自己也不能抛：日志落盘失败若在这里冒出去，会盖掉 <c>finally</c> 里真正的退出码，
    /// 把一条"结果文件没写成"变成一条来路不明的崩溃。
    /// </remarks>
    private static void ReportResultFileFailure(CliServices services, string message)
    {
        Console.Error.WriteLine(message);

        try
        {
            services.Log.Error(message);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"⚠ 日志写入也失败了（{ex.Message}）—— 原因以本条控制台输出为准。");
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
        Console.WriteLine("  --restore-all               还原全部接管项并删除调度 / 守卫计划任务（卸载时调用）");
        Console.WriteLine("  --result-file <路径>         把退出码写到该文件（供提权拉起方回读，见 D61）；");
        Console.WriteLine("                              路径必须位于 %TEMP%\\DelayStart\\ 之下且文件不得预先存在");
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
