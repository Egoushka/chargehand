using System.Text.RegularExpressions;

namespace Chargehand.Modules.Watch.Api;

/// <summary>One Markdown link in a doc that cites a file under <c>src/</c>, <c>tests/</c> or <c>scripts/</c>: the claim's evidence.</summary>
/// <param name="Doc">The doc, relative to the repository root, with <c>/</c> separators.</param>
/// <param name="Line">The doc line the link is on, from 1.</param>
/// <param name="Label">The link text without backticks.</param>
/// <param name="Target">The cited file, relative to the repository root.</param>
/// <param name="StartLine">The first cited line of a <c>#L10</c> or <c>#L10-L12</c> anchor, or null.</param>
/// <param name="EndLine">The last cited line; equal to <paramref name="StartLine"/> for one line.</param>
public sealed record DocCitation(string Doc, int Line, string Label, string Target, int? StartLine, int? EndLine);

/// <summary>A citation that no longer matches the code: the doc line and why.</summary>
public sealed record DriftFinding(DocCitation Citation, string Reason)
{
    public override string ToString() => $"{Citation.Doc}:{Citation.Line}: {Citation.Target}: {Reason}";
}

/// <summary>
/// The claim-drift watch (docs/specs/2026-11-08-claim-drift-watch-design.md): re-checks each claim in the docs that cites code or a
/// test, without a model. A citation is broken when its file is gone, when its line anchor runs past the end of the file, or when
/// its link text names a symbol (an identifier such as <c>EvidenceResolverTests</c>) the file no longer contains.
/// </summary>
public static partial class ClaimDrift
{
    /// <summary>The folders a citation must point into to count as citing code or a test.</summary>
    public static readonly IReadOnlyList<string> CitedRoots = ["src/", "tests/", "scripts/"];

    /// <summary>Every code or test citation in the Markdown files under <paramref name="docsFolder"/>, in file and line order.</summary>
    public static IReadOnlyList<DocCitation> Citations(string repositoryRoot, string docsFolder)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var folder = Path.Combine(root, docsFolder);
        if (!Directory.Exists(folder))
            return [];
        var citations = new List<DocCitation>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            citations.AddRange(CitationsIn(root, file));
        return citations;
    }

    /// <summary>Checks each citation against the files under <paramref name="repositoryRoot"/> and returns the broken ones.</summary>
    public static IReadOnlyList<DriftFinding> Check(string repositoryRoot, IEnumerable<DocCitation> citations)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var findings = new List<DriftFinding>();
        foreach (var citation in citations)
        {
            if (Broken(root, citation) is { } reason)
                findings.Add(new DriftFinding(citation, reason));
        }
        return findings;
    }

    /// <summary>Finds and checks the citations of <paramref name="docsFolder"/> in one call.</summary>
    public static IReadOnlyList<DriftFinding> Scan(string repositoryRoot, string docsFolder = "docs/guide") =>
        Check(repositoryRoot, Citations(repositoryRoot, docsFolder));

    /// <summary>The title and body of the issue a broken claim opens: it names the doc line, the cited file and the reason.</summary>
    public static (string Title, string Body) Issue(DriftFinding finding)
    {
        var c = finding.Citation;
        var title = $"docs: stale claim at {c.Doc}:{c.Line}";
        var body = $"The claim-drift watch found a claim whose citation no longer matches the code.\n\n"
            + $"- Doc line: `{c.Doc}:{c.Line}`\n"
            + $"- Cites: `{c.Target}{Anchor(c)}` (link text `{c.Label}`)\n"
            + $"- Why: {finding.Reason}\n\n"
            + "Update the claim or its citation so they match the code again.";
        return (title, body);
    }

    private static IEnumerable<DocCitation> CitationsIn(string root, string file)
    {
        var doc = Path.GetRelativePath(root, file).Replace('\\', '/');
        var docDir = Path.GetDirectoryName(file)!;
        var lines = File.ReadAllLines(file);
        var fenced = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                fenced = !fenced;
            if (fenced)
                continue;
            foreach (Match link in LinkPattern().Matches(lines[i]))
            {
                if (Resolve(root, docDir, link.Groups["path"].Value) is not { } target)
                    continue;
                var (start, end) = LineRange(link.Groups["anchor"].Value);
                yield return new DocCitation(doc, i + 1, link.Groups["label"].Value.Replace("`", "", StringComparison.Ordinal).Trim(), target, start, end);
            }
        }
    }

    private static string? Resolve(string root, string docDir, string path)
    {
        if (path.Contains("://", StringComparison.Ordinal) || path.StartsWith('/'))
            return null;
        var full = Path.GetFullPath(Path.Combine(docDir, Uri.UnescapeDataString(path)));
        var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        return CitedRoots.Any(r => relative.StartsWith(r, StringComparison.Ordinal)) ? relative : null;
    }

    private static (int? Start, int? End) LineRange(string anchor)
    {
        var m = LineAnchorPattern().Match(anchor);
        if (!m.Success)
            return (null, null);
        var start = int.Parse(m.Groups["start"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var end = m.Groups["end"].Success ? int.Parse(m.Groups["end"].Value, System.Globalization.CultureInfo.InvariantCulture) : start;
        return (start, end);
    }

    private static string? Broken(string root, DocCitation citation)
    {
        var path = Path.Combine(root, citation.Target);
        if (Directory.Exists(path))
            return null;
        if (!File.Exists(path))
            return "the cited file does not exist";
        var text = File.ReadAllText(path);
        if (citation.EndLine is { } end)
        {
            var count = text.Split('\n').Length - (text.EndsWith('\n') ? 1 : 0);
            if (end > count || citation.StartLine > end)
                return $"the cited lines {citation.StartLine}-{end} are past the end of the file ({count} lines)";
        }
        if (SymbolPattern().IsMatch(citation.Label) && !NamesFile(citation) && !text.Contains(citation.Label, StringComparison.Ordinal))
            return $"the file no longer contains `{citation.Label}`, which the link text names";
        return null;
    }

    // "mcp-smoke.py" or "Dockerfile": the label names the file, not a symbol in it.
    private static bool NamesFile(DocCitation citation) =>
        string.Equals(citation.Label, Path.GetFileName(citation.Target), StringComparison.Ordinal)
        || string.Equals(citation.Label, Path.GetFileNameWithoutExtension(citation.Target), StringComparison.Ordinal) && !citation.Target.EndsWith(".cs", StringComparison.Ordinal);

    private static string Anchor(DocCitation c) =>
        c.StartLine is null ? "" : c.StartLine == c.EndLine ? $"#L{c.StartLine}" : $"#L{c.StartLine}-L{c.EndLine}";

    [GeneratedRegex(@"(?<!!)\[(?<label>[^\]]*)\]\((?<path>[^)\s#]+)(?<anchor>#[^)\s]*)?\)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"^#L(?<start>\d+)(-L(?<end>\d+))?$")]
    private static partial Regex LineAnchorPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")]
    private static partial Regex SymbolPattern();
}
