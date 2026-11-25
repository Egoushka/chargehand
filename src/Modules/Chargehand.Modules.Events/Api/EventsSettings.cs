using System.Text.RegularExpressions;

namespace Chargehand.Modules.Events.Api;

/// <summary>The profile's <c>http.events</c> (ADR 0090): the event log at <c>/v1/events</c> and the threads at <c>/v1/threads</c>. The owner's key reads and writes all of it;
/// each token here is one producer's, bound to its source prefix and its scope grants.</summary>
/// <param name="Tokens">The producers' tokens; empty: only the owner's key reaches the log.</param>
/// <param name="Database">The SQLite file; null: <c>events.db</c> beside the run log.</param>
/// <param name="Signals">Plain-English watchers over finished threads (P06); null: off.</param>
public sealed record EventsSettings(IReadOnlyList<EventTokenSettings>? Tokens = null, string? Database = null, SignalsSettings? Signals = null)
{
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Signals is not null)
            problems.AddRange(Signals.Problems());
        var tokens = Tokens ?? [];
        foreach (var token in tokens)
            problems.AddRange(token.Problems());
        foreach (var name in tokens.GroupBy(t => t.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key))
            problems.Add($"http.events.tokens: the name '{name}' is used twice");
        return problems;
    }
}

/// <param name="Name">Unique among the tokens; it names the token in refusals, never the secret.</param>
/// <param name="Secret">The secret-store item holding the bearer token.</param>
/// <param name="Source">The source prefix the token writes, e.g. <c>claude</c>.</param>
/// <param name="Read">The scope globs it reads, e.g. <c>project:*</c>.</param>
/// <param name="Write">The scope globs it writes.</param>
/// <param name="DefaultScope">The scope of an event it sends without one.</param>
public sealed partial record EventTokenSettings(string Name, string Secret, string Source, IReadOnlyList<string>? Read = null, IReadOnlyList<string>? Write = null,
    string? DefaultScope = null)
{
    public EventGrant Grant() => new(Name, Source, Read ?? [], Write ?? [], DefaultScope);

    public IReadOnlyList<string> Problems()
    {
        var at = $"http.events.tokens[{Name}]";
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
            problems.Add("http.events.tokens: a token needs a name");
        if (string.IsNullOrWhiteSpace(Secret))
            problems.Add($"{at}.secret: name the secret-store item that holds the token");
        if (string.IsNullOrWhiteSpace(Source) || !EventPatterns.Source().IsMatch(Source))
            problems.Add($"{at}.source: '{Source}' is not a source (<tool>[/<instance>])");
        if ((Read ?? []).Concat(Write ?? []).Any(string.IsNullOrWhiteSpace))
            problems.Add($"{at}: a scope glob is empty");
        if (DefaultScope is { } scope && !EventPatterns.Scope().IsMatch(scope))
            problems.Add($"{at}.default_scope: '{scope}' is not a scope");
        if (DefaultScope is { } own && !Grant().CanWrite(own))
            problems.Add($"{at}.default_scope: '{own}' is outside the token's write grants");
        return problems;
    }
}

/// <summary>The patterns of <c>schemas/events/v1/events.schema.json</c>.</summary>
public static partial class EventPatterns
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*(/[A-Za-z0-9._-]+)*$")]
    public static partial Regex Source();

    [GeneratedRegex("^[a-z0-9_]+(\\.[a-z0-9_]+){2,}$")]
    public static partial Regex Type();

    [GeneratedRegex("^(global|(project|run|channel|agent):.+)$")]
    public static partial Regex Scope();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/#-]*$")]
    public static partial Regex Thread();

    /// <summary>A <c>work/</c> path (D-48): <c>work/</c> at the start or after a character that cannot end a path segment's name, so <c>network/</c> is not one.</summary>
    [GeneratedRegex("(^|[^A-Za-z0-9_.-])work/")]
    public static partial Regex WorkPath();
}

/// <summary>The profile's <c>http.events.signals</c> (P06): each sentence in <paramref name="Items"/> is checked, by a cheap read-only model run, against every thread that closes; a hit is kept for the console.
/// Off unless <paramref name="Enabled"/>, because every check is a model call.</summary>
/// <param name="Enabled">Default false.</param>
/// <param name="MaxChecksPerDay">Model calls per UTC day across all signals (default 50); a thread that closes after it is not checked.</param>
/// <param name="Preset">The read-only preset the judge runs on (default <c>cheap</c>).</param>
/// <param name="Items">The signals, each a unique id and a sentence.</param>
public sealed record SignalsSettings(bool Enabled = false, int MaxChecksPerDay = 50, string Preset = "cheap", IReadOnlyList<SignalItem>? Items = null)
{
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (MaxChecksPerDay < 1)
            problems.Add("http.events.signals.max_checks_per_day must be at least 1");
        if (string.IsNullOrWhiteSpace(Preset))
            problems.Add("http.events.signals.preset must not be empty");
        foreach (var item in Items ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Text))
                problems.Add("http.events.signals.items: a signal needs an id and a text");
        }
        foreach (var id in (Items ?? []).GroupBy(i => i.Id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key))
            problems.Add($"http.events.signals.items: the id '{id}' is used twice");
        return problems;
    }
}

/// <param name="Id">Unique; names the signal on a hit.</param>
/// <param name="Text">What to watch for, in plain English: "session stuck in a loop".</param>
public sealed record SignalItem(string Id, string Text);
