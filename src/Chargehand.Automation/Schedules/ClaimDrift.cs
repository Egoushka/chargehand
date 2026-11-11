using System.Text.RegularExpressions;

namespace Chargehand.Automation;

public enum DriftReason { FileMissing, SymbolMissing, TextMissing, TestMissing, Malformed }

/// <param name="Detail">The cited symbol or literal, when the claim names one.</param>
public sealed record DriftFinding(string DocFile, int DocLine, DriftReason Reason, string CitedPath, string? Detail = null)
{
    public string ReasonName => Reason switch
    {
        DriftReason.FileMissing => "file-missing",
        DriftReason.SymbolMissing => "symbol-missing",
        DriftReason.TextMissing => "text-missing",
        DriftReason.TestMissing => "test-missing",
        _ => "malformed",
    };

    /// <summary>The line the CLI prints and the schedule posts: <c>docs/guide/x.md:42: symbol-missing src/A.cs#Name</c>.</summary>
    public string Line => $"{DocFile}:{DocLine}: {ReasonName} {CitedPath}{Suffix}";

    /// <summary>What identifies the finding across runs, so it is reported once.</summary>
    public string Key => $"{DocFile}:{DocLine}:{ReasonName}:{CitedPath}";

    private string Suffix => Detail is null || Reason is DriftReason.FileMissing or DriftReason.Malformed ? ""
        : Reason == DriftReason.TextMissing ? $" \"{Detail}\"" : $"#{Detail}";
}

/// <summary>Checks that what a guide page cites still exists (specs/2026-11-11-claim-drift-watch.md). Deterministic, no model: it reads the cited files and nothing else. It judges that a cited thing exists, not that the prose is true.</summary>
public static class ClaimDrift
{
    public const string GuideDirectory = "docs/guide";

    /// <summary>The claims of every page under <c>docs/guide</c>.</summary>
    public static IReadOnlyList<DocClaim> Scan(string repositoryRoot)
    {
        var guide = Path.Combine(repositoryRoot, GuideDirectory);
        if (!Directory.Exists(guide))
            return [];
        return [.. Directory.EnumerateFiles(guide, "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .SelectMany(f => ClaimMarkers.Parse(Path.GetRelativePath(repositoryRoot, f).Replace('\\', '/'), File.ReadAllText(f)))];
    }

    /// <param name="testNames">The compiled test names (<see cref="TestNames"/>); when given, a claim citing a symbol in a file under <c>tests/</c> also needs that test in it.</param>
    public static IReadOnlyList<DriftFinding> Check(IEnumerable<DocClaim> claims, string repositoryRoot, IReadOnlySet<string>? testNames = null) =>
        [.. claims.Select(c => CheckOne(c, repositoryRoot, testNames)).OfType<DriftFinding>()];

    private static DriftFinding? CheckOne(DocClaim claim, string root, IReadOnlySet<string>? testNames)
    {
        DriftFinding Found(DriftReason reason, string? detail = null) => new(claim.DocFile, claim.Line, reason, claim.Path, detail);
        if (claim.Kind == ClaimKind.Malformed)
            return Found(DriftReason.Malformed);
        var full = Path.GetFullPath(root);
        var file = Path.GetFullPath(Path.Combine(full, claim.Path));
        if (!file.StartsWith(full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(file))
            return Found(DriftReason.FileMissing);
        switch (claim.Kind)
        {
            case ClaimKind.Symbol:
                if (!Regex.IsMatch(File.ReadAllText(file), $@"(?<!\w){Regex.Escape(claim.Symbol!)}(?!\w)"))
                    return Found(DriftReason.SymbolMissing, claim.Symbol);
                return testNames is not null && claim.Path.StartsWith("tests/", StringComparison.Ordinal) && !testNames.Contains(claim.Symbol!)
                    ? Found(DriftReason.TestMissing, claim.Symbol) : null;
            case ClaimKind.Text:
                return File.ReadAllText(file).Contains(claim.Literal!, StringComparison.Ordinal) ? null : Found(DriftReason.TextMissing, claim.Literal);
            default:
                return null;
        }
    }

    /// <summary>Test method names from a list, one per line (<c>dotnet test --list-tests</c> output or plain names): blank and header lines are skipped; a parameter list and a <c>Class.</c> prefix are cut off.</summary>
    public static IReadOnlySet<string> TestNames(string list) =>
        list.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.Contains(' ') || l.Contains('(')).Select(l =>
        {
            var name = l.Split('(')[0].Trim();
            return name[(name.LastIndexOf('.') + 1)..];
        }).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
}
