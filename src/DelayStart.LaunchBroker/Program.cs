using System.Runtime.InteropServices;
using System.Text.Json;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Launch;
using DelayStart.Core.Logging;
using DelayStart.Core.Serialization;
using DelayStart.Core.Services;

namespace DelayStart.LaunchBroker;

/// <summary>
/// UIAccess 中转器（D70，2026-09-21 用户批复）—— 降权链的第二跳。
/// </summary>
/// <remarks>
/// <para>
/// <b>链路</b>：调度端 A（High）写作业 JSON → 经外壳令牌 + CPWT 降权拉起本程序 B
/// （Medium）→ B 读作业文件，对目标 C（asInvoker + uiAccess="true"）调
/// <c>ShellExecuteExW</c> → AppInfo 校验（签名 + 安全位置 + 清单声明）通过后给 C
/// 赋 UIAccess 标志、按调用方身份抬 IL → B 把 <b>C 的</b>启动状态回写结果文件。
/// </para>
/// <para>
/// 🔴 为什么目标必须经 ShellExecute：<c>TokenUIAccess</c> 需要 SeTcbPrivilege（仅 SYSTEM），
/// AppInfo（RAiLaunchAdminProcess）是用户侧唯一认可通道 —— 普通 CreateProcess 创建目标
/// 时 UIAccess 标志静默丢失（辅助功能降级），CPWT/AsUser 直接 740。
/// </para>
/// <para>
/// 🔴 回写的是<b>目标 C</b> 的状态，不是 B 自己的：B 的退出码只表达"结果有没有回写成功"
/// （0=已回写；2=作业文件不可读；3=回写失败），目标成败全在结果 JSON 里。
/// </para>
/// <para>
/// 日志：<c>%LOCALAPPDATA%\DelayStart\logs\launchbroker.log</c>（滚动策略与调度端一致），
/// 记录作业接收、目标启动、结果回写全过程；日志失败绝不影响启动主流程。
/// </para>
/// </remarks>
internal static class Program
{
    private const int ExitResultDelivered = 0;
    private const int ExitBadJob = 2;
    private const int ExitResultWriteFailed = 3;

    private const uint SeeMaskNoCloseProcess = 0x0000_0040;
    private const uint SeeMaskNoAsync = 0x0000_1000;
    private const int ShowNormal = 1;
    private const uint WaitObject0 = 0;
    private const uint WaitFailed = 0xFFFF_FFFF;

