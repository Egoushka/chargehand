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

/// <summary>What a citation must resolve to: a path in the repository, or a test that exists under tests/.</summary>
internal sealed class GuideEvidence(Func<string, bool> pathExists, Func<string, bool> testExists)
{
    public static readonly GuideEvidence Repository = new(RepoPathExists, RepoTestExists);

    public bool PathExists(string path) => pathExists(path);

    public bool TestExists(string token) => testExists(token);

    private static bool RepoPathExists(string path) =>
        !path.Contains("..", StringComparison.Ordinal) && (File.Exists(Repo.Path(path)) || Directory.Exists(Repo.Path(path)));

    private static readonly Lazy<(HashSet<string> Classes, HashSet<string> Methods)> Index = new(BuildIndex);

    private static (HashSet<string>, HashSet<string>) BuildIndex()
    {
        var classes = new HashSet<string>();
        var methods = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(Repo.Path("tests"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"\bclass\s+(\w+)"))
                classes.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"\b(?:void|Task)\s+(\w+)\s*\("))
                methods.Add(m.Groups[1].Value);
        }
        return (classes, methods);
    }

    private static bool RepoTestExists(string token)
    {
        var (classes, methods) = Index.Value;
        var parts = token.Split('.');
        return parts.Length == 1
            ? classes.Contains(token) || methods.Contains(token)
            : classes.Contains(parts[0]) && methods.Contains(parts[1]);
    }
}

internal static partial class GuideClaims
{
    /// <summary>Baseline lines to a multiset: a sentence listed twice may appear twice.</summary>
    public static Dictionary<string, int> ReadBaseline(IEnumerable<string> lines)
    {
        var counts = new Dictionary<string, int>();
        foreach (var p in lines.Select(l => l.Split('\t')).Where(p => p.Length >= 2))
        {
            var key = $"{p[0]}\t{p[1]}";
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        return counts;
    }

    /// <summary>Claims beyond what the baseline holds, counting occurrences.</summary>
    public static List<GuideClaim> Added(IEnumerable<GuideClaim> claims, Dictionary<string, int> baseline)
    {
        var left = new Dictionary<string, int>(baseline);
        var added = new List<GuideClaim>();
        foreach (var claim in claims)
        {
            if (left.GetValueOrDefault(claim.Key) > 0)
                left[claim.Key]--;
            else
                added.Add(claim);
        }
        return added;
    }

    /// <summary>Baseline entries no claim matches any more (cited or reworded since).</summary>
    public static int Stale(IEnumerable<GuideClaim> claims, Dictionary<string, int> baseline)
    {
        var left = new Dictionary<string, int>(baseline);
        foreach (var claim in claims)
            if (left.GetValueOrDefault(claim.Key) > 0)
                left[claim.Key]--;
        return left.Values.Sum();
    }

    public static List<GuideClaim> Uncited(string file, string[] lines, GuideEvidence? evidence = null)
    {
        evidence ??= GuideEvidence.Repository;
        var result = new List<GuideClaim>();
        foreach (var block in Blocks(lines))
        {
            var text = string.Join(' ', block);
            if (Cited(text, evidence))
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
        var i = FrontMatterEnd(lines);
        char fence = '\0';
        var fenceLength = 0;
        for (; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (fence != '\0')
            {
                if (IsFence(trimmed, out var c, out var n) && c == fence && n >= fenceLength && trimmed.TrimEnd().Length == n)
                    fence = '\0';
                continue;
            }
            if (IsFence(trimmed, out var open, out var length))
            {
                fence = open;
                fenceLength = length;
                current = null;
                continue;
            }
            if (current is not null && trimmed.StartsWith("<!-- cites", StringComparison.Ordinal))
            {
                current.Add(trimmed);
                continue;
            }
            if (trimmed.Length == 0 || trimmed[0] is '#' or '|' or '>' || HtmlBlock().IsMatch(trimmed) || LinkOnly().IsMatch(trimmed))
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

    /// <summary>Index of the first line after front matter: 0 when the file does not open with a "---" line.</summary>
    private static int FrontMatterEnd(string[] lines)
    {
        if (lines.Length == 0 || !FrontMatterDelimiter().IsMatch(lines[0]))
            return 0;
        for (var i = 1; i < lines.Length; i++)
            if (FrontMatterDelimiter().IsMatch(lines[i]))
                return i + 1;
        return 0;
    }

    private static bool IsFence(string trimmed, out char marker, out int length)
    {
        marker = trimmed.Length > 0 ? trimmed[0] : '\0';
        length = 0;
        if (marker is not ('`' or '~'))
            return false;
        while (length < trimmed.Length && trimmed[length] == marker)
            length++;
        return length >= 3;
    }

    private static bool Cited(string text, GuideEvidence evidence)
    {
        foreach (Match m in EvidenceLink().Matches(text))
            if (evidence.PathExists(Normalize(m.Groups[1].Value)))
                return true;
        foreach (Match m in Backticked().Matches(text))
        {
            var token = m.Groups[1].Value;
            if (token.StartsWith("src/", StringComparison.Ordinal) || token.StartsWith("tests/", StringComparison.Ordinal))
            {
                if (evidence.PathExists(Normalize(token)))
                    return true;
            }
            else if (TestName().IsMatch(token) && evidence.TestExists(token))
                return true;
        }
        foreach (Match m in Marker().Matches(text))
            if (evidence.PathExists(Normalize(m.Groups[1].Value)))
                return true;
        return false;
    }

    private static string Normalize(string target)
    {
        var path = target;
        while (path.StartsWith("../", StringComparison.Ordinal))
            path = path[3..];
        var anchor = path.IndexOf('#', StringComparison.Ordinal);
        return (anchor >= 0 ? path[..anchor] : path).TrimEnd('/');
    }

    private static IEnumerable<string> Sentences(string text) =>
        SentenceEnd().Split(Comments().Replace(text, " ")).Select(s => s.Trim())
            .Where(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 4);

    [GeneratedRegex(@"^---\s*$")]
    private static partial Regex FrontMatterDelimiter();

    [GeneratedRegex(@"^\[[^\]]*\]\([^)]*\)\s*$")]
    private static partial Regex LinkOnly();

    [GeneratedRegex(@"^(?:<!--|</?(?:div|details|summary|table|thead|tbody|tr|td|th|p|pre|img|br|hr|h[1-6]|ul|ol|li|section|figure|center|picture|video|iframe)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBlock();

    [GeneratedRegex(@"^(?:[-*+]|\d+\.)\s+")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"\]\(((?:\.\./)*(?:src|tests)/[^)\s]*)\)")]
    private static partial Regex EvidenceLink();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex Backticked();

    /// <summary>A test class (<c>FooTests</c>, <c>FooTests.Method</c>) or a snake_case test method (<c>Method_name</c>).</summary>
    [GeneratedRegex(@"^(?:[A-Z]\w*Tests(?:\.\w+)?|[A-Z]\w*_[a-z]\w*)$")]
    private static partial Regex TestName();

    /// <summary>The marker format shared with the claim-drift watch (#785): <c>&lt;!-- cites: path#Symbol --&gt;</c> or <c>cites-text: path "literal"</c>.
    /// Only the path is resolved here.</summary>
    [GeneratedRegex(@"<!--\s*cites(?:-text)?:\s*(\S+)")]
    private static partial Regex Marker();

    [GeneratedRegex(@"<!--.*?-->")]
    private static partial Regex Comments();

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z`\[])")]
    private static partial Regex SentenceEnd();
}
