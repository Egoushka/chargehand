using System.Text.Json;

namespace Chargehand.OpenCode;

/// <summary>
/// Thin typed client for the OpenCode V2 operations listed in docs/opencode-adapter-ops.json (ADR 0004, A23:
/// hand-written because the spec's anyOf unions carry no discriminator). Auth: HTTP Basic, user "opencode".
/// Bodies of /api/model*, /api/provider*, /api/config* can hold provider keys and are never called or logged.
/// </summary>
public interface IOpenCodeClient
{
    Task<string> VersionAsync(CancellationToken ct);

    Task<SessionInfo> CreateSessionAsync(CreateSessionBody body, CancellationToken ct);

    Task<SessionInfo> GetSessionAsync(string sessionId, CancellationToken ct);

    Task PromptAsync(string sessionId, string text, CancellationToken ct);

    /// <summary>POST /api/experimental/session/{id}/wait; 204 when idle. No server-side timeout.</summary>
    Task WaitAsync(string sessionId, CancellationToken ct);

    /// <summary>Newest first. Elements are the raw union members, dispatched on their "type" field.</summary>
    Task<IReadOnlyList<JsonElement>> MessagesAsync(string sessionId, CancellationToken ct);

    Task<bool> InterruptAsync(string sessionId, CancellationToken ct);

    Task<SessionInfo> ForkAsync(string sessionId, string? beforeMessageId, CancellationToken ct);

    /// <summary>Steered: runs after the current step, even mid-turn; the turn then continues (spike, 2.0.16).</summary>
    Task CompactAsync(string sessionId, CancellationToken ct);

    Task<IReadOnlyList<JsonElement>> PermissionsAsync(string sessionId, CancellationToken ct);

    /// <summary>decision: "once" or "reject". "always" is never sent (ADR 0006).</summary>
    Task ReplyPermissionAsync(string sessionId, string requestId, string decision, string? message, CancellationToken ct);

    Task<IReadOnlyList<JsonElement>> DiffAsync(string sessionId, CancellationToken ct);

    Task PutInstructionAsync(string sessionId, string key, string value, CancellationToken ct);

    Task MoveAsync(string sessionId, string directory, CancellationToken ct);

    /// <summary>Null provider/model: the server's base configuration default (ADR 0026).</summary>
    Task<string> GenerateAsync(string? providerId, string? modelId, string prompt, CancellationToken ct);

    IAsyncEnumerable<JsonElement> EventsAsync(CancellationToken ct);
}

/// <param name="Model">Null: the server's base configuration default (ADR 0026).</param>
public sealed record CreateSessionBody(
    string Agent,
    ModelBody? Model,
    LocationBody Location,
    IReadOnlyList<RuleBody> Permissions,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record ModelBody(string ProviderID, string Id, string? Variant = null);

public sealed record LocationBody(string Directory);

public sealed record RuleBody(string Action, string Resource, string Effect);

public sealed record SessionInfo(string Id, string Agent, ModelBody Model, LocationBody Location, string? Outcome, string? ProjectID = null);
