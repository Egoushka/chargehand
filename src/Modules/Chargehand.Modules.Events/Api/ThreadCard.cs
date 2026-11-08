using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Chargehand.Modules.Events.Api;

/// <summary>A thread's state, derived from its steps (threads addendum): what a fresh session reads to continue the work.</summary>
/// <param name="Owner">The latest step's actor, as JSON.</param>
/// <param name="Steps">How many of the thread's steps the caller may read.</param>
public sealed record ThreadCard(string Thread, string? Goal, bool Closed, string Scope, ThreadStep Last, string? Next, string LastActivity, int Steps,
    IReadOnlyList<string> Refs)
{
    public const int MaxText = 2000;

    public string State => Closed ? "closed" : "open";

    /// <summary>The card from <paramref name="steps"/>, oldest first, the latest of them the thread's latest step.</summary>
    public static ThreadCard From(IReadOnlyList<ThreadStep> steps, string? lastActivity)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var last = steps[^1];
        var goal = steps.LastOrDefault(s => s.Goal is not null)?.Goal;
        var refs = steps.Reverse().SelectMany(s => s.Refs).Distinct(StringComparer.Ordinal).Take(ThreadStep.MaxRefs).ToList();
        var activity = lastActivity is not null && string.CompareOrdinal(lastActivity, last.Time) > 0 ? lastActivity : last.Time;
        return new ThreadCard(last.Thread, goal, last.Closed, last.Scope, last, last.Next, activity, steps.Count, refs);
    }

    /// <summary>The card as Markdown of at most <see cref="MaxText"/> characters: thread id, goal and next step first, so a cut never loses them.</summary>
    public string Text()
    {
        var who = ActorName(Last.Actor);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Thread {Thread} ({State}), scope {Scope}\n");
        text.Append(CultureInfo.InvariantCulture, $"Goal: {Cut(Goal ?? "(none recorded)", 500)}\n");
        text.Append(CultureInfo.InvariantCulture, $"Next: {Cut(Next ?? "(none: the thread is closed)", 600)}\n");
        text.Append(CultureInfo.InvariantCulture, $"Last step: {Cut(Last.Done, 500)} ({who}, {Last.Time})\n");
        text.Append(CultureInfo.InvariantCulture, $"Steps: {Steps}; last activity {LastActivity}\n");
        if (Refs.Count > 0)
            text.Append("Refs: ").Append(string.Join(", ", Refs)).Append('\n');
        return Cut(text.ToString().TrimEnd('\n'), MaxText);
    }

    /// <summary>The card's JSON fields; <c>text</c> only when <paramref name="withText"/>.</summary>
    public void Write(Utf8JsonWriter w, bool withText)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartObject();
        w.WriteString("thread", Thread);
        w.WriteString("goal", Goal);
        w.WriteString("state", State);
        w.WriteString("scope", Scope);
        w.WriteStartObject("last_step");
        w.WriteNumber("seq", Last.Seq);
        w.WriteString("time", Last.Time);
        w.WriteString("done", Last.Done);
        w.WritePropertyName("actor");
        w.WriteRawValue(Last.Actor);
        w.WriteEndObject();
        w.WriteString("next", Next);
        w.WritePropertyName("owner");
        w.WriteRawValue(Last.Actor);
        w.WriteString("last_activity", LastActivity);
        w.WriteNumber("steps", Steps);
        w.WriteStartArray("refs");
        foreach (var r in Refs)
            w.WriteStringValue(r);
        w.WriteEndArray();
        if (withText)
            w.WriteString("text", Text());
        w.WriteEndObject();
    }

    private static string ActorName(string actor)
    {
        using var doc = JsonDocument.Parse(actor);
        var a = doc.RootElement;
        return Envelope.Text(a, "name") ?? Envelope.Text(a, "id") ?? "unknown actor";
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : string.Concat(s.AsSpan(0, max - 1), "…");
}
