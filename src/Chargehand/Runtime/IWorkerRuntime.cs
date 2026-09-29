namespace Chargehand.Runtime;

/// <summary>
/// Port to the coding-agent runtime (ADR 0004). Asynchronous first: a node is created, a prompt is submitted,
/// and the caller awaits idle with its own deadline, interrupting on timeout. One session per node.
/// </summary>
public interface IWorkerRuntime
{
    Task<WorkerSession> CreateAsync(NodeSpec spec, CancellationToken ct);

    /// <summary>Sets a durable instruction block. Call only before the node's first prompt (ADR 0010).</summary>
    Task SetInstructionAsync(string sessionId, string key, string value, CancellationToken ct);

    Task SubmitAsync(string sessionId, string text, CancellationToken ct);

    /// <summary>Blocks until the session is idle. The runtime has no timeout; <paramref name="ct"/> is the deadline.</summary>
    Task<IdleOutcome> AwaitIdleAsync(string sessionId, CancellationToken ct);

    Task InterruptAsync(string sessionId, CancellationToken ct);

    Task<IReadOnlyList<WorkerMessage>> ReadMessagesAsync(string sessionId, CancellationToken ct);

    Task<IReadOnlyList<PermissionRequest>> PendingPermissionsAsync(string sessionId, CancellationToken ct);

    /// <summary>Answers a pending request. Only once or reject; never a saved permission (ADR 0006).</summary>
    Task AnswerPermissionAsync(string sessionId, string requestId, PermissionDecision decision, string? message, CancellationToken ct);

    Task<WorkerSession> ForkAsync(string sessionId, string? beforeMessageId, CancellationToken ct);

    Task CompactAsync(string sessionId, CancellationToken ct);

    Task<IReadOnlyList<FileDiff>> DiffAsync(string sessionId, CancellationToken ct);

    /// <summary>One-shot text generation without a session (intake, ADR 0005). Returns no usage. Null model: the runtime's own default (ADR 0026).</summary>
    Task<string> GenerateAsync(ModelRef? model, string prompt, CancellationToken ct);
}

public sealed record ModelRef(string ProviderId, string ModelId, string? Variant = null)
{
    public override string ToString() => Variant is null ? $"{ProviderId}/{ModelId}" : $"{ProviderId}/{ModelId}#{Variant}";
}

public sealed record PermissionRule(string Action, string Resource, PermissionEffect Effect);

public enum PermissionEffect { Allow, Deny, Ask }

public enum PermissionDecision { Once, Reject }

/// <summary>Everything fixed at node creation. Directory must lie outside the runtime user's home (ADR 0003).</summary>
/// <param name="Model">Null: the runtime's own default model (ADR 0026).</param>
/// <param name="Services">The MCP services the preset granted this node (ADR 0034), resolved for the run; null or empty: none.
/// A runtime hands each grant's server to the worker and lets it call the granted tools only.</param>
public sealed record NodeSpec(
    string Directory,
    string Agent,
    ModelRef? Model,
    IReadOnlyList<PermissionRule> Permissions,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<ServiceGrant>? Services = null);

/// <summary>An MCP server the worker may use and the tools of it that it may call (ADR 0034), as resolved for one run.</summary>
/// <param name="Server">The profile's name for the server; the worker's runtime names the server that way.</param>
/// <param name="Tools">The granted tool names, sorted; every other tool of the server stays refused.</param>
/// <param name="Sha256">Hash of the granted tools' names, descriptions and input schemas; part of <c>as_sent.tools_sha256</c>.</param>
public sealed record ServiceGrant(string Server, ServiceTransport Transport, IReadOnlyList<string> Tools, string Sha256);

/// <summary>How a runtime reaches a granted server, with the profile's <c>{secret:item}</c> values already replaced.
/// <c>ToString</c> of every case hides header and environment values, so a grant can be logged.</summary>
public abstract record ServiceTransport;

/// <summary>A server at a URL. <see cref="Protocol"/> is what a runtime whose config names the protocol writes (Claude Code:
/// <c>http</c> or <c>sse</c>); OpenCode's <c>remote</c> entry needs no protocol.</summary>
public sealed record HttpServiceTransport(Uri Url, IReadOnlyDictionary<string, string> Headers, HttpServiceProtocol Protocol = HttpServiceProtocol.StreamableHttp) : ServiceTransport
{
    public override string ToString() => $"{nameof(HttpServiceTransport)} {{ Protocol = {Protocol}, Headers = [{string.Join(", ", Headers.Keys)}] }}";
}

/// <summary>A server the runtime starts as a child process. <see cref="Env"/> is only what the profile declares: the runtime
/// adds it to the environment it gives the child.</summary>
public sealed record StdioServiceTransport(IReadOnlyList<string> Command, IReadOnlyDictionary<string, string> Env) : ServiceTransport
{
    public override string ToString() => $"{nameof(StdioServiceTransport)} {{ Command = {(Command.Count > 0 ? Command[0] : "")}, Env = [{string.Join(", ", Env.Keys)}] }}";
}

/// <summary>The wire protocol of an HTTP MCP server. A profile server whose <c>transport</c> is <c>auto</c> (or unset) is
/// <see cref="StreamableHttp"/> for a worker: chargehand's own client falls back to SSE, a runtime's config cannot, so a
/// legacy SSE server must say <c>sse</c> in the profile for workers to reach it.</summary>
public enum HttpServiceProtocol { StreamableHttp, Sse }

public sealed record WorkerSession(string Id, string Directory);

public enum IdleOutcome { Succeeded, Failed, Interrupted }

public sealed record TokenCounts(long Input, long Output, long Reasoning, long CacheRead, long CacheWrite);

/// <param name="ToolOutput">Concatenated tool inputs and outputs of an assistant message (evidence scope).</param>
public sealed record WorkerMessage(
    string Id,
    WorkerMessageKind Kind,
    DateTimeOffset Created,
    string? Text,
    TokenCounts? Tokens,
    DateTimeOffset? Completed = null,
    string? Model = null,
    string? ToolOutput = null,
    string? Error = null,
    IdleOutcome? Outcome = null);

public enum WorkerMessageKind { User, Assistant, Compaction, Idle, Other }

public sealed record PermissionRequest(string Id, string Action, IReadOnlyList<string> Resources);

public sealed record FileDiff(string File, string Patch, int Additions, int Deletions, string Status);
