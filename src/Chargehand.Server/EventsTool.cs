using System.Text.Json;
using Chargehand.Modules.Events.Api;
using Chargehand.Server.Events;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Chargehand.Server;

/// <summary>
/// MCP tool "events" (D-147): a read of the event log with the filters of <c>GET /v1/events</c> plus <c>actor</c>, so a desk's Start asks what happened since it last ran.
/// Read-only; the caller's grant limits the scopes it sees, and a caller with no grant (an account key, stdio) gets an error.
/// </summary>
public static class EventsTool
{
    public const string Name = "events";

    private const string InputSchema = """
        {"type":"object","properties":{
        "type":{"type":"string","description":"Event type glob, e.g. claude.session.*"},
        "source":{"type":"string","description":"Source glob"},
        "scope":{"type":"string","description":"Scope glob, e.g. project:owner/repo or run:*"},
        "actor":{"type":"string","description":"Glob matched against the actor's name, id or run"},
        "subject":{"type":"string","description":"Exact subject"},
        "correlationid":{"type":"string","description":"Exact correlation id, e.g. a session id"},
        "since":{"type":"string","description":"RFC 3339 time, events at or after it"},
        "until":{"type":"string","description":"RFC 3339 time, events at or before it"},
        "after":{"type":"integer","description":"Only events with a seq after this; pass last_seq to page"},
        "limit":{"type":"integer","description":"At most this many, oldest first (default 100, max 500)"}}}
        """;

    public static McpServerTool Create()
    {
        var tool = McpServerTool.Create(CallAsync, new McpServerToolCreateOptions
        {
            Name = Name,
            Description = "Read chargehand's event log (what agents and runs did), oldest first, filtered by type, source, scope, actor, subject, correlation id and time. "
                        + "Returns {events, last_seq}.",
            ReadOnly = true,
        });
        tool.ProtocolTool.InputSchema = JsonDocument.Parse(InputSchema).RootElement.Clone();
        return tool;
    }

    private static async Task<CallToolResult> CallAsync(RequestContext<CallToolRequestParams> context, CancellationToken ct)
    {
        var service = context.Services!.GetService<EventService>();
        var grant = context.Services!.GetService<IHttpContextAccessor>()?.HttpContext?.Items[EventRoutes.GrantKey] as EventGrant;
        if (service is null || grant is null)
            return Error(service is null ? "this server has no event log" : "this key has no grant on the event log");
        var args = context.Params?.Arguments;
        string? Text(string name) => args?.TryGetValue(name, out var v) == true && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
        long Number(string name, long fallback) => args?.TryGetValue(name, out var v) == true && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : fallback;
        var query = new EventQuery(Number("after", 0), Text("type"), Text("source"), Text("scope"), Text("subject"), (int)Math.Clamp(Number("limit", EventQuery.DefaultLimit), 1, EventQuery.MaxLimit),
            Text("since"), Text("until"), Text("correlationid"), Text("actor"));
        var answer = await service.QueryAsync(grant, query, ct);
        return answer.Status == 200
            ? new CallToolResult { Content = [new TextContentBlock { Text = answer.Json }] }
            : Error(answer.Json);
    }

    private static CallToolResult Error(string text) => new() { IsError = true, Content = [new TextContentBlock { Text = text }] };
}
