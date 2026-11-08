using System.Text.Json;
using Chargehand.Modules.Events.Api;

namespace Chargehand.Modules.Events.Tests;

/// <summary>An event log in a temporary file, with the grants the spec's checks name.</summary>
internal sealed class Log : IDisposable
{
    public static readonly EventGrant VideoStudio = new("video-studio", "video-studio", ["project:*"], ["project:*", "global"]);

    public static readonly EventGrant ProjectA = new("a", "claude", ["project:Egoushka/a"], ["project:Egoushka/a"], "project:Egoushka/a");

    public static readonly EventGrant Mac = new("mac", "claude", ["project:*", "channel:*", "agent:cc/*", "global"], ["project:*", "channel:*", "agent:cc/*", "global"]);

    private readonly string dir = Path.Combine(Path.GetTempPath(), "chargehand-events-" + Guid.NewGuid().ToString("N"));

    public Log()
    {
        Store = SqliteEventLog.Open(Path.Combine(dir, "events.db"));
        Service = new EventService(Store);
    }

    public SqliteEventLog Store { get; }

    public EventService Service { get; }

    public static string Envelope(string id, string type = "claude.tool.called", string source = "claude", string? scope = "project:Egoushka/a", string? subject = null,
        string? data = null, string time = "2026-11-08T12:00:00Z", string? correlation = null, string? actor = null, string? causation = null)
    {
        var fields = new List<string>
        {
            "\"specversion\":\"1.0\"", $"\"id\":{Q(id)}", $"\"source\":{Q(source)}", $"\"type\":{Q(type)}", $"\"time\":{Q(time)}",
        };
        if (scope is not null)
            fields.Add($"\"scope\":{Q(scope)}");
        if (subject is not null)
            fields.Add($"\"subject\":{Q(subject)}");
        if (data is not null)
            fields.Add($"\"data\":{data}");
        if (correlation is not null)
            fields.Add($"\"correlationid\":{Q(correlation)}");
        if (causation is not null)
            fields.Add($"\"causationid\":{Q(causation)}");
        if (actor is not null)
            fields.Add($"\"actor\":{actor}");
        return "{" + string.Join(",", fields) + "}";
    }

    public static string Step(string id, string thread, string done, string? next = null, string? goal = null, string? state = null, string[]? refs = null,
        string scope = "project:Egoushka/a", string time = "2026-11-08T12:00:00Z", string actor = "cc/one")
    {
        var data = new List<string> { $"\"done\":{Q(done)}" };
        if (next is not null)
            data.Add($"\"next\":{Q(next)}");
        if (goal is not null)
            data.Add($"\"goal\":{Q(goal)}");
        if (state is not null)
            data.Add($"\"state\":{Q(state)}");
        if (refs is not null)
            data.Add($"\"refs\":[{string.Join(",", refs.Select(Q))}]");
        return Envelope(id, ThreadStep.Type, scope: scope, subject: thread, data: "{" + string.Join(",", data) + "}", time: time,
            actor: $"{{\"kind\":\"agent\",\"id\":{Q(actor)},\"name\":null,\"run\":null}}");
    }

    public async Task<EventAnswer> PostAsync(string json, EventGrant? grant = null)
    {
        using var doc = JsonDocument.Parse(json);
        return await Service.AppendAsync(grant ?? EventGrant.Owner, doc.RootElement, CancellationToken.None);
    }

    public async Task<long> CountAsync() => (await Store.ReadAsync(new EventFilter(Limit: 100_000), CancellationToken.None)).Count;

    public static JsonElement Parse(EventAnswer answer) => JsonDocument.Parse(answer.Json).RootElement;

    public static string Q(string s) => JsonSerializer.Serialize(s, EventsTestJson.Default.String);

    public void Dispose()
    {
        Service.Dispose();
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // agent-checks: allow empty-catch -- a temp directory left behind by a file the OS still holds is harmless to the test run
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class EventsTestJson : System.Text.Json.Serialization.JsonSerializerContext;
