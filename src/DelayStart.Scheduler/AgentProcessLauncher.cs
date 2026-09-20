using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Launch;
using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Scheduler;

/// <summary>发往代理的启动命令（JSON 行协议，camelCase，与代理端一致）。</summary>
internal sealed class AgentCommand
{
    /// <summary>命令字：<c>launch</c> / <c>exit</c>。</summary>
    public string Op { get; set; } = string.Empty;

    /// <summary>条目显示名，仅用于代理日志。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>目标路径。</summary>
    public string Exe { get; set; } = string.Empty;

    /// <summary>命令行参数。</summary>
    public string Args { get; set; } = string.Empty;

    /// <summary>工作目录。</summary>
    public string Cwd { get; set; } = string.Empty;
}

/// <summary>代理发来的握手报文（JSON 行协议，camelCase）。令牌配对：校验通过才回执。</summary>
internal sealed class AgentHello
{
    /// <summary>命令字：恒为 <c>hello</c>。</summary>
    public string Op { get; set; } = "hello";

    /// <summary>代理启动时经命令行收到的会话令牌（D39 配对凭据）。</summary>
    public string Token { get; set; } = string.Empty;
}

/// <summary>代理回传的执行结果 / 握手回执（JSON 行协议，camelCase）。</summary>
internal sealed class AgentResult
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; set; }

    /// <summary>目标进程 ID；拿不到（UWP）为 <see langword="null"/>。</summary>
    public int? Pid { get; set; }

    /// <summary>失败原因。</summary>
    public string? Error { get; set; }
}

/// <summary>AOT 安全的 JSON 源生成上下文。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AgentHello))]
[JsonSerializable(typeof(AgentCommand))]
[JsonSerializable(typeof(AgentResult))]
internal sealed partial class AgentJsonContext : JsonSerializerContext;

/// <summary>调度端对代理生命周期的控制（退出信号）。</summary>
internal interface IAgentControl
{
    /// <summary>通知代理退出。代理收不到时也会因管道断开自行退出（双保险）。</summary>
    void Shutdown();
}

/// <summary>
/// 调度端对代理的预热编排（D39，2026-09-20 用户批复）：调度开始时若存在普通用户条目，
/// **立即**经 explorer 委托拉起代理，不等首条普通条目到点。
/// </summary>
internal interface IAgentOrchestrator
{
    /// <summary>按本次调度的条目清单预热代理（无普通条目则不拉起）。</summary>
    void PreWarm(IReadOnlyList<DelayedItem> items);
}

/// <summary>
/// 经**普通用户代理**（<c>DelayStart.Agent.exe</c>，D38/D39）启动目标进程的实现。
/// </summary>
/// <remarks>
/// <para>
/// 路由规则：管理员条目（<see cref="DelayedItem.RunAsAdmin"/>）继承调度端提权令牌直接启动；
/// 其余一律经命名管道发给代理 —— 代理以普通用户身份运行，<c>Process.Start</c> 天然降权。
/// 🔴 降权链上**没有回退**：代理不可达即判失败（D20 红线），绝不提权启动普通条目。
/// </para>
/// <para>
/// <b>通信拓扑（D39）</b>：**调度端是管道服务端**（<c>DelayStart.AgentLink</c>），
/// 代理是客户端 —— 代理启动后主动连接、报 hello（携带配对令牌）、等回执，双方进入就绪。
/// 调度端每次运行生成随机令牌，经**会话令牌文件**一次性交接给代理（explorer 不转发
/// 命令行参数，见 <see cref="SpawnAgentViaExplorer"/>）；握手时校验，拒绝无主连接。
/// 🔴 两端都**不加** <c>PipeOptions.CurrentUserOnly</c>：该标志跨提权/非提权边界校验必失败
/// （2026-09-20 真机实锤），会话隔离（Local 命名空间）+ 令牌配对承担安全职责。
/// </para>
/// <para>
/// <b>预热与拉起重试</b>：调度开始即预热（有普通条目时）；首条普通条目启动时若未就绪，
/// 等待最多 <see cref="AgentReadyThreshold"/> 并轮询代理进程存在；超时**杀掉代理重新拉起**，
/// 共尝试 2 次，仍失败则本轮所有普通条目判「Agent 拉起失败」（不再逐条重试拉起）。
/// </para>
/// <para>
/// 🔴 拉起代理必须**经 explorer.exe 委托**（explorer 以普通用户身份运行，由它启动的代理
/// 不继承提权令牌）—— 直接 <c>Process.Start</c> 会让代理继承调度端的提权令牌，D20 违规。
/// </para>
/// </remarks>
internal sealed class AgentProcessLauncher : IProcessLauncher, IAgentControl, IAgentOrchestrator, IDisposable
{
    private const string PipeName = "DelayStart.AgentLink";

