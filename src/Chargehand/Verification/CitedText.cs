using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Chargehand.Contracts;

namespace Chargehand.Verification;

/// <param name="Index">The claim's position in <c>contract.Claims</c>.</param>
/// <param name="Text">What its file, diff and input citations point at, each under a header.</param>
/// <param name="Truncated">The text was cut: a citation to more than 200 lines, or more than 12 000 characters in all.</param>
/// <param name="Hashes">Evidence id to the SHA-256 (lowercase hex) of the whole cited text, before any cut: a file's cited lines joined by
/// newlines, a diff's overlapping hunks, an input's text. A reader can recompute it from the repository at the pinned commit.</param>
public sealed record CitedClaim(int Index, Claim Claim, string Text, bool Truncated, IReadOnlyDictionary<string, string>? Hashes = null);

/// <summary>The text a claim cites (ADR 0036), for the support check. A claim whose citations are only <c>url</c>, <c>commit</c> or
/// <c>session_message</c> has none to compare, and is left out.</summary>
public static class CitedText
{
    internal const int MaxLinesPerCitation = 200;
    internal const int MaxCharsPerClaim = 12_000;
    private const int MaxInputChars = 2_000;

    public static async Task<IReadOnlyList<CitedClaim>> ForAsync(ResultContract contract, EvidenceScope scope, CancellationToken ct)
    {
        var byId = contract.Evidence.ToDictionary(e => e.Id);
        var cited = new List<CitedClaim>();
        for (var i = 0; i < contract.Claims.Count; i++)
        {
            var claim = contract.Claims[i];
            var sb = new StringBuilder();
            var hashes = new Dictionary<string, string>();
            var truncated = false;
            foreach (var e in claim.Evidence.Select(id => byId.GetValueOrDefault(id)).OfType<Evidence>())
            {
                var (text, cut, whole) = e.Kind switch
                {
                    EvidenceKind.File => await FileText(e, scope, ct),
                    EvidenceKind.Diff => DiffText(e, scope),
                    EvidenceKind.Input => InputText(e, scope),
                    _ => (null, false, null),
                };
                if (text is null)
                    continue;
                hashes[e.Id] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(whole ?? text)));
                sb.Append('[').Append(e.Id).Append("] ").Append(e.Kind.ToString().ToLowerInvariant()).Append(' ').AppendLine(e.Locator).AppendLine(text);
                truncated |= cut;
            }
            if (sb.Length == 0)
                continue;
            var all = sb.ToString().TrimEnd();
            if (all.Length > MaxCharsPerClaim)
            {
                all = all[..MaxCharsPerClaim];
                truncated = true;
            }
            cited.Add(new CitedClaim(i, claim, all, truncated, hashes));
        }
        return cited;
    }

    private static async Task<(string?, bool, string?)> FileText(Evidence e, EvidenceScope scope, CancellationToken ct)
    {
        if (!GitEvidenceResolver.TryParseRange(e.Locator, out var path, out var start, out var end) || path.StartsWith('/') || path.Split('/').Contains(".."))
            return (null, false, null);
        if (await GitEvidenceResolver.Git(scope.RepositoryPath, ct, "show", $"{scope.Commit}:{path}") is not { } content)
            return (null, false, null);
        var lines = content.TrimEnd('\n').Split('\n');
        var last = Math.Min(end, lines.Length);
        var cut = last - start + 1 > MaxLinesPerCitation;
        if (cut)
            last = start + MaxLinesPerCitation - 1;
        if (start > lines.Length)
            return (null, false, null);
        var whole = string.Join("\n", lines[(start - 1)..Math.Min(end, lines.Length)]);
        return (string.Join("\n", Enumerable.Range(start, last - start + 1).Select(n => $"{n}: {lines[n - 1]}")), cut, whole);
    }

    /// <summary>The hunks of the file's patch that overlap the cited range (the whole patch when the locator names no range).</summary>
    private static (string?, bool, string?) DiffText(Evidence e, EvidenceScope scope)
    {
        var hasRange = GitEvidenceResolver.TryParseRange(e.Locator, out var path, out var start, out var end);
        var file = scope.Diff.FirstOrDefault(d => d.File == (hasRange ? path : e.Locator));
        if (file is null)
            return (null, false, null);
        var hunks = Regex.Split(file.Patch, @"(?m)^(?=@@ )").Where(h => h.StartsWith("@@", StringComparison.Ordinal)).ToList();
        var chosen = hasRange
            ? hunks.Where(h => Overlaps(h, start, end)).ToList()
            : hunks;
        var text = string.Join("\n", chosen).TrimEnd();
        var lines = text.Split('\n');
        if (text.Length == 0)
            return (null, false, null);
        return lines.Length > MaxLinesPerCitation ? (string.Join("\n", lines.Take(MaxLinesPerCitation)), true, text) : (text, false, text);
    }

    private static bool Overlaps(string hunk, int start, int end)
    {
        var m = GitEvidenceResolver.Hunk().Match(hunk);
        if (!m.Success)
            return false;
        var from = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
        var len = m.Groups["l"].Success ? int.Parse(m.Groups["l"].Value, CultureInfo.InvariantCulture) : 1;
        return start <= from + len - 1 && end >= from;
    }

    /// <summary>The input's own text, out of the node's rendered inputs (<c>- id "x" (kind): text</c>, one entry per input).</summary>
    private static (string?, bool, string?) InputText(Evidence e, EvidenceScope scope)
    {
        var entries = Regex.Split(scope.InputText ?? scope.SeenText, @"(?m)^(?=- id "")");
        var entry = entries.FirstOrDefault(x => x.StartsWith($"- id \"{e.Locator}\"", StringComparison.Ordinal));
        if (entry is null)
            return (null, false, null);
        var text = entry[(entry.IndexOf("): ", StringComparison.Ordinal) + 3)..].TrimEnd();
        return text.Length > MaxInputChars ? (text[..MaxInputChars], true, text) : (text, false, text);
    }
}
