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

    /// <summary>PUT /api/experimental/mcp/{name} for a location: adds the server, or replaces one of that name there. 204 comes
    /// before the connection; poll <see cref="McpServersAsync"/> (spike O1, ADR 0034). The body holds header and environment values.</summary>
    Task PutMcpServerAsync(string name, string directory, McpConfigBody config, CancellationToken ct);

    /// <summary>DELETE /api/experimental/mcp/{name} for a location: sessions there lose the server at once (spike O7). 404 when it is not there.</summary>
    Task RemoveMcpServerAsync(string name, string directory, CancellationToken ct);

    /// <summary>GET /api/mcp for a location: every server registered there and its status. Names and status only.</summary>
    Task<IReadOnlyList<McpServerStatus>> McpServersAsync(string directory, CancellationToken ct);

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

/// <summary>An MCP server entry as OpenCode takes it: <c>remote</c> with a URL and headers, or <c>local</c> with a command and environment.
/// <c>ToString</c> hides header and environment values.</summary>
public sealed record McpConfigBody(
    string Type,
    string? Url = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyList<string>? Command = null,
    IReadOnlyDictionary<string, string>? Environment = null)
{
    public override string ToString() => $"{nameof(McpConfigBody)} {{ Type = {Type} }}";
}

/// <param name="Status">connected, pending, failed, needs_auth or disabled.</param>
/// <param name="Error">OpenCode's reason for failed and needs_auth.</param>
public sealed record McpServerStatus(string Name, string Status, string? Error = null);

public sealed record SessionInfo(string Id, string Agent, ModelBody Model, LocationBody Location, string? Outcome, string? ProjectID = null);
