using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Runtime;

namespace Chargehand.OpenCode;

/// <summary>
/// IWorkerRuntime over OpenCode V2 (ADR 0004). Create with <see cref="ConnectAsync"/>, which pins the version. It hands a
/// node's granted services to the worker by registering the servers at the run's location (<see cref="OpenCodeServices"/>,
/// ADR 0034), so it also ends a run's registrations and says which servers never connected.
/// </summary>
public sealed class OpenCodeWorkerRuntime : IWorkerRuntime, IRunCleanup, IServiceHealth
{
    private readonly IOpenCodeClient _oc;
    private readonly OpenCodeServices _services;

    internal OpenCodeWorkerRuntime(IOpenCodeClient oc, string version, ServiceTimings? serviceTimings = null)
    {
        _oc = oc;
        Version = version;
        _services = new OpenCodeServices(oc, serviceTimings ?? ServiceTimings.Default);
    }

    public string Version { get; }

    /// <summary>The server version the adapter was verified against (ADR 0004); the default server (ADR 0030) must run it.</summary>
    public const string PinnedVersion = "2.0.18";

    /// <summary>Refuses a server whose version differs from the pinned one.</summary>
    public static async Task<OpenCodeWorkerRuntime> ConnectAsync(IOpenCodeClient oc, string pinnedVersion, CancellationToken ct)
    {
        var version = await oc.VersionAsync(ct);
        return version == pinnedVersion
            ? new OpenCodeWorkerRuntime(oc, version)
            : throw new ChargehandException(ErrorCode.RuntimeVersionMismatch, $"OpenCode server runs {version}; this adapter is pinned to {pinnedVersion}.",
                $"Run OpenCode {pinnedVersion}, or change opencode.version in the profile.");
    }

    /// <summary>
    /// Ends every session's rules (last match wins): denies each action with an underscore. An MCP tool's action is
    /// the server name, an underscore and the tool name, so this covers every server, and the MCP resource tools and
    /// external_directory (every preset denies it already). Servers cannot be listed instead: a checkout's own config
    /// registers them after the session exists, the listing lags that by up to a second, and any client can add one
    /// later (ADR 0034). A granted tool is allowed by a rule after this one (<see cref="OpenCodeServices"/>).
    /// </summary>
    internal static readonly PermissionRule DenyMcpTools = new("*_*", "*", PermissionEffect.Deny);

    /// <summary>The servers of the node's grants are registered and connected before the session exists, and the session's
    /// rules end with the deny and one exact allow per granted tool of a server that connected.</summary>
    public async Task<WorkerSession> CreateAsync(NodeSpec spec, CancellationToken ct)
    {
        var granted = await _services.GrantAsync(spec, ct);
        var s = await _oc.CreateSessionAsync(new CreateSessionBody(
            spec.Agent,
            spec.Model is { } m ? new ModelBody(m.ProviderId, m.ModelId, m.Variant) : null,
            new LocationBody(spec.Directory),
            spec.Permissions.Append(DenyMcpTools).Concat(granted.Rules).Select(r => new RuleBody(r.Action, r.Resource, r.Effect.ToString().ToLowerInvariant())).ToList(),
            spec.Metadata), ct);
        _services.Bind(s.Id, granted);
        return new WorkerSession(s.Id, s.Location.Directory);
    }

    public Task EndRunAsync(string runId, CancellationToken ct) => _services.EndRunAsync(runId, ct);

    public IReadOnlyDictionary<string, string> UnavailableServices(string sessionId) => _services.Unavailable(sessionId);

    public Task SetInstructionAsync(string sessionId, string key, string value, CancellationToken ct) => _oc.PutInstructionAsync(sessionId, key, value, ct);

    public Task SubmitAsync(string sessionId, string text, CancellationToken ct) => _oc.PromptAsync(sessionId, text, ct);

    public async Task<IdleOutcome> AwaitIdleAsync(string sessionId, CancellationToken ct)
    {
        await _oc.WaitAsync(sessionId, ct);
        var idle = (await ReadMessagesAsync(sessionId, ct)).FirstOrDefault(m => m.Kind == WorkerMessageKind.Idle);
        return idle?.Outcome ?? IdleOutcome.Failed;
    }

    public async Task InterruptAsync(string sessionId, CancellationToken ct) => await _oc.InterruptAsync(sessionId, ct);

    public async Task<IReadOnlyList<WorkerMessage>> ReadMessagesAsync(string sessionId, CancellationToken ct) =>
        (await _oc.MessagesAsync(sessionId, ct)).Select(Map).ToList();

