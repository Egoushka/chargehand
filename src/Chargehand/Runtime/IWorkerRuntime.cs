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

    /// <summary>One-shot text generation without a session (intake, ADR 0005). Returns no usage.</summary>
    Task<string> GenerateAsync(ModelRef model, string prompt, CancellationToken ct);
}

public sealed record ModelRef(string ProviderId, string ModelId, string? Variant = null)
{
    public override string ToString() => Variant is null ? $"{ProviderId}/{ModelId}" : $"{ProviderId}/{ModelId}#{Variant}";
}

public sealed record PermissionRule(string Action, string Resource, PermissionEffect Effect);

public enum PermissionEffect { Allow, Deny, Ask }

public enum PermissionDecision { Once, Reject }

/// <summary>Everything fixed at node creation. Directory must lie outside the runtime user's home (ADR 0003).</summary>
public sealed record NodeSpec(
    string Directory,
    string Agent,
    ModelRef Model,
    IReadOnlyList<PermissionRule> Permissions,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record WorkerSession(string Id, string Directory);

public enum IdleOutcome { Succeeded, Failed, Interrupted }

public sealed record TokenCounts(long Input, long Output, long Reasoning, long CacheRead, long CacheWrite);

public sealed record WorkerMessage(string Id, WorkerMessageKind Kind, DateTimeOffset Created, string? Text, TokenCounts? Tokens);

public enum WorkerMessageKind { User, Assistant, Compaction, Idle, Other }

public sealed record PermissionRequest(string Id, string Action, IReadOnlyList<string> Resources);

public sealed record FileDiff(string File, string Patch, int Additions, int Deletions, string Status);
