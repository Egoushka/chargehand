using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Modules.Events.Api;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Automation;

public sealed partial class IssueSweep
{
    // Four bytes a character at most: the pool refuses a body over 16 KiB.
    private const int MaxBodyChars = 4000;

    [GeneratedRegex(@"\b(?:fix(?:es|ed)?|close[sd]?|resolve[sd]?)\s+#(?<n>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex Fixes();

    private sealed class Tally
    {
        private int _ready, _needsYou, _blocked, _epic;

        public int Skipped { get; set; }

        public int Enqueued { get; set; }

        public int Deferred { get; set; }

        public string? Problem { get; set; }

        public List<SweepNeedsYou> NeedsYouItems { get; } = [];

        public void Count(string repository, IntakeIssue issue, SortResult result)
        {
            switch (result.Verdict)
            {
                case SweepVerdict.Ready: _ready++; break;
                case SweepVerdict.NeedsOwner:
                    _needsYou++;
                    NeedsYouItems.Add(new SweepNeedsYou(repository, issue.Number, issue.Title, result.Reason,
                        $"https://github.com/{repository}/issues/{issue.Number.ToString(CultureInfo.InvariantCulture)}"));
                    break;
                case SweepVerdict.Blocked: _blocked++; break;
                default: _epic++; break;
            }
        }

        public SweepRepoRow Row(string repository, string? problem) => new(repository, _ready, _needsYou, _blocked, _epic, Skipped, Enqueued, problem, Deferred);
    }

    /// <summary>What one repository's pass reads once: its open chargehand pull requests and its rules file.</summary>
    private sealed class RepositoryContext(string repository, AutomationRepository checkout, IReadOnlySet<int> pullIssues)
    {
        private bool _read;
        private string? _claudeMd;

        public string Repository => repository;

        public AutomationRepository Checkout => checkout;

        public IReadOnlySet<int> PullIssues => pullIssues;

        public string? ClaudeMd()
        {
            if (_read) return _claudeMd;
            _read = true;
            var path = Path.Combine(checkout.Path, "CLAUDE.md");
            try
            {
                _claudeMd = File.Exists(path) ? File.ReadAllText(path) : null;
            }
            // agent-checks: allow empty-catch -- CLAUDE.md is optional context for the sorting pass; one that cannot be read sorts without it
            catch (IOException)
            {
            }
            // agent-checks: allow empty-catch -- as above: an unreadable CLAUDE.md only means less context
            catch (UnauthorizedAccessException)
            {
            }

            return _claudeMd;
        }
    }

    private async Task<IReadOnlySet<int>> ChargehandPullIssuesAsync(string repository, CancellationToken ct)
    {
        var found = new HashSet<int>();
        foreach (var pull in await host.OpenPullsAsync(repository, ct))
        {
            if (!GitHubIntake.IsChargehandBranch(pull.HeadBranch, pull.SameRepository)) continue;
            foreach (Match m in Fixes().Matches(pull.Body))
                found.Add(int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture));
        }

        return found;
    }

    private async Task<bool> QueuedOrRunningAsync(RepositoryContext context, int number, CancellationToken ct) =>
        context.PullIssues.Contains(number)
        || intake.Tasks.Any(t => t.Repository == context.Repository && t.Issue == number && t.Phase != IntakePhase.Done)
        || await pool.HasOpenItemAsync(context.Repository, number, ct);

    /// <returns>The first open blocker (as <c>owner/repo#n</c>), and the references that could not be read, which do not block.</returns>
    private async Task<(string? Open, IReadOnlyList<string> Unreadable)> BlockersAsync(string repository, IntakeIssue issue, CancellationToken ct)
    {
        var unreadable = new List<string>();
        foreach (var reference in BlockerRefs.Parse(issue.Body, repository))
        {
            var name = $"{reference.Repository}#{reference.Number}";
            if (name == $"{repository}#{issue.Number}") continue;
            switch (await host.IssueStateAsync(reference.Repository, reference.Number, ct))
            {
                case IssueState.Open: return (name, unreadable);
                case null: unreadable.Add(name); break;
            }
        }

        return (null, unreadable);
    }

    private static (IReadOnlyList<string> Add, IReadOnlyList<string> Remove) LabelChanges(IntakeIssue issue, SortResult result)
    {
        var verdictLabels = new[] { SweepLabels.Ready, SweepLabels.NeedsYou, SweepLabels.Blocked, SweepLabels.Epic };
        var wanted = new List<string> { VerdictLabel(result.Verdict) };
        var sizeLabels = new[] { "size:S", "size:M", "size:L" };
        if (result.Size is "S" or "M" or "L") wanted.Add($"size:{result.Size}");
        var known = verdictLabels.Concat(sizeLabels).ToList();
        var add = wanted.Where(l => !Has(issue, l)).ToList();
        var remove = known.Where(l => !wanted.Contains(l) && Has(issue, l)).ToList();
        return (add, remove);
    }

    /// <summary>A changed verdict, or a first one the owner needs the reason for; a Ready issue's result is its pull request, and a label already there means an earlier sweep spoke.</summary>
    private static bool ShouldComment(IntakeIssue issue, SweepVerdict verdict, SweepVerdict? last)
    {
        if (Has(issue, VerdictLabel(verdict))) return false;
        return last is null ? verdict != SweepVerdict.Ready : last != verdict;
    }

    private static string VerdictLabel(SweepVerdict verdict) => verdict switch
    {
        SweepVerdict.Ready => SweepLabels.Ready,
        SweepVerdict.NeedsOwner => SweepLabels.NeedsYou,
        SweepVerdict.Blocked => SweepLabels.Blocked,
        _ => SweepLabels.Epic,
    };

    private static string CommentText(SortResult result, IReadOnlyList<string> unreadable)
    {
        var words = result.Verdict switch
        {
            SweepVerdict.Ready => "ready for an agent",
            SweepVerdict.NeedsOwner => "needs the owner",
            SweepVerdict.Blocked => "blocked",
            _ => "an epic to split",
        };
        var size = result.Size is "S" or "M" or "L" ? $" (size {result.Size})" : "";
        var note = unreadable.Count > 0 ? $" Could not read {string.Join(", ", unreadable)}, so it was not counted as a blocker." : "";
        return $"{CommentMarker}\nSweep verdict: {words}{size}. {result.Reason}{note}";
    }

    private static string ProblemLine(Exception ex)
    {
        var text = ex is IntakeException ? ex.Message.ReplaceLineEndings(" ") : ex.GetType().Name;
        return text.Length > 200 ? text[..200] : text;
    }

    private static void WriteCounts(Utf8JsonWriter w, SweepReport report)
    {
        w.WriteString("trigger", report.Trigger);
        w.WriteStartArray("repositories");
        foreach (var r in report.Repositories)
        {
            w.WriteStartObject();
            w.WriteString("repository", r.Repository);
            w.WriteNumber("ready", r.Ready);
            w.WriteNumber("needs_you", r.NeedsYou);
            w.WriteNumber("blocked", r.Blocked);
            w.WriteNumber("epic", r.Epic);
            w.WriteNumber("skipped", r.Skipped);
            w.WriteNumber("enqueued", r.Enqueued);
            w.WriteNumber("deferred", r.Deferred);
            if (r.Problem is not null) w.WriteString("problem", r.Problem);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private Task EmitAsync(string sweepId, string type, string suffix, Action<Utf8JsonWriter> data, CancellationToken ct) =>
        SweepEvents.EmitAsync(events, clock, sweepId, type, suffix, data, ct);
}
