using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Chargehand.Tests;

internal sealed record GuideClaim(string File, string Sentence)
{
    /// <summary>File and a hash of the sentence: editing an uncited sentence makes it new.</summary>
    public string Key => $"{File}\t{Hash}";

    public string BaselineLine => $"{Key}\t{(Sentence.Length > 70 ? Sentence[..70] : Sentence)}";

    private string Hash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Sentence)))[..10].ToLowerInvariant();
}

internal static partial class GuideClaims
{
    public static HashSet<string> ReadBaseline(IEnumerable<string> lines) =>
        lines.Select(l => l.Split('\t')).Where(p => p.Length >= 2).Select(p => $"{p[0]}\t{p[1]}").ToHashSet();

    public static List<GuideClaim> Uncited(string file, string[] lines)
    {
        var result = new List<GuideClaim>();
        foreach (var block in Blocks(lines))
        {
            var text = string.Join(' ', block);
            if (Cited(text))
                continue;
            foreach (var sentence in Sentences(text))
                result.Add(new GuideClaim(file, sentence));
        }
        return result;
    }

    private static List<List<string>> Blocks(string[] lines)
    {
        var blocks = new List<List<string>>();
        List<string>? current = null;
        var i = 0;
        if (lines.Length > 0 && lines[0] == "---")
            for (i = 1; i < lines.Length && lines[i] != "---"; i++) { }
        i++;
        var fenced = false;
        for (; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fenced = !fenced;
                current = null;
                continue;
            }
            if (!fenced && current is not null && trimmed.StartsWith("<!-- cites", StringComparison.Ordinal))
            {
                current.Add(trimmed);
                continue;
            }
            if (fenced || trimmed.Length == 0 || trimmed[0] is '#' or '|' or '>' or '<' || LinkOnly().IsMatch(trimmed))
            {
                current = null;
                continue;
            }
            var item = ListMarker().Match(trimmed);
            if (item.Success)
            {
                current = [trimmed[item.Length..]];
                blocks.Add(current);
            }
            else if (current is null)
            {
                current = [trimmed];
                blocks.Add(current);
            }
            else
                current.Add(trimmed);
        }
        return blocks;
    }

    private static bool Cited(string text) => EvidenceLink().IsMatch(text) || EvidenceCode().IsMatch(text) || Marker().IsMatch(text);

    private static IEnumerable<string> Sentences(string text) =>
        SentenceEnd().Split(Comments().Replace(text, " ")).Select(s => s.Trim())
            .Where(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 4);

    [GeneratedRegex(@"^\[[^\]]*\]\([^)]*\)\s*$")]
    private static partial Regex LinkOnly();

    [GeneratedRegex(@"^(?:[-*+]|\d+\.)\s+")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"\]\((?:\.\./)*(?:src|tests)/")]
    private static partial Regex EvidenceLink();

    [GeneratedRegex(@"`(?:(?:src|tests)/[^`]*|[A-Z]\w*Tests(?:\.\w+)?|[A-Z]\w+\.[A-Z_]\w+|[A-Z]\w*_[a-z]\w*)`")]
    private static partial Regex EvidenceCode();

    /// <summary>The marker format shared with the claim-drift watch (#785): <c>&lt;!-- cites: path#Symbol --&gt;</c> or <c>cites-text:</c>.</summary>
    [GeneratedRegex(@"<!--\s*cites(?:-text)?:\s*\S")]
    private static partial Regex Marker();

    [GeneratedRegex(@"<!--.*?-->")]
    private static partial Regex Comments();

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z`\[])")]
    private static partial Regex SentenceEnd();
}
