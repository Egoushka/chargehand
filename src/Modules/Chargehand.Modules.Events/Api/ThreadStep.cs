using System.Text.Json;

namespace Chargehand.Modules.Events.Api;

/// <summary>One <c>thread.step.recorded</c> event read back (threads addendum): the thread is its <c>subject</c>.</summary>
public sealed record ThreadStep(long Seq, string Thread, string Time, string Scope, string? Goal, string Done, string? Next, bool Closed, IReadOnlyList<string> Refs,
    string Actor)
{
    public const string Type = "thread.step.recorded";

    public const int MaxText = 2000;

    public const int MaxRefs = 20;

    public const int MaxRef = 300;

    /// <summary>What is wrong with a step's <c>subject</c> and <c>data</c>, or null. The first-step rule (a goal) needs the log and is checked on append.</summary>
    public static string? Problem(JsonElement e)
    {
        if (Envelope.Text(e, "subject") is not { Length: > 0 and <= 128 } thread || !EventPatterns.Thread().IsMatch(thread))
            return "a thread step's 'subject' is the thread id: 1 to 128 characters of letters, digits and ._:/#-";
        if (!e.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return "a thread step's 'data' is an object";
        var state = Envelope.Text(data, "state") ?? "open";
        if (state is not ("open" or "closed"))
            return "'data.state' is open or closed";
        if (!Sized(data, "done", required: true))
            return "'data.done' is required: 1 to 2000 characters";
        if (!Sized(data, "next", required: state == "open"))
            return "'data.next' is required while the thread is open: 1 to 2000 characters";
        if (!Sized(data, "goal", required: false))
            return "'data.goal' is 1 to 2000 characters";
        return RefsOf(data) is null ? "'data.refs' is at most 20 strings of up to 300 characters" : null;
    }

    /// <summary>Whether a step gives a goal; a thread's first step must.</summary>
    public static bool HasGoal(JsonElement e) =>
        e.TryGetProperty("data", out var data) && Envelope.Text(data, "goal") is { Length: > 0 };

    /// <summary>The step in a stored event, or null when it is not one this build reads.</summary>
    public static ThreadStep? Read(StoredEvent stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (stored.Type != Type || stored.Subject is null)
            return null;
        using var doc = JsonDocument.Parse(stored.Envelope);
        var e = doc.RootElement;
        if (!e.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object || Envelope.Text(data, "done") is not { } done)
            return null;
        var actor = e.TryGetProperty("actor", out var a) ? a.GetRawText() : "null";
        return new ThreadStep(stored.Seq, stored.Subject, stored.Time, stored.Scope, Envelope.Text(data, "goal"), done, Envelope.Text(data, "next"),
            Envelope.Text(data, "state") == "closed", RefsOf(data) ?? [], actor);
    }

    private static bool Sized(JsonElement data, string name, bool required)
    {
        if (!data.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            return !required;
        return v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 and <= MaxText };
    }

    private static List<string>? RefsOf(JsonElement data)
    {
        if (!data.TryGetProperty("refs", out var refs) || refs.ValueKind == JsonValueKind.Null)
            return [];
        if (refs.ValueKind != JsonValueKind.Array || refs.GetArrayLength() > MaxRefs)
            return null;
        var found = new List<string>();
        foreach (var r in refs.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.String || r.GetString() is not { Length: > 0 and <= MaxRef } s)
                return null;
            found.Add(s);
        }
        return found;
    }
}
