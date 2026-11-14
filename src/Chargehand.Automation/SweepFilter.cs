using System.Text.RegularExpressions;

namespace Chargehand.Automation;

/// <param name="Repository">The repository as <c>owner/name</c>.</param>
public sealed record IssueRef(string Repository, int Number);

/// <summary>Why the sweep leaves an issue alone before it asks a model.</summary>
public enum SweepSkip { None, Owner, InProgress, Skip, Claimed, AlreadyQueued }

/// <summary>Finds the issues a body says it waits on.</summary>
public static partial class BlockerRefs
{
    /// <summary>The <c>#n</c> and <c>owner/repo#n</c> references after "blocked by", "depends on" or "after", within one sentence.</summary>
    public static IReadOnlyList<IssueRef> Parse(string body, string repository)
    {
        var found = new List<IssueRef>();
        foreach (Match sentence in Sentence().Matches(body))
        {
            var rest = sentence.Groups["rest"].Value;
            if (Bare().IsMatch(rest)) rest += ListItems(body[(sentence.Index + sentence.Length)..]);
            foreach (Match reference in Reference().Matches(rest))
            {
                var repo = reference.Groups["repo"].Success ? reference.Groups["repo"].Value : repository;
                if (!int.TryParse(reference.Groups["n"].Value, out var number)) continue;
                var issue = new IssueRef(repo, number);
                if (!found.Contains(issue)) found.Add(issue);
            }
        }

        return found;
    }

    // A keyword that ends its line ("Blocked by:") introduces the list items that follow it.
    private static string ListItems(string after)
    {
        var items = new List<string>();
        foreach (var line in after.Split('\n').Skip(1))
        {
            if (!ListMarker().IsMatch(line)) break;
            items.Add(line);
        }

        return " " + string.Join(' ', items);
    }

    [GeneratedRegex(@"^[\s\p{P}\p{S}]*$")]
    private static partial Regex Bare();

    [GeneratedRegex(@"^\s*(?:[-*+]|\d+\.)\s")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:blocked by|depends on|after)\b(?<rest>.*?)(?=[.!?](?:\s|$)|\r|\n|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Sentence();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?<repo>[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9._-]+)?#(?<n>\d{1,9})\b")]
    private static partial Regex Reference();
}

/// <summary>The checks that keep an issue away from the model.</summary>
public static class SweepFilter
{
    /// <param name="claimed">A live claim covers the issue.</param>
    /// <param name="queuedOrRunning">An open item, run or chargehand pull request already exists for it.</param>
    public static SweepSkip Skip(IntakeIssue issue, bool claimed, bool queuedOrRunning)
    {
        if (issue.Labels.Contains("you")) return SweepSkip.Owner;
        if (issue.Labels.Contains("in-progress")) return SweepSkip.InProgress;
        if (issue.Labels.Contains(SweepLabels.Skip)) return SweepSkip.Skip;
        if (claimed) return SweepSkip.Claimed;
        return queuedOrRunning ? SweepSkip.AlreadyQueued : SweepSkip.None;
    }
}