    /// <summary>代理进程名（不带扩展名，<see cref="Process.GetProcessesByName(string)"/> 语义）。</summary>
    private const string AgentProcessName = "DelayStart.Agent";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>单次拉起后等待代理完成握手的阈值（用户批复：做一个最长时间的阈值）。20 秒覆盖慢机器。</summary>
    private static readonly TimeSpan AgentReadyThreshold = TimeSpan.FromSeconds(20);

    /// <summary>就绪轮询间隔（同时检查代理进程是否还活着）。</summary>
    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>代理拉起总尝试次数（预热算第 1 次 + 杀掉重拉 1 次，用户批复：尝试 2 次）。</summary>
    private const int MaxSpawnAttempts = 2;

    private readonly PathService _paths;
    private readonly ILogSink _log;

    /// <summary>本会话配对令牌。每次调度端进程启动重新生成，经令牌文件一次性下发给代理。</summary>
    private readonly string _sessionToken = Guid.NewGuid().ToString("N");

    /// <summary>共享状态门：管道与就绪标志由后台握手线程写、消息循环线程读。</summary>
    private readonly object _gate = new();

    private NamedPipeServerStream? _server;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _ready;
    private bool _unavailable;
    private Thread? _acceptThread;
    private volatile bool _stopping;

    /// <summary>构造代理启动器并生成会话令牌。</summary>
    public AgentProcessLauncher(PathService paths, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);

