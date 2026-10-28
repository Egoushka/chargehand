namespace AgentClientProtocol;

/// <summary>An option an agent offers when it asks permission. <see cref="Kind"/> is an <see cref="AcpPermissionKind"/>.</summary>
public sealed record PermissionOption(string OptionId, string Name, string Kind);

/// <summary>The agent asks to run a tool call. Answer with <see cref="RequestPermissionOutcome.Selected"/> and one of the offered option ids, or <see cref="RequestPermissionOutcome.Cancelled"/>.</summary>
public sealed record RequestPermissionRequest(string SessionId, ToolCallUpdate ToolCall, IReadOnlyList<PermissionOption> Options);

public sealed record RequestPermissionOutcome(string Outcome, string? OptionId = null)
{
    public static RequestPermissionOutcome Cancelled { get; } = new("cancelled");

    public static RequestPermissionOutcome Selected(string optionId) => new("selected", optionId);
}

public sealed record RequestPermissionResponse(RequestPermissionOutcome Outcome);

public sealed record ReadTextFileRequest(string SessionId, string Path, int? Line = null, int? Limit = null);

public sealed record ReadTextFileResponse(string Content);

public sealed record WriteTextFileRequest(string SessionId, string Path, string Content);

public sealed record CreateTerminalRequest(
    string SessionId,
    string Command,
    IReadOnlyList<string>? Args = null,
    IReadOnlyList<EnvVariable>? Env = null,
    string? Cwd = null,
    long? OutputByteLimit = null);

public sealed record CreateTerminalResponse(string TerminalId);

/// <summary>Names a terminal for output, wait, kill and release.</summary>
public sealed record TerminalRequest(string SessionId, string TerminalId);

public sealed record TerminalExitStatus(int? ExitCode = null, string? Signal = null);

public sealed record TerminalOutputResponse(string Output, bool Truncated, TerminalExitStatus? ExitStatus = null);
