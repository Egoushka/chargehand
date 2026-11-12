using System.Text.RegularExpressions;

namespace Chargehand.Automation;

public enum ClaimKind { File, Symbol, Text, Malformed }

/// <summary>A doc line that cites the code it rests on. <paramref name="Path"/> is the cited file, relative to the repository root; for <see cref="ClaimKind.Malformed"/> it is the marker as written.</summary>
public sealed record DocClaim(string DocFile, int Line, ClaimKind Kind, string Path, string? Symbol = null, string? Literal = null);

/// <summary>Reads citation markers from a guide page (specs/2026-11-11-claim-drift-watch.md): an HTML comment at the end of a line, <c>cites: path#Symbol</c>, <c>cites: path</c> or <c>cites-text: path "literal"</c>.
/// Pure: no file is read. A marker inside a fenced code block is an example, not a claim; a marker that does not parse is a <see cref="ClaimKind.Malformed"/> claim so a typo does not drop it silently.</summary>
public static partial class ClaimMarkers
{
    [GeneratedRegex(@"<!--\s*cites(?:-text)?\b")]
    private static partial Regex Opening();

    [GeneratedRegex(@"^<!--\s*cites:\s*(?<path>[^\s#""]+)(?:#(?<symbol>[A-Za-z_]\w*))?\s*-->$")]
    private static partial Regex Cites();

    [GeneratedRegex(@"^<!--\s*cites-text:\s*(?<path>[^\s#""]+)\s+""(?<literal>[^""]+)""\s*-->$")]
    private static partial Regex CitesText();

    public static IReadOnlyList<DocClaim> Parse(string docFile, string markdown)
    {
        var claims = new List<DocClaim>();
        string? fence = null;
        var number = 0;
        foreach (var line in markdown.Split('\n'))
        {
            number++;
            var trimmed = line.TrimStart();
            var mark = trimmed.StartsWith("```", StringComparison.Ordinal) ? "```" : trimmed.StartsWith("~~~", StringComparison.Ordinal) ? "~~~" : null;
            if (mark is not null)
            {
                fence = fence is null ? mark : fence == mark ? null : fence;
                continue;
            }
            if (fence is null && Opening().Match(line) is { Success: true } open)
                claims.Add(ParseMarker(docFile, number, line[open.Index..].TrimEnd()));
        }
        return claims;
    }

    private static DocClaim ParseMarker(string docFile, int line, string marker)
    {
        if (Cites().Match(marker) is { Success: true } cites && Safe(cites.Groups["path"].Value))
        {
            var symbol = cites.Groups["symbol"].Success ? cites.Groups["symbol"].Value : null;
            return new DocClaim(docFile, line, symbol is null ? ClaimKind.File : ClaimKind.Symbol, cites.Groups["path"].Value, symbol);
        }
        if (CitesText().Match(marker) is { Success: true } text && Safe(text.Groups["path"].Value))
            return new DocClaim(docFile, line, ClaimKind.Text, text.Groups["path"].Value, Literal: text.Groups["literal"].Value);
        return new DocClaim(docFile, line, ClaimKind.Malformed, marker);
    }

    /// <summary>A cited path stays inside the repository: relative, no <c>..</c>.</summary>
    private static bool Safe(string path) => !Path.IsPathRooted(path) && !path.Contains('\\') && !path.Split('/').Contains("..");
}