        _paths = paths;
        _log = log;
    }

    /// <inheritdoc />
    public LaunchOutcome Launch(DelayedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (string.IsNullOrWhiteSpace(item.Path))
        {
            return LaunchOutcome.Failure("条目没有目标路径。");
        }

        if (item.RunAsAdmin)
        {
            return LaunchDirect(item);
        }

        return LaunchViaAgent(item);
    }

    /// <inheritdoc />
    public void PreWarm(IReadOnlyList<DelayedItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        // 每轮调度重置：上一轮的"不可用"结论不带入（用户重试场景）。
        lock (_gate)
        {
            _unavailable = false;
        }

        var needsAgent = false;
        foreach (var item in items)
        {
            if (!item.RunAsAdmin && !string.IsNullOrWhiteSpace(item.Path))
            {
                needsAgent = true;
                break;
            }
        }

        if (!needsAgent)
        {
            return;
        }

        _log.Info("检测到普通用户条目，预热拉起普通用户代理。");
        StartAccepting();
        SpawnAgentViaExplorer();
    }

    /// <inheritdoc />
    public void Shutdown()
    {
        _stopping = true;

        try
        {
            if (_reader is not null && _writer is not null)
            {
                var exit = JsonSerializer.Serialize(
                    new AgentCommand { Op = "exit" },
                    AgentJsonContext.Default.AgentCommand);
                _writer.WriteLine(exit);
                _ = _reader.ReadLine(); // 收回执；代理也会因断开退出，双保险
                _log.Info("已通知普通用户代理退出。");
            }
        }
        catch (Exception ex)
        {
            _log.Warn(ex, "发送代理退出信号失败（代理会因管道断开自行退出）。");
        }
        finally
        {
            ResetConnection();
        }
    }

    /// <summary>释放管道资源（等价于发退出信号后断开 —— CA1001）。</summary>
    public void Dispose()
    {
        Shutdown();
    }

    /// <summary>管理员条目：继承调度端提权令牌直接启动（有意提权，D20 的允许面）。</summary>
    private static LaunchOutcome LaunchDirect(DelayedItem item)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = item.Path,
                Arguments = item.Arguments,
                UseShellExecute = true,
            };

            var workingDirectory = ResolveWorkingDirectory(item);
            if (workingDirectory.Length > 0)
            {
                start.WorkingDirectory = workingDirectory;
            }

            using var process = Process.Start(start);

            int? processId = null;
            try
            {
                if (process is not null)
                {
                    processId = process.Id;
                }
            }
            catch (InvalidOperationException)
            {
                // ShellExecute 启动关联类型时句柄可能拿不到 —— 不影响创建成功这个结论。
            }

            return LaunchOutcome.Success(processId);
        }
        catch (Exception ex)
        {
            return LaunchOutcome.Failure($"创建进程失败：{ex.Message}");
        }
    }

    /// <summary>普通条目：经代理启动（D38/D39）。失败没有提权回退。</summary>
    private LaunchOutcome LaunchViaAgent(DelayedItem item)
    {
        try
        {
            lock (_gate)
            {
                if (_unavailable)
                {
                    return LaunchOutcome.Failure("Agent 拉起失败（本轮已放弃），普通用户条目全部判失败。");
                }
            }

            if (!IsReady && !EnsureReadyBlocking())
            {
                lock (_gate)
                {
                    _unavailable = true;
                }

                var message = $"Agent 拉起失败（已尝试 {MaxSpawnAttempts} 次），本轮所有普通用户条目判失败。";
                _log.Warn(message);
                return LaunchOutcome.Failure(message);
            }

            var command = new AgentCommand
            {
                Op = "launch",
                Name = item.Name,
                Exe = item.Path,
                Args = item.Arguments,
                Cwd = ResolveWorkingDirectory(item),
            };

            var result = SendWithReconnect(command);
            return result.Ok
                ? LaunchOutcome.Success(result.Pid)
                : LaunchOutcome.Failure(result.Error ?? "代理未返回失败原因。");
        }
        catch (Exception ex)
        {
            _log.Warn(ex, $"经普通用户代理启动『{item.Name}』失败。");
            return LaunchOutcome.Failure($"普通用户代理不可用：{ex.Message}");
        }
    }

    /// <summary>代理握手是否已完成（就绪）。</summary>
    private bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _ready;
            }
        }
    }

    private AgentResult SendWithReconnect(AgentCommand command)
    {
        try
        {
            return SendOnce(command);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or EndOfStreamException or JsonException)
        {
            _log.Warn($"与代理的管道断开（{ex.Message}），重建连接后重试一次。");
            ResetConnection();

            if (!EnsureReadyBlocking())
            {
                lock (_gate)
                {
                    _unavailable = true;
                }

                throw new IOException("Agent 拉起失败（重建连接失败）。");
            }

            return SendOnce(command);
        }
    }

    private AgentResult SendOnce(AgentCommand command)
    {
        StreamReader? reader;
        StreamWriter? writer;
        lock (_gate)
        {
            reader = _reader;
            writer = _writer;
        }

        if (reader is null || writer is null)
        {
            throw new InvalidOperationException("代理连接不可用。");
        }

        var line = JsonSerializer.Serialize(command, AgentJsonContext.Default.AgentCommand);
        writer.WriteLine(line);

        var response = reader.ReadLine()
            ?? throw new EndOfStreamException("代理已断开连接，未返回结果。");

        var result = JsonSerializer.Deserialize(response, AgentJsonContext.Default.AgentResult)
            ?? throw new EndOfStreamException("代理返回空结果。");

        return result;
    }

    /// <summary>
    /// 等待代理就绪：阈值内轮询握手状态与代理进程存活；超时杀掉进程重新拉起。
    /// 共尝试 <see cref="MaxSpawnAttempts"/> 次（预热/前一次重拉算一次）。
    /// </summary>
    private bool EnsureReadyBlocking()
    {
        for (var attempt = 1; attempt <= MaxSpawnAttempts; attempt++)
        {
            if (attempt > 1)
            {
                StartAccepting();
                SpawnAgentViaExplorer();
            }

            if (WaitForReady(AgentReadyThreshold))
            {
                if (attempt > 1)
                {
                    _log.Info("重新拉起的代理已就绪。");
                }

                return true;
            }

            _log.Warn($"第 {attempt}/{MaxSpawnAttempts} 次拉起的代理在 "
                + $"{AgentReadyThreshold.TotalSeconds:0} 秒内未完成握手。");
            KillAgentProcesses();
        }

        return IsReady;
    }

    /// <summary>
    /// 阈值内轮询就绪状态；代理进程消失（被拒绝/自毁/被杀）提前返回 <see langword="false"/>。
    /// </summary>
    private bool WaitForReady(TimeSpan threshold)
    {
        var deadline = DateTime.UtcNow + threshold;

        while (DateTime.UtcNow < deadline)
        {
            if (IsReady)
            {
                return true;
            }

            if (!AgentProcessExists())
            {
                _log.Warn("代理进程已消失（可能握手失败自毁或被外部终止），提前结束本轮等待。");
                return false;
            }

            Thread.Sleep(ReadyPollInterval);
        }

        return IsReady;
    }

    /// <summary>启动握手接受线程（幂等：线程还活着就不再起）。</summary>
    private void StartAccepting()
    {
        lock (_gate)
        {
            if (_acceptThread is { IsAlive: true })
            {
                return;
            }

            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "DelayStart.AgentAccept",
            };
            _acceptThread.Start();
        }
    }

    /// <summary>
    /// 握手接受循环：创建服务端管道 → 等代理连接 → 校验 hello 令牌 → 回执 → 交出管道。
    /// 令牌不匹配的连接直接拒绝（代理会自行退出）；就绪或停止后线程结束。
    /// </summary>
    private void AcceptLoop()
    {
        while (!_stopping)
        {
            var server = CreateServerPipe();

            lock (_gate)
            {
                _server = server;
            }

            try
            {
                // 60 秒无连接则重建管道再来一轮（代理自毁重拉的间隙）。
                if (!server.WaitForConnectionAsync().Wait(TimeSpan.FromSeconds(60)))
                {
                    server.Dispose();
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                server.Dispose();
                continue;
            }

            if (_stopping)
            {
                server.Dispose();
                return;
            }

            if (TryHandshake(server))
            {
                return; // 管道已交接，本线程功成身退
            }
        }
    }

    /// <summary>
    /// 创建服务端管道。
    /// 🔴 **必须显式放行当前交互用户**：提权进程创建的管道默认 DACL 只含
    /// Administrators/SYSTEM 等组，本用户的**非提权**令牌连接会报
    /// <c>Access to the path is denied</c>（2026-09-20 真机实锤，v43 复测）。
    /// 调度端与代理是同一登录用户（仅完整性级别不同），授予该用户 SID 完全控制即可。
    /// </summary>
    private static NamedPipeServerStream CreateServerPipe()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("无法取得当前用户 SID，无法配置管道 ACL。");
        security.AddAccessRule(new PipeAccessRule(
            user,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // 🔴 现代 .NET 里带 ACL 的管道必须走 NamedPipeServerStreamAcl.Create 工厂
        // （构造函数重载在 .NET Core 后被移除，CS1729）；参数全部位置传参最稳。
        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security);
    }

    /// <summary>校验握手报文并回执；成功时把管道登记为会话通道并置就绪。</summary>
    private bool TryHandshake(NamedPipeServerStream server)
    {
        try
        {
            using var handshakeReader = new StreamReader(server, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            using var handshakeWriter = new StreamWriter(server, Utf8NoBom, bufferSize: 1024, leaveOpen: true) { AutoFlush = true };

            var helloTask = handshakeReader.ReadLineAsync();
            if (!helloTask.Wait(TimeSpan.FromSeconds(5)) || helloTask.Result is null)
            {
                _log.Warn("代理已连接但握手报文超时，拒绝该连接。");
                return false;
            }

            var hello = JsonSerializer.Deserialize(helloTask.Result, AgentJsonContext.Default.AgentHello);
            if (hello is null)
            {
                _log.Warn("代理握手报文为空，拒绝该连接。");
                return false;
            }

            if (!string.Equals(hello.Token, _sessionToken, StringComparison.Ordinal))
            {
                _log.Warn("代理握手令牌与本次会话不匹配（疑似无主/过期代理），拒绝该连接。");
                return false;
            }

            // 校验通过：回执（leaveOpen 保住底层流），随后把所有权移交给会话。
            handshakeWriter.WriteLine(JsonSerializer.Serialize(
                new AgentResult { Ok = true },
                AgentJsonContext.Default.AgentResult));

            lock (_gate)
            {
                _reader = new StreamReader(server, Utf8NoBom);
                _writer = new StreamWriter(server, Utf8NoBom) { AutoFlush = true };
                _ready = true;
            }

            _log.Info("普通用户代理握手成功，进入就绪状态。");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn(ex, "代理握手处理失败，拒绝该连接。");
            return false;
        }
    }

    /// <summary>代理进程当前是否存在（按进程名检查，用户批复的周期性检查语义）。</summary>
    private static bool AgentProcessExists()
    {
        var processes = Process.GetProcessesByName(AgentProcessName);
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Length > 0;
    }

    /// <summary>杀掉所有代理进程（重新拉起前的清理，用户批复语义）。</summary>
    private void KillAgentProcesses()
    {
        var processes = Process.GetProcessesByName(AgentProcessName);
        if (processes.Length == 0)
        {
            return;
        }

        foreach (var process in processes)
        {
            try
            {
                process.Kill(entireProcessTree: false);
                _log.Warn($"已终止未就绪的代理进程：PID {process.Id}。");
            }
            catch (Exception ex)
            {
                _log.Warn(ex, $"终止代理进程 PID {process.Id} 失败（可能已退出）。");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// 经 explorer.exe 委托拉起代理 —— 外壳以普通用户身份运行，代理不继承提权令牌。
    /// 🔴 配对令牌**不能放在命令行**：explorer.exe 不转发参数，带参调用会被它当成
    /// "打开第二个路径"而整体失败（2026-09-20 真机实锤：Agent 一行日志都没有）。
    /// 改为把令牌写进会话文件，Agent 启动时读取并立即删除（一次性交接）。
    /// </summary>
    private void SpawnAgentViaExplorer()
    {
        var agent = _paths.AgentExecutablePath;
        if (!File.Exists(agent))
        {
            throw new InvalidOperationException($"代理程序不存在：{agent}。请确认安装完整。");
        }

        // 令牌文件写在委托**之前** —— Agent 启动即读，不能有竞态窗口。
        var tokenFile = _paths.AgentTokenFilePath;
        Directory.CreateDirectory(_paths.LocalRoot);
        File.WriteAllText(tokenFile, _sessionToken, Encoding.UTF8);
        _log.Info("已签发代理配对令牌（会话文件一次性交接）。");

        var explorer = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe");

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = explorer,
            Arguments = $"\"{agent}\"",
            UseShellExecute = false,
        });

        _log.Info($"已经 explorer.exe 委托拉起普通用户代理：{agent}");
    }

    private void ResetConnection()
    {
        lock (_gate)
        {
            _reader?.Dispose();
            _writer?.Dispose();
            _server?.Dispose();
            _reader = null;
            _writer = null;
            _server = null;
            _ready = false;
        }
    }

    private static string ResolveWorkingDirectory(DelayedItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.WorkingDirectory))
        {
            return item.WorkingDirectory;
        }

        var directory = Path.GetDirectoryName(item.Path);
        return string.IsNullOrEmpty(directory) ? string.Empty : directory;
    }
}
