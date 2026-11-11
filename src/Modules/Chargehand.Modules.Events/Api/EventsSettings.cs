using System.Text.RegularExpressions;

namespace Chargehand.Modules.Events.Api;

/// <summary>The profile's <c>http.events</c> (ADR 0090): the event log at <c>/v1/events</c> and the threads at <c>/v1/threads</c>. The owner's key reads and writes all of it;
/// each token here is one producer's, bound to its source prefix and its scope grants.</summary>
/// <param name="Tokens">The producers' tokens; empty: only the owner's key reaches the log.</param>
/// <param name="Database">The SQLite file; null: <c>events.db</c> beside the run log.</param>
public sealed record EventsSettings(IReadOnlyList<EventTokenSettings>? Tokens = null, string? Database = null)
{
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
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
