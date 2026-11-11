namespace Chargehand.Modules.Events.Api;

/// <summary>What one caller of the event log may do (events spec, decision 8): the source prefix it writes as, and the scope globs it reads and writes.</summary>
/// <param name="Name">The token's name in the profile; <c>owner</c> for the owner's key.</param>
/// <param name="Source">The source prefix it may write: <c>claude</c> allows <c>claude</c> and <c>claude/…</c>. Null: any source.</param>
/// <param name="DefaultScope">The scope an event it sends without one gets; null: <c>run:&lt;actor.run&gt;</c> when the actor names a run, else <c>global</c>.</param>
public sealed record EventGrant(string Name, string? Source, IReadOnlyList<string> Read, IReadOnlyList<string> Write, string? DefaultScope = null)
{
    /// <summary>The owner's key: every source and every scope.</summary>
    public static EventGrant Owner { get; } = new("owner", null, ["*"], ["*"]);

    public bool CanRead(string scope) => Read.Any(g => Glob.Match(g, scope));

    public bool CanWrite(string scope) => Write.Any(g => Glob.Match(g, scope));

    public bool CanWriteSource(string source) =>
        Source is null || source == Source || source.StartsWith(Source + "/", StringComparison.Ordinal);
}

/// <summary>The globs of the event filters and grants: <c>*</c> matches any run of characters, <c>/</c> included; every other character matches itself.</summary>
public static class Glob
{
    public static bool IsPattern(string glob)
    {
        ArgumentNullException.ThrowIfNull(glob);
        return glob.Contains('*', StringComparison.Ordinal);
    }

    public static bool Match(string glob, string value)
    {
        ArgumentNullException.ThrowIfNull(glob);
        ArgumentNullException.ThrowIfNull(value);
        int g = 0, v = 0, star = -1, mark = 0;
        while (v < value.Length)
        {
            if (g < glob.Length && glob[g] == '*')
            {
                star = g++;
                mark = v;
            }
            else if (g < glob.Length && glob[g] == value[v])
            {
                g++;
                v++;
            }
            else if (star >= 0)
            {
                g = star + 1;
                v = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (g < glob.Length && glob[g] == '*')
            g++;
        return g == glob.Length;
    }

    /// <summary>The same glob for SQLite's <c>GLOB</c>, where <c>?</c> and <c>[</c> mean something.</summary>
    public static string ToSqlite(string glob)
    {
        ArgumentNullException.ThrowIfNull(glob);
        return glob.Replace("[", "[[]", StringComparison.Ordinal).Replace("?", "[?]", StringComparison.Ordinal);
    }
}
