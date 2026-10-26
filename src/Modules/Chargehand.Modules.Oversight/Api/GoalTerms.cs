using System.Globalization;

namespace Chargehand.Modules.Oversight.Api;

/// <summary>The words of a task's goal, reduced to what a learning record may keep (ADR 0045: no goal text). Each distinct word of three or more letters or digits, minus a few filler words, is
/// lower-cased and hashed (FNV-1a, 32 bits, 8 hex digits); at most <see cref="MaxTerms"/> are kept. Two goals that share words share hashes, which is all the forecast compares.</summary>
public static class GoalTerms
{
    public const int MaxTerms = 40;

    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "that", "this", "from", "into", "are", "was", "not", "all", "any", "can", "its", "use", "add", "make", "when", "then", "should", "must", "will",
    };

    /// <returns>Sorted, so the same goal always gives the same list.</returns>
    public static IReadOnlyList<string> Of(string? goal)
    {
        if (string.IsNullOrWhiteSpace(goal))
            return [];
        HashSet<string> terms = new(StringComparer.Ordinal);
        foreach (var word in Words(goal))
            if (terms.Count < MaxTerms && !Filler.Contains(word))
                terms.Add(Hash(word));
        return [.. terms.Order(StringComparer.Ordinal)];
    }

    /// <summary>The share of the terms of either list that both have (Jaccard); 0 when either is empty.</summary>
    public static double Similarity(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (a is not { Count: > 0 } || b is not { Count: > 0 })
            return 0;
        var shared = a.Intersect(b, StringComparer.Ordinal).Count();
        return (double)shared / (a.Count + b.Count - shared);
    }

    private static IEnumerable<string> Words(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inWord = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (inWord && start < 0)
                start = i;
            else if (!inWord && start >= 0)
            {
                if (i - start >= 3)
                    yield return text[start..i].ToLowerInvariant();
                start = -1;
            }
        }
    }

    private static string Hash(string word)
    {
        var hash = 2166136261u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(word))
            hash = unchecked((hash ^ b) * 16777619u);
        return hash.ToString("x8", CultureInfo.InvariantCulture);
    }
}
