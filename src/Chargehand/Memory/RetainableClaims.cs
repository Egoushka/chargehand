using System.Globalization;
using System.Text.RegularExpressions;
using Chargehand.Contracts;

namespace Chargehand.Memory;

/// <param name="Locators">The <c>file</c> locators as cited, and <c>commit &lt;7 hex&gt;</c> for commit evidence, once each in citation order.</param>
public sealed record RetainableClaim(string Text, IReadOnlyList<string> Locators, double Confidence);

/// <param name="Skipped">One line per claim left out for a reason other than an unresolved citation (those are already open questions).</param>
public sealed record RetainSelection(IReadOnlyList<RetainableClaim> Claims, IReadOnlyList<string> Skipped);

/// <summary>
/// Which claims of a result long-term memory may keep (goal 0.6, ADR 0034, spec decisions 4 and 10). A completed result's
/// evidence has all resolved by the time it gets here: the worker node drops what did not, after its repair turn, so a
/// claim qualifies when at least one <c>file</c> or <c>commit</c> entry it cites is left. No confidence floor. "Resolved"
/// means the path, lines or commit exist at the pinned commit. A claim the support check found only partly supported is not kept (ADR 0036); an
/// unsupported one is no longer a claim.
/// </summary>
public static partial class RetainableClaims
{
    private const int PreviewLength = 60;

    public static RetainSelection From(ResultContract result)
    {
        if (result.Status != ResultStatus.Completed)
            return new RetainSelection([], []);
        var evidence = new Dictionary<string, Evidence>();
        foreach (var e in result.Evidence)
            evidence.TryAdd(e.Id, e);
        var claims = new List<RetainableClaim>();
        var skipped = new List<string>();
        foreach (var claim in result.Claims)
        {
            var text = OneLine(claim.Text);
            if (claim.Support == ClaimSupport.Partial)
            {
                skipped.Add($"partly supported: {Preview(text)}");
                continue;
            }
            var locators = claim.Evidence
                .Select(id => evidence.GetValueOrDefault(id))
                .OfType<Evidence>()
                .Select(Locator)
                .OfType<string>()
                .Distinct()
                .ToList();
            if (locators.Count == 0)
                skipped.Add($"not repository-anchored: {Preview(text)}");
            else if (ChargehandException.Scrub(text) != text || locators.Any(l => ChargehandException.Scrub(l) != l))
                // The preview is scrubbed too: a skipped claim is exactly the one that had something to hide.
                skipped.Add($"redacted: {Preview(ChargehandException.Scrub(text))}");
            else
                claims.Add(new RetainableClaim(text, locators, claim.Confidence));
        }
        return new RetainSelection(claims, skipped);
    }

    /// <summary>What a repository-anchored citation is written as in the item, or null for a kind that anchors nothing.</summary>
    private static string? Locator(Evidence e) => e.Kind switch
    {
        EvidenceKind.File => e.Locator,
        EvidenceKind.Commit => $"commit {e.Locator[..Math.Min(7, e.Locator.Length)]}",
        _ => null,
    };

    /// <summary>A claim is one line, so no claim can start an item line of its own.</summary>
    private static string OneLine(string text) => Whitespace().Replace(text, " ").Trim();

    private static string Preview(string text) => text.Length <= PreviewLength ? text : text[..PreviewLength];

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>The item a run leaves in each memory that retains: the checked claims with where they were checked, never the request or the summary.</summary>
public static class RetainItems
{
    private const int ShortCommitLength = 12;

    public static MemoryItem Build(RetainSelection selection, string repositoryLabel, string commit, string runId, DateTimeOffset finished)
    {
        var shortCommit = commit[..Math.Min(ShortCommitLength, commit.Length)];
        var lines = new List<string> { $"Repository: {repositoryLabel}, commit {shortCommit} (citations checked at this commit)" };
        lines.AddRange(selection.Claims.Select(c =>
            $"- {c.Text} [{string.Join("; ", c.Locators)}] (confidence {c.Confidence.ToString("0.00", CultureInfo.InvariantCulture)})"));
        var provenance = new RetainProvenance(repositoryLabel, shortCommit, selection.Claims.SelectMany(c => c.Locators).Distinct().ToList());
        return new MemoryItem(string.Join("\n", lines), "chargehand run result", finished, runId, ["chargehand"], provenance);
    }
}

/// <summary>Names a repository in a memory: the same on every machine that holds a clone of it (spec decision 6).</summary>
public static partial class RepositoryLabel
{
    /// <summary>
    /// The <c>origin</c> URL without scheme, user information, port and a trailing <c>.git</c>, so
    /// <c>https://user:tok@host/team/proj.git</c> and <c>git@host:team/proj.git</c> both read <c>host/team/proj</c>.
    /// The directory name when there is no origin, or when it is a path on this machine.
    /// </summary>
    public static string From(string? originUrl, string directoryName)
    {
        var url = originUrl?.Trim() ?? "";
        string label;
        if (SchemeUrl().Match(url) is { Success: true } scheme)
            label = scheme.Groups["scheme"].Value.Equals("file", StringComparison.OrdinalIgnoreCase) ? "" : HostAndPath(url[scheme.Length..]);
        else if (ScpUrl().Match(url) is { Success: true } scp)
            label = $"{scp.Groups["host"].Value}/{scp.Groups["path"].Value.TrimStart('/')}";
        else
            label = "";
        label = label.TrimEnd('/');
        label = label.EndsWith(".git", StringComparison.Ordinal) ? label[..^4] : label;
        return label.Length == 0 ? directoryName : label;
    }

    /// <summary>The part after <c>scheme://</c>, less user information and port.</summary>
    private static string HostAndPath(string rest)
    {
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var authority = slash < 0 ? rest : rest[..slash];
        var host = Port().Replace(authority[(authority.LastIndexOf('@') + 1)..], "");
        return slash < 0 ? host : host + rest[slash..];
    }

    [GeneratedRegex(@"^(?<scheme>[A-Za-z][A-Za-z0-9+.-]*)://")]
    private static partial Regex SchemeUrl();

    /// <summary>The scp form, <c>[user@]host:path</c>; a local path has no colon before its first separator.</summary>
    [GeneratedRegex(@"^(?:[^@/\s]+@)?(?<host>[^:/\s@]+):(?<path>\S+)$")]
    private static partial Regex ScpUrl();

    [GeneratedRegex(@":\d*$")]
    private static partial Regex Port();
}
