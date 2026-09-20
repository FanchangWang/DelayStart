using System.Text.Json.Serialization;

namespace DelayStart.Agent;

/// <summary>调度端发来的命令（JSON 行协议，一行一条）。</summary>
internal sealed class AgentCommand
{
    /// <summary>命令字：<c>launch</c> = 启动目标程序；<c>exit</c> = 退出代理。</summary>
    public string Op { get; set; } = string.Empty;

    /// <summary>条目显示名，仅用于日志。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>目标路径。<c>.exe</c> 直接启动；<c>.lnk</c> 经外壳解析；
    /// <c>shell:AppsFolder\…</c>（UWP）经 explorer.exe 激活。</summary>
    public string Exe { get; set; } = string.Empty;

    /// <summary>命令行参数，可空。</summary>
    public string Args { get; set; } = string.Empty;

    /// <summary>工作目录，可空（空则用目标所在目录）。</summary>
    public string Cwd { get; set; } = string.Empty;
}

/// <summary>代理回传给调度端的执行结果（JSON 行协议，一行一条）。</summary>
internal sealed class AgentResult
{
    /// <summary>进程是否创建成功。</summary>
    public bool Ok { get; set; }

    /// <summary>目标进程 ID；拿不到（UWP 激活）为 <see langword="null"/>。</summary>
    public int? Pid { get; set; }

    /// <summary>失败原因（中文），成功时为 <see langword="null"/>。</summary>
    public string? Error { get; set; }

    /// <summary>构造成功结果。</summary>
    public static AgentResult Success(int? pid) => new() { Ok = true, Pid = pid };

    /// <summary>构造失败结果。</summary>
    public static AgentResult Fail(string error) => new() { Ok = false, Error = error };
}

/// <summary>代理发给调度端的握手报文（JSON 行协议）。令牌配对：调度端校验后才回执。</summary>
internal sealed class AgentHello
{
    /// <summary>命令字：恒为 <c>hello</c>。</summary>
    public string Op { get; set; } = "hello";

    /// <summary>调度端签发、经令牌文件一次性交接的会话令牌（D39 配对凭据）。</summary>
    public string Token { get; set; } = string.Empty;
}

/// <summary>AOT 安全的 JSON 源生成上下文 —— 代理只用这三个模型。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AgentHello))]
[JsonSerializable(typeof(AgentCommand))]
[JsonSerializable(typeof(AgentResult))]
internal sealed partial class AgentJsonContext : JsonSerializerContext;