    private static int Main(string[] args)
    {
        // 日志最先建立：无参数（疑似手动双击）也要留下痕迹。
        var log = new FileLogger(new PathService().BrokerLogPath, "LaunchBroker", SystemClock.Instance);

        if (args.Length != 1)
        {
            log.Warn($"参数数量为 {args.Length}（期望 1 个作业文件路径），疑似手动双击启动 —— 退出码 {ExitBadJob}。");
            return ExitBadJob;
        }

        BrokerLaunchJob? job;
        try
        {
            var json = File.ReadAllText(args[0]);
            job = JsonSerializer.Deserialize(json, BrokerJsonContext.Default.BrokerLaunchJob);
        }
        catch (Exception ex)
        {
            // 结果文件路径在作业里 —— 作业读不出来就无处回写，只能用退出码报错。
            log.Error(ex, $"读取作业文件失败：{args[0]} —— 退出码 {ExitBadJob}。");
            return ExitBadJob;
        }

        if (job is null
            || string.IsNullOrWhiteSpace(job.ResultFile)
            || string.IsNullOrWhiteSpace(job.Target))
        {
            log.Warn("作业内容无效（缺 Target / ResultFile）—— 退出码 " + ExitBadJob + "。");
            return ExitBadJob;
        }

        log.Info(
            $"收到作业：Target={job.Target}；Arguments={job.Arguments}；"
            + $"WorkingDirectory={job.WorkingDirectory}；WaitTimeoutMs={job.WaitTimeoutMs}。");

        // 🔴 shell 函数要求调用线程初始化 STA COM：MSDN ShellExecuteEx Remarks 明确要求
        // CoInitializeEx(NULL, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE)；
        // 未初始化/MTA 时 ShellExecuteEx 已知报 ERROR_ACCESS_DENIED(5)
        //（Raymond Chen "One possible reason why ShellExecute returns SE_ERR_ACCESSDENIED" + KB287087）。
        // uiAccess 目标的启动握手走 COM 激活路径，必踩。
        var comInitialized = NativeMethods.CoInitializeEx(0, NativeMethods.CoinitApartmentThreaded | NativeMethods.CoinitDisableOle1Dde) >= 0;

        var result = Launch(job, log);

        try
        {
            // 先写 .tmp 再原子改名：调度端轮询到结果文件名出现即可安全读取，不会读到半截。
            var tempFile = job.ResultFile + ".tmp";
            File.WriteAllText(
                tempFile,
                JsonSerializer.Serialize(result, BrokerJsonContext.Default.BrokerLaunchResult));
            File.Move(tempFile, job.ResultFile, overwrite: true);
            log.Info($"结果已回写：Ok={result.Ok}；ProcessId={FormatNullable(result.ProcessId)}；Win32Error={FormatNullable(result.Win32Error)}；ResultFile={job.ResultFile}。");
            return ExitResultDelivered;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"结果回写失败：{job.ResultFile} —— 退出码 {ExitResultWriteFailed}。");
            return ExitResultWriteFailed;
        }
        finally
        {
            if (comInitialized)
            {
                NativeMethods.CoUninitialize();
            }
        }
    }

    /// <summary>对目标执行 ShellExecuteEx 并尽量取回 PID / 秒退信息。本方法不抛异常。</summary>
    private static BrokerLaunchResult Launch(BrokerLaunchJob job, ILogSink log)
    {
        // 🔴 LibraryImport 源生成器只支持 blittable 结构（SYSLIB1051）：
        // ShellExecuteInfoW 的字符串字段全部用 nint 指针，字符串本体在下面用 fixed 钉住。
        var parameters = NullIfEmpty(job.Arguments);
        var directory = NullIfEmpty(job.WorkingDirectory);

        unsafe
        {
            fixed (char* filePointer = job.Target)
            fixed (char* parametersPointer = parameters)
            fixed (char* directoryPointer = directory)
            {
                // 🔴 ABI 自检：x64 下 sizeof 必须是 112（含 dwHotKey 后的 union）。
                // 不对就拒绝调用 —— 布局错 = cbSize 错 + 原生越过缓冲区写 hProcess。
                var structSize = sizeof(NativeMethods.ShellExecuteInfoW);
                if (Environment.Is64BitProcess && structSize != 112)
                {
                    log.Error($"ABI 自检失败：SHELLEXECUTEINFOW sizeof={structSize}（期望 112），拒绝调用 ShellExecuteExW。");
                    return new BrokerLaunchResult
                    {
                        Ok = false,
                        Win32Error = 87, // ERROR_INVALID_PARAMETER —— 语义上就是"参数块不合法"
                        Message = $"SHELLEXECUTEINFOW 布局自检失败（sizeof={structSize}，期望 112），拒绝调用。",
                    };
                }

                var info = new NativeMethods.ShellExecuteInfoW
                {
                    CbSize = (uint)structSize,
                    FMask = SeeMaskNoCloseProcess | SeeMaskNoAsync,
                    Hwnd = 0,
                    LpVerb = 0, // 默认动词 open
                    LpFile = (nint)filePointer,
                    LpParameters = (nint)parametersPointer,
                    LpDirectory = (nint)directoryPointer,
                    NShow = ShowNormal,
                };

                if (!NativeMethods.ShellExecuteExW(ref info))
                {
                    var error = Marshal.GetLastWin32Error();
                    log.Warn(
                        $"ShellExecuteExW 失败：Win32Error={error} (0x{error:X8})；"
                        + $"HInstApp=0x{info.HInstApp:X}{DescribeSeErr(info.HInstApp)}；Target={job.Target}。");

                    var message = $"ShellExecuteEx 失败，Win32Error={error}";
                    if (error == 740)
                    {
                        message += "（740 = ERROR_ELEVATION_REQUIRED：目标要求提权或清单校验未过）";
                    }
                    else if (info.HInstApp is >= 0 and <= 32)
                    {
                        message += $"（hInstApp={DescribeSeErr(info.HInstApp).Trim('（', '）')}）";
                    }

                    return new BrokerLaunchResult
                    {
                        Ok = false,
                        Win32Error = error,
                        Message = message,
                    };
                }

                return CollectProcessStatus(info.HProcess, job, log);
            }
        }
    }

    /// <summary>ShellExecuteEx 失败时 hInstApp 可能携带 SE_ERR_*（0..32）；其余值无意义。</summary>
    private static string DescribeSeErr(nint value)
        => value is >= 0 and <= 32
            ? value switch
            {
                0 => "（SE_ERR_OUT_OF_MEMORY）",
                2 => "（SE_ERR_FILE_NOT_FOUND）",
                3 => "（SE_ERR_PATH_NOT_FOUND）",
                5 => "（SE_ERR_ACCESS_DENIED）",
                26 => "（SE_ERR_DLL_NOT_FOUND）",
                27 => "（SE_ERR_SHARE）",
                28 => "（SE_ERR_ASSOCINCOMPLETE）",
                29 => "（SE_ERR_DDETIMEOUT）",
                30 => "（SE_ERR_DDEFAIL）",
                31 => "（SE_ERR_NOASSOC）",
                _ => "（SE_ERR_*）",
            }
            : string.Empty;

    /// <summary>句柄有效则取 PID 并做秒退检测；外壳/DDE 激活拿不到句柄时判创建成功但 PID 未知。</summary>
    private static BrokerLaunchResult CollectProcessStatus(nint hProcess, BrokerLaunchJob job, ILogSink log)
    {
        if (hProcess == 0)
        {
            log.Info("目标经外壳/DDE 激活，进程句柄不可用（PID 未知）。");
            return new BrokerLaunchResult
            {
                Ok = true,
                Message = "目标经外壳/DDE 激活，进程句柄不可用（PID 未知）。",
            };
        }

        try
        {
            var pid = NativeMethods.GetProcessId(hProcess);
            var wait = NativeMethods.WaitForSingleObject(hProcess, (uint)Math.Max(0, job.WaitTimeoutMs));

            if (wait == WaitObject0)
            {
                // 🔴 `Ok` 只表达"进程确实被创建出来"（CreateProcess 语义），**不表达运行成败** ——
                // 目标成败全在这份结果 JSON 里，由调度端判定（见类注释）。
                // 这里必须把**真实退出码**如实带出去：调度端拿它区分"显式非零退出码 = 启动失败"
                // 与"退出码 0 的秒退 = 正常"（E4）。
                // 🔴 读不到退出码时要**留 null**（= 未知），绝不能兜成 0：那会被调度端
                // 当成"成功退出"，而实际是"不知道"—— 两者在 E4 下都判成功，但日志里必须能分开，
                // 否则一次读取失败就永久消失在"退出码 0"里。
                uint? exitCode = null;
                if (NativeMethods.GetExitCodeProcess(hProcess, out var readExitCode))
                {
                    exitCode = readExitCode;
                }
                else
                {
                    log.Error($"目标进程 PID {pid} 已退出，但读取退出码失败（Win32Error={Marshal.GetLastWin32Error()}）："
                        + "按退出码未知上报，调度端将走 E4 宽容路径并在日志中标注未确认。");
                }

                var exitCodeText = exitCode is { } code ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "未知";
                log.Warn($"目标进程 PID {pid} 在 {job.WaitTimeoutMs} ms 等待窗口内自行退出（exitCode={exitCodeText}）。");
                return new BrokerLaunchResult
                {
                    Ok = true,
                    ProcessId = (int)pid,
                    ExitedImmediately = true,
                    ExitCode = exitCode,
                    Message = $"目标在 {job.WaitTimeoutMs} ms 等待窗口内自行退出（exitCode={exitCodeText}）。",
                };
            }

            if (wait == WaitFailed)
            {
                // 等待失败只影响"秒退检测"，不影响"创建成功"这个结论。
                log.Warn($"秒退检测等待失败（目标 PID {pid}），未确认是否仍在运行。");
                return new BrokerLaunchResult
                {
                    Ok = true,
                    ProcessId = (int)pid,
                    Message = "目标运行中（秒退检测等待失败，未确认是否仍在运行）。",
                };
            }

            log.Info($"目标启动成功：PID {pid}（{job.WaitTimeoutMs} ms 内未退出，判定运行中）。");
            return new BrokerLaunchResult
            {
                Ok = true,
                ProcessId = (int)pid,
            };
        }
        finally
        {
            _ = NativeMethods.CloseHandle(hProcess);
        }
    }

    private static string? NullIfEmpty(string value)
        => string.IsNullOrEmpty(value) ? null : value;

    private static string FormatNullable(int? value)
        => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "无";
}