    public async Task<IReadOnlyList<PermissionRequest>> PendingPermissionsAsync(string sessionId, CancellationToken ct) =>
        (await _oc.PermissionsAsync(sessionId, ct)).Select(p => new PermissionRequest(
            p.GetProperty("id").GetString()!,
            p.GetProperty("action").GetString()!,
            p.GetProperty("resources").EnumerateArray().Select(r => r.GetString()!).ToList())).ToList();

    public Task AnswerPermissionAsync(string sessionId, string requestId, PermissionDecision decision, string? message, CancellationToken ct) =>
        _oc.ReplyPermissionAsync(sessionId, requestId, decision == PermissionDecision.Once ? "once" : "reject", message, ct);

    public async Task<WorkerSession> ForkAsync(string sessionId, string? beforeMessageId, CancellationToken ct)
    {
        var s = await _oc.ForkAsync(sessionId, beforeMessageId, ct);
        _services.Inherit(sessionId, s.Id);
        return new WorkerSession(s.Id, s.Location.Directory);
    }

    public Task CompactAsync(string sessionId, CancellationToken ct) => _oc.CompactAsync(sessionId, ct);

    public async Task<IReadOnlyList<FileDiff>> DiffAsync(string sessionId, CancellationToken ct) =>
        (await _oc.DiffAsync(sessionId, ct)).Select(d => new FileDiff(
            d.GetProperty("file").GetString()!,
            d.TryGetProperty("patch", out var p) ? p.GetString() ?? "" : "",
            d.TryGetProperty("additions", out var a) ? a.GetInt32() : 0,
            d.TryGetProperty("deletions", out var del) ? del.GetInt32() : 0,
            d.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "")).ToList();

    public Task<string> GenerateAsync(ModelRef? model, string prompt, CancellationToken ct) => _oc.GenerateAsync(model?.ProviderId, model?.ModelId, prompt, ct);

    /// <summary>Messages are an untagged union dispatched on "type" (the spec declares no discriminator).</summary>
    internal static WorkerMessage Map(JsonElement m)
    {
        var type = m.GetProperty("type").GetString();
        var time = m.GetProperty("time");
        var created = DateTimeOffset.FromUnixTimeMilliseconds(time.GetProperty("created").GetInt64());
        DateTimeOffset? completed = time.TryGetProperty("completed", out var c) ? DateTimeOffset.FromUnixTimeMilliseconds(c.GetInt64()) : null;
        switch (type)
        {
            case "assistant":
                var text = new StringBuilder();
                var tools = new StringBuilder();
                var results = new List<string>();
                if (m.TryGetProperty("content", out var parts))
                    foreach (var p in parts.EnumerateArray())
                    {
                        var pt = p.GetProperty("type").GetString();
                        if (pt == "text")
                            text.Append(p.GetProperty("text").GetString());
                        else if (pt == "tool" && p.TryGetProperty("state", out var state))
                        {
                            tools.Append(state.GetRawText()).Append('\n');
                            if (state.TryGetProperty("output", out var output) && output.ValueKind != JsonValueKind.Null)
                                results.Add(output.ValueKind == JsonValueKind.String ? output.GetString()! : output.GetRawText());
                        }
                    }
                return new WorkerMessage(
                    m.GetProperty("id").GetString()!, WorkerMessageKind.Assistant, created, text.ToString(), Tokens(m), completed,
                    m.TryGetProperty("model", out var model) ? $"{model.GetProperty("providerID").GetString()}/{model.GetProperty("id").GetString()}" : null,
                    tools.ToString(),
                    m.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null ? err.GetRawText() : null,
                    ToolResults: results);
            case "idle":
                var outcome = m.TryGetProperty("outcome", out var o) ? o.GetString() : null;
                return new WorkerMessage(m.GetProperty("id").GetString()!, WorkerMessageKind.Idle, created, null, null,
                    Outcome: outcome switch { "succeeded" => IdleOutcome.Succeeded, "interrupted" => IdleOutcome.Interrupted, _ => IdleOutcome.Failed });
            default:
                var kind = type switch { "user" => WorkerMessageKind.User, "compaction" => WorkerMessageKind.Compaction, _ => WorkerMessageKind.Other };
                return new WorkerMessage(m.GetProperty("id").GetString()!, kind, created,
                    m.TryGetProperty("text", out var t) ? t.GetString() : null, kind == WorkerMessageKind.Compaction ? Tokens(m) : null, completed);
        }
    }

    private static TokenCounts? Tokens(JsonElement m)
    {
        if (!m.TryGetProperty("tokens", out var t) || t.ValueKind != JsonValueKind.Object)
            return null;
        var cache = t.GetProperty("cache");
        return new TokenCounts(
            (long)t.GetProperty("input").GetDouble(), (long)t.GetProperty("output").GetDouble(), (long)t.GetProperty("reasoning").GetDouble(),
            (long)cache.GetProperty("read").GetDouble(), (long)cache.GetProperty("write").GetDouble());
    }
}
