using System.Text;
using System.Text.RegularExpressions;

namespace Chargehand.Bench;

/// <summary>The text a replay session sees: the pull request's title and body and the closed issues' text, with everything that points at the withheld tests taken out (cycle B spec, decision 2).</summary>
public static partial class ReplayGoal
{
    /// <summary>The goal, or null with the rejection reason when no honest goal is left.</summary>
    public static string? Build(ReplayPr pr, IReadOnlyCollection<string> oraclePaths, out string reason, out string detail)
    {
        var needles = Needles(oraclePaths);
        var title = PullRequestRef().Replace(pr.Title, "").Trim();
        reason = detail = "";
        if (needles.Any(n => title.Contains(n, StringComparison.OrdinalIgnoreCase)))
        {
            (reason, detail) = (ReplayRejection.GoalLeaksTests, "the title names a withheld test file");
            return null;
        }
        var parts = new List<string> { title };
        parts.AddRange(Scrub(pr.Body, needles));
        foreach (var issue in pr.Issues ?? [])
        {
            var text = string.Join("\n\n", new[] { issue.Title is { Length: > 0 } t ? $"Issue: {PullRequestRef().Replace(t, "").Trim()}" : "" }.Concat(Scrub(issue.Body, needles)).Where(s => s.Length > 0));
            if (text.Length > 0)
                parts.Add(text);
        }
        var goal = Truncate(string.Join("\n\n", parts.Where(p => p.Length > 0)));
        if (title.Length == 0 || goal.Length == 0)
        {
            (reason, detail) = (ReplayRejection.EmptyGoal, "nothing is left of the title and body");
            return null;
        }
        if (needles.FirstOrDefault(n => goal.Contains(n, StringComparison.OrdinalIgnoreCase)) is { } leak)
        {
            (reason, detail) = (ReplayRejection.GoalLeaksTests, $"the goal still contains '{leak}'");
            return null;
        }
        return goal;
    }

    /// <summary>The strings that would tell a session which tests exist: each oracle path, its file name and the name without its extension.</summary>
    internal static IReadOnlyList<string> Needles(IEnumerable<string> oraclePaths) =>
        [.. oraclePaths.SelectMany(p => p.EndsWith(".cs", StringComparison.Ordinal) ? new[] { p, Path.GetFileName(p), Path.GetFileNameWithoutExtension(p) } : [p, Path.GetFileName(p)])
            .Where(n => n.Length >= 4).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length)];

    private static IEnumerable<string> Scrub(string? text, IReadOnlyList<string> needles)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;
        var skipSection = false;
        var paragraph = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (Heading().IsMatch(line))
                skipSection = TestHeading().IsMatch(line);
            if (skipSection || Boilerplate().IsMatch(line) || needles.Any(n => line.Contains(n, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (line.Length == 0)
            {
                if (paragraph.Length > 0)
                    yield return paragraph.ToString().Trim();
                paragraph.Clear();
            }
            else
                paragraph.AppendLine(line);
        }
        if (paragraph.Length > 0)
            yield return paragraph.ToString().Trim();
    }

    private static string Truncate(string goal)
    {
        if (goal.Length <= TaskSetLoader.MaxGoalLength)
            return goal;
        var cut = goal.LastIndexOf("\n\n", TaskSetLoader.MaxGoalLength - 1, StringComparison.Ordinal);
        return goal[..(cut > 0 ? cut : TaskSetLoader.MaxGoalLength)].TrimEnd();
    }

    [GeneratedRegex(@"\s*\((?:#\d+|[A-Z][A-Z0-9]+-\d+)\)")]
    private static partial Regex PullRequestRef();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s*(test plan|tests?|testing|how (this|it) was tested|verification|checks?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TestHeading();

    [GeneratedRegex(@"Generated with \[?Claude Code|Co-Authored-By:|^\s*(closes|fixes|resolves)\s+\S*#\d+", RegexOptions.IgnoreCase)]
    private static partial Regex Boilerplate();
}