/// <summary>中转器自身需要的最小 Win32 面（shell32 / ole32 / kernel32）。</summary>
internal static partial class NativeMethods
{
    /// <summary>
    /// SHELLEXECUTEINFOW。🔴 字符串字段一律 nint（LibraryImport 只支持 blittable 结构，
    /// SYSLIB1051）。🔴 布局必须与 C 版完全一致：**dwHotKey 之后有 union{hIcon;hMonitor;}**，
    /// 之后才是 hProcess —— x64 下 sizeof = 112。缺联合体的旧布局是 104：cbSize 错、
    /// hProcess 偏移错（SEE_MASK_NOCLOSEPROCESS 下原生写 104..111，越过 104 字节缓冲）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ShellExecuteInfoW
    {
        public uint CbSize;
        public uint FMask;
        public nint Hwnd;
        public nint LpVerb;
        public nint LpFile;
        public nint LpParameters;
        public nint LpDirectory;
        public int NShow;
        public nint HInstApp;
        public nint LpIDList;
        public nint LpClass;
        public nint HkeyClass;
        public uint DwHotKey;

        /// <summary>union { HANDLE hIcon; HANDLE hMonitor; }（SEE_MASK_FLAG_HMONITOR 才用）。</summary>
        public nint HIconOrMonitor;

        /// <summary>真正的 hProcess（SEE_MASK_NOCLOSEPROCESS 下由系统回填）。</summary>
        public nint HProcess;
    }

    [LibraryImport("shell32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShellExecuteExW(ref ShellExecuteInfoW info);

    internal const uint CoinitApartmentThreaded = 0x2;      // COINIT_APARTMENTTHREADED
    internal const uint CoinitDisableOle1Dde = 0x4;         // COINIT_DISABLE_OLE1DDE

    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint GetProcessId(nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);
}
