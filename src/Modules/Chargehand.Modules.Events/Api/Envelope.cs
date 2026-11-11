using System.Globalization;
using System.Text;
using System.Text.Json;
using Chargehand.Kernel;

namespace Chargehand.Modules.Events.Api;

/// <summary>Why an envelope was not stored: the HTTP status, a stable code and a sentence. It never quotes the refused value.</summary>
public sealed record EventRefusal(int Status, string Code, string Message, int Index = 0);

/// <summary>The server's checks on one <c>events/v1</c> envelope (events spec, decision 2 and its refusals; threads addendum) and its normalized form.</summary>
public static class Envelope
{
    /// <summary>The most <c>data</c> may hold inline; more goes behind <c>dataref</c>.</summary>
    public const int MaxDataBytes = 16 * 1024;

    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Checks <paramref name="e"/> against <paramref name="grant"/> and returns the event to store, or why not.</summary>
    public static (NewEvent? Event, EventRefusal? Refusal) Check(JsonElement e, EventGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (e.ValueKind != JsonValueKind.Object)
            return Refuse(400, "not_an_object", "an envelope is a JSON object");
        if (Head(e) is { } head)
            return (null, head);
        var source = e.GetProperty("source").GetString()!;
        var type = e.GetProperty("type").GetString()!;
        if (!grant.CanWriteSource(source))
            return Refuse(403, "source_not_granted", $"token '{grant.Name}' may not write source '{source}'");
        if (Data(e) is { } data)
            return (null, data);
        if (Leaks(e) is { } leak)
            return (null, leak);
        var (scope, refusal) = Scope(e, type, grant);
        if (refusal is not null)
            return (null, refusal);
        if (type == ThreadStep.Type && ThreadStep.Problem(e) is { } step)
            return Refuse(400, "thread_step_invalid", step);
        var time = DateTimeOffset.Parse(e.GetProperty("time").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
            .UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);
        var subject = Text(e, "subject");
        return (new NewEvent(source, e.GetProperty("id").GetString()!, type, subject, scope!, Text(e, "correlationid"), time, Normalize(e, time, scope!)), null);
    }

    private static EventRefusal? Head(JsonElement e)
    {
        foreach (var field in new[] { "id", "source", "type", "time" })
        {
            if (!e.TryGetProperty(field, out var v) || v.ValueKind != JsonValueKind.String || v.GetString() is not { Length: > 0 })
                return new EventRefusal(400, "missing_field", $"'{field}' is required and is a non-empty string");
        }
        if (e.TryGetProperty("specversion", out var spec) && (spec.ValueKind != JsonValueKind.String || spec.GetString() != "1.0"))
            return new EventRefusal(400, "bad_specversion", "'specversion' is \"1.0\"");
        if (!EventPatterns.Source().IsMatch(e.GetProperty("source").GetString()!))
            return new EventRefusal(400, "bad_source", "'source' is <tool>[/<instance>] in lower case");
        if (!EventPatterns.Type().IsMatch(e.GetProperty("type").GetString()!))
            return new EventRefusal(400, "bad_type", "'type' is <domain>.<noun>.<past verb>");
        if (!DateTimeOffset.TryParse(e.GetProperty("time").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            return new EventRefusal(400, "bad_time", "'time' is an RFC 3339 timestamp");
        foreach (var field in new[] { "subject", "correlationid", "causationid", "dataref", "scope" })
        {
            if (e.TryGetProperty(field, out var v) && v.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return new EventRefusal(400, "bad_field", $"'{field}' is a string");
        }
        return null;
    }

    private static EventRefusal? Data(JsonElement e)
    {
        if (!e.TryGetProperty("data", out var data) || Text(e, "dataref") is not null)
            return null;
        return Encoding.UTF8.GetByteCount(data.GetRawText()) > MaxDataBytes
            ? new EventRefusal(413, "data_too_large", $"'data' is over {MaxDataBytes} bytes; put the content behind 'dataref'")
            : null;
    }

    /// <summary>A secret anywhere in the envelope, or a <c>work/</c> path in <c>subject</c>, <c>dataref</c> or <c>data</c> (D-48).</summary>
    private static EventRefusal? Leaks(JsonElement e)
    {
        if (DiffScan.ScanText(e.GetRawText(), []) is { Count: > 0 } kinds)
            return new EventRefusal(422, "secret", $"the envelope matches a secret pattern ({string.Join(", ", kinds)}); nothing was stored");
        var marked = new[] { Text(e, "subject"), Text(e, "dataref") }.Where(s => s is not null).Select(s => s!)
            .Concat(e.TryGetProperty("data", out var data) ? Strings(data) : []);
        return marked.Any(s => EventPatterns.WorkPath().IsMatch(s))
            ? new EventRefusal(422, "work_path", "the envelope names a path under work/ (D-48); nothing was stored")
            : null;
    }

    private static (string? Scope, EventRefusal? Refusal) Scope(JsonElement e, string type, EventGrant grant)
    {
        var scope = Text(e, "scope") ?? Default(e, grant);
        if (scope.StartsWith("person:", StringComparison.Ordinal))
            return (null, new EventRefusal(422, "person_scope", "no event is addressed to a person; the approvals gate reaches the maintainer"));
        if (!EventPatterns.Scope().IsMatch(scope))
            return (null, new EventRefusal(400, "bad_scope", "'scope' is global, project:, run:, channel: or agent:"));
        // Every token may post to an agent's inbox, as with a postbox (events spec, decision 8); only the inbox's owner reads it.
        var postbox = type == "agent.message.sent" && scope.StartsWith("agent:", StringComparison.Ordinal);
        return postbox || grant.CanWrite(scope)
            ? (scope, null)
            : (null, new EventRefusal(403, "scope_not_granted", $"token '{grant.Name}' may not write scope '{scope}'"));
    }

    private static string Default(JsonElement e, EventGrant grant)
    {
        if (grant.DefaultScope is { } own)
            return own;
        return e.TryGetProperty("actor", out var actor) && actor.ValueKind == JsonValueKind.Object && actor.TryGetProperty("run", out var run)
            && run.ValueKind == JsonValueKind.String && run.GetString() is { Length: > 0 } id
            ? $"run:{id}"
            : "global";
    }

    /// <summary>The envelope as stored: <c>seq</c> dropped (the server sets it), <c>time</c> in UTC, <c>specversion</c> and <c>scope</c> filled in.</summary>
    private static string Normalize(JsonElement e, string time, string scope)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("specversion", "1.0");
            foreach (var p in e.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "seq" or "specversion" or "scope":
                        break;
                    case "time":
                        w.WriteString("time", time);
                        break;
                    default:
                        p.WriteTo(w);
                        break;
                }
            }
            w.WriteString("scope", scope);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IEnumerable<string> Strings(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                yield return e.GetString()!;
                break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    yield return p.Name;
                    foreach (var s in Strings(p.Value))
                        yield return s;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                {
                    foreach (var s in Strings(item))
                        yield return s;
                }
                break;
        }
    }

    private static (NewEvent?, EventRefusal?) Refuse(int status, string code, string message) => (null, new EventRefusal(status, code, message));
}
