using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace DelayStart.Agent;

/// <summary>
/// 普通用户代理进程入口（D38 双进程方案 + D39 握手协议，2026-09-20 用户批复）。
/// </summary>
/// <remarks>
/// <para>
/// <b>职责边界</b>：代理只做一件事 —— 收到一条启动命令后，用**自身普通用户令牌**
/// 把目标程序拉起来，把成败写回管道。没有任何令牌操作（提权/降权都不存在：
/// 代理由调度端经 explorer.exe 委托拉起，继承的是外壳的普通用户令牌）。
/// </para>
/// <para>
/// <b>启动条件</b>：必须拿到调度端本次会话签发的配对令牌，否则退出 ——
/// 防止用户手动双击运行出一个无主的代理。获取顺序：① 命令行
/// <c>--token=...</c>（预留的直接拉起通道）；② 会话令牌文件（D39 修订，
/// **explorer.exe 不转发命令行参数**，带参委托调用会被整体拒绝 —— 真机实锤，
/// 故经调度端写入的令牌文件一次性交接，Agent 读取后立即删除）。
/// 令牌同时是通信配对凭据：握手报文携带令牌，调度端校验通过才回执。
/// </para>
/// <para>
/// <b>握手（D39）</b>：代理主动连接调度端的命名管道，发 <c>hello</c> 报文等回执；
/// 1 次发起 + 2 次重试全部失败 → 记日志并**自动结束进程**（防止调度端拉起后报错、
/// 或代理被用户/其他软件遗弃成孤儿）。
/// </para>
/// <para>
/// <b>生命周期</b>：握手成功 → 命令循环（一行命令一行回复）→ 收到 <c>exit</c> 回执后
/// 退出；调度端断开/管道断裂也退出（配对对象已消失，代理没有存在意义）。
/// </para>
/// <para>
/// 🔴 代理**不做成败判定**（进程 1.5 秒后是否还活着由调度端复查），
/// 也不重试 —— 重试额度归调度端（FR-9.6）。
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>命名管道名。**调度端是服务端**，代理是客户端；会话内 Local 命名空间。</summary>
    private const string PipeName = "DelayStart.AgentLink";

    /// <summary>单实例互斥名。重复拉起时后者静默退出（用户批复 2026-09-20：三进程都加互斥）。</summary>
    private const string SingleInstanceMutexName = @"Local\DelayStart.Agent";

    private const string TokenPrefix = "--token=";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>握手总次数 = 1 次发起 + 2 次重试（用户批复 2026-09-20）。</summary>
    private const int MaxHandshakeAttempts = 3;

    private static int Main(string[] args)
    {
        // 🔴 防手动双击：没有调度端签发的令牌就不启动。
        //   命令行参数优先（预留直接拉起通道）；explorer 委托场景下参数被外壳吞掉，
        //   回落到令牌文件 —— 调度端写、代理读后立即删除（一次性交接）。
        var token = ParseToken(args);
        var viaFile = token.Length == 0;
        if (viaFile)
        {
            token = ReadTokenFile();
        }

        if (token.Length == 0)
        {
            Log(viaFile
                ? "会话令牌文件不存在或为空（疑似手动双击启动，或调度端尚未签发），退出。"
                : "命令行缺少 --token 启动参数，退出。");
            return 0;
        }

        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isNew);
        if (!isNew)
        {
            Log("已有代理实例在运行，退出。");
            return 0;
        }

        try
        {
            return Run(token);
        }
        catch (Exception ex)
        {
            Log("代理发生未处理异常：" + ex.Message);
            return 1;
        }
    }

    private static int Run(string token)
    {
        Log("代理已启动，开始连接调度端握手。");

        for (var attempt = 1; attempt <= MaxHandshakeAttempts; attempt++)
        {
            using var pipe = new NamedPipeClientStream(
                serverName: ".",
                PipeName,
                PipeDirection.InOut);

            try
            {
                pipe.Connect((int)ConnectTimeout.TotalMilliseconds);
                using var reader = new StreamReader(pipe, Utf8NoBom);
                using var writer = new StreamWriter(pipe, Utf8NoBom) { AutoFlush = true };

                writer.WriteLine(JsonSerializer.Serialize(
                    new AgentHello { Token = token },
                    AgentJsonContext.Default.AgentHello));

                var ackTask = reader.ReadLineAsync();
                if (!ackTask.Wait(AckTimeout) || ackTask.Result is null)
                {
                    throw new IOException("握手回执超时或连接被关闭。");
                }

                var ack = JsonSerializer.Deserialize(ackTask.Result, AgentJsonContext.Default.AgentResult);
                if (ack is not { Ok: true })
                {
                    throw new IOException($"握手被调度端拒绝：{ack?.Error ?? "未知原因"}。");
                }

                Log("握手成功，进入命令循环。");
                CommandLoop(reader, writer);
                return 0;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
            {
                // UnauthorizedAccessException：提权服务端管道 ACL 未放行本用户时出现（历史坑），
                // 与 IO 失败同等对待 —— 走重试，不整进程崩掉。
                Log($"第 {attempt}/{MaxHandshakeAttempts} 次握手失败：{ex.Message}");
            }

            if (attempt < MaxHandshakeAttempts)
            {
                Thread.Sleep(RetryDelay);
            }
        }

        Log("握手重试全部失败，代理自动退出（调度端可能未就绪、已退出，或拒绝了配对令牌）。");
        return 1;
    }

    /// <summary>命令循环。断开/管道断裂 = 配对的调度端已消失，退出进程。</summary>
    private static void CommandLoop(StreamReader reader, StreamWriter writer)
    {
        while (reader.ReadLine() is { } line)
        {
            if (HandleCommand(line, writer))
            {
                Log("收到退出命令，代理退出。");
                return;
            }
        }

        Log("调度端断开连接，代理退出。");
    }

    /// <summary>处理一条命令；返回是否要求退出。</summary>
    private static bool HandleCommand(string line, StreamWriter writer)
    {
        AgentCommand? command;
        try
        {
            command = JsonSerializer.Deserialize(line, AgentJsonContext.Default.AgentCommand);
        }
        catch (JsonException ex)
        {
            Write(writer, AgentResult.Fail($"无法解析命令：{ex.Message}"));
            return false;
        }

        if (command is null)
        {
            Write(writer, AgentResult.Fail("命令为空。"));
            return false;
        }

        switch (command.Op)
        {
            case "exit":
                Write(writer, AgentResult.Success(null));
                return true;

            case "launch":
                Log($"启动『{command.Name}』：{command.Exe}");
                Write(writer, Launch(command));
                return false;

            default:
                Write(writer, AgentResult.Fail($"未知命令：{command.Op}"));
                return false;
        }
    }

    /// <summary>按目标形态分流启动。全部在代理自身（普通用户）令牌下进行。</summary>
    private static AgentResult Launch(AgentCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Exe))
        {
            return AgentResult.Fail("目标路径为空。");
        }

        try
        {
            var process = Start(command);
            return AgentResult.Success(process is null ? null : PidOrNull(process));
        }
        catch (Exception ex)
        {
            Log($"『{command.Name}』启动失败：{ex.Message}");
            return AgentResult.Fail($"创建进程失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 启动目标。UWP（<c>shell:AppsFolder\…</c>）经 explorer.exe 激活（D28=A，零 COM，
    /// 拿不到目标 PID）；<c>.lnk</c> 交给外壳解析；其余直接 <c>CreateProcess</c>。
    /// </summary>
    private static Process? Start(AgentCommand command)
    {
        var isUwp = command.Exe.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase);
        var isShortcut = command.Exe.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);

        ProcessStartInfo start;
        if (isUwp)
        {
            start = new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "explorer.exe"),
                Arguments = $"\"{command.Exe}\"",
                UseShellExecute = false,
            };
        }
        else
        {
            start = new ProcessStartInfo
            {
                FileName = command.Exe,
                Arguments = command.Args,
                UseShellExecute = isShortcut,
            };

            var workingDirectory = !string.IsNullOrWhiteSpace(command.Cwd)
                ? command.Cwd
                : Path.GetDirectoryName(command.Exe);
            if (!string.IsNullOrEmpty(workingDirectory))
            {
                start.WorkingDirectory = workingDirectory;
            }
        }

        var process = Process.Start(start);
        return process;
    }

    /// <summary>取目标进程 ID；ShellExecute 启动关联类型时句柄可能拿不到，按 <see langword="null"/> 处理。</summary>
    private static int? PidOrNull(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void Write(StreamWriter writer, AgentResult result)
    {
        writer.WriteLine(JsonSerializer.Serialize(result, AgentJsonContext.Default.AgentResult));
    }

    private static string ParseToken(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith(TokenPrefix, StringComparison.Ordinal))
            {
                return arg[TokenPrefix.Length..];
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 读取调度端下发的会话令牌文件并**立即删除**（一次性交接：重复拉起/手动双击
    /// 都拿不到同一份令牌）。文件由调度端（提权）写进用户 Local 目录，继承目录 ACL，
    /// 普通用户代理可读可删。读失败/不存在返回空串，绝不抛异常挡住退出路径。
    /// </summary>
    private static string ReadTokenFile()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DelayStart",
                "agent-session.token");
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            var token = File.ReadAllText(path, Encoding.UTF8).Trim();
            File.Delete(path);
            return token;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>极简文件日志。写失败静默忽略 —— 代理不能因为日志把启动拖垮。</summary>
    private static void Log(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DelayStart",
                "logs");
            Directory.CreateDirectory(directory);

            File.AppendAllText(
                Path.Combine(directory, "agent.log"),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [Agent] {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // 日志失败不影响主流程。
        }
    }
}
