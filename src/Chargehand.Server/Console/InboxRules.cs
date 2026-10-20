using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Server;

/// <summary>An inbox item before the rules, with what they match it on.</summary>
/// <param name="Path">The repository path of the request (a batch's for its tasks); null: the request named none.</param>
/// <param name="Text">The request's text in full: a task's goal, a run's question.</param>
/// <param name="Started">When the run, or the task's batch, started: "later" is measured from here.</param>
/// <param name="Code">The error code, <c>lost</c> or <c>partial</c>, for a failure.</param>
/// <param name="TaskIds">A batch's task ids, for a failure whose subject is the batch.</param>
/// <param name="Asked">For a batch's task: what its batch asked of it, the goal or else the ref (<see cref="InboxRules.AskedOf"/>); a later task matches on its id and this.</param>
internal sealed record InboxCandidate(InboxItem Item, string? Path, string Text, DateTimeOffset Started, string? Code, IReadOnlyList<string> TaskIds, string? Asked = null);

/// <summary>Why an item left the inbox without anyone dealing with it there (ADR 0064). Derived on every call from the run log and the learning records, nothing stored; a dismissal is the one stored
/// reason, and <see cref="ConsoleInbox"/> applies it.</summary>
internal sealed partial class InboxRules
{
    public const string Superseded = "superseded";
    public const string Infrastructure = "infrastructure";
    public const string Continued = "continued";
    public const string PrClosed = "pr_closed";

    /// <summary>The codes that say chargehand, its host or its setup failed, not the task: a later run on the same repository that finished ok shows the cause is gone. <c>lost</c> is a run whose
    /// process ended (a restart). <c>checkout_has_secrets</c>, budgets, verification and the session's own failures are about the task or the repository's content and stay.</summary>
    public static readonly IReadOnlySet<string> InfrastructureCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "lost", "internal", "checkout_invalid", "repository_not_allowed", "runtime_unavailable", "runtime_version_mismatch", "runtime_ambiguous", "provider_unavailable", "rate_limited",
        "sandbox_unavailable", "container_unavailable", "credential_unavailable",
    };

    private readonly RunIndex _index;
    private readonly List<LaterBatch> _batches;
    private readonly List<(string? Path, DateTimeOffset Started)> _ok;
    private readonly Dictionary<string, LearningEntry> _byRun;
    private readonly HashSet<string> _closedPulls;
    private readonly List<(string Pull, string Head, DateTimeOffset At)> _okHeads;

    /// <param name="learning">Every learning record, not only the caller's: a task's outcome where the batch's artifact lacks it.</param>
    public InboxRules(RunIndex index, IReadOnlyList<LearningEntry> learning)
    {
        _index = index;
        var learned = learning.Where(e => e.Record.Completed || e.Record.Pr is not null).ToLookup(e => e.Record.BatchId, e => e.Record.TaskId);
        _batches = [.. index.Batches.Select(s => LaterBatch.Of(s, index.RunOf(s.RunId), learned))];
        // A run that finished ok, of any kind and any account, or a batch with a task that did: proof that the checkout, the runtime and the host work for that repository.
        _ok = [.. index.Starts.Values.Where(s => index.RunOf(s.RunId)?.Result.Status == ResultStatus.Completed).Select(s => (s.Request.Context.Repository?.Path, s.Started))
            .Concat(_batches.Where(b => b.Ok.Count > 0).Select(b => (b.Path, b.Started)))];
        _byRun = learning.GroupBy(e => e.Record.RunId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        _closedPulls = learning.Where(e => e.Record.Pr is not null && e.PrOutcome is { } o && PrOutcomes.Resolved(o.Outcome)).Select(e => PullOf(e.Record.Pr!)).ToHashSet(StringComparer.Ordinal);
        // A pull request's head that a later run got through: a review of it that completed, or a task on it that completed.
        _okHeads = [.. index.Starts.Values.Where(s => index.RunOf(s.RunId)?.Result.Status == ResultStatus.Completed && ReviewedPull(s.Request.Text) is not null && s.Request.Context.Repository?.Commit is not null)
            .Select(s => (ReviewedPull(s.Request.Text)!, s.Request.Context.Repository!.Commit!, s.Started))
            .Concat(learning.Where(e => e.Record.Completed && e.Record.Pr?.HeadSha is not null).Select(e => (PullOf(e.Record.Pr!), e.Record.Pr!.HeadSha!, e.Record.At)))];
    }

    /// <summary><c>owner/name#number</c>, as a review's text and <see cref="ConsoleBatches.RepositoryName"/> spell it.</summary>
    private static string PullOf(LearningPr pr) => $"{ConsoleBatches.RepositoryName(pr.Remote)}#{pr.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>The pull request an intake review run (ADR 0051) names in its text, <c>Review pull request owner/name#12.</c>; null for any other run.</summary>
    internal static string? ReviewedPull(string text) => ReviewText().Match(text) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"^Review pull request (\S+#\d+)\.$")]
    private static partial Regex ReviewText();

    /// <summary>What a batch asked of its task <paramref name="task"/>: the goal, else the ref; a continuation asks neither. Null: the batch has no such task.</summary>
    public static string? AskedOf(StartRecord? batch, string task) =>
        batch?.Request.Driven?.Tasks.FirstOrDefault(t => t.Id == task) is { } asked ? asked.Goal ?? asked.Ref ?? "" : null;

    /// <summary>The rule that resolves the item, or null while it stays open.</summary>
    public string? Resolve(InboxCandidate c) => c.Item.Kind switch
    {
        "question" => Question(c),
        "failure" => Failure(c),
        _ => null,
    };

    /// <summary>A question is continued when a batch carries its answers or retried it; superseded when a later batch of the same account on the same repository asks a task with its id the same
    /// goal, whatever became of it: the newer task is the one to look at.</summary>
    private string? Question(InboxCandidate c)
    {
        var (batch, task) = (c.Item.BatchRunId!, c.Item.TaskId!);
        if (_index.ContinuationsOf(batch, task).Any() || _batches.Any(b => b.Text == $"Retry of {batch}/{task}."))
            return Continued;
        return _batches.Any(b => Later(b, c) && Same(b, c)) ? Superseded : null;
    }

    /// <summary>A failure about a pull request that was merged or closed is moot (<see cref="PrClosed"/>). It is superseded when a later run got through the same pull request at the same head (any account),
    /// when a batch of any account continued or retried the same task and it did its work there, or when a later run of the same account did what it failed to: a batch's task with the same id and goal that
    /// completed or opened a pull request, a run of its own with the same text that completed, or, for a whole batch, a later batch in which every task of it did. Else an infrastructure failure is resolved
    /// by any later run on the repository that finished ok.</summary>
    private string? Failure(InboxCandidate c)
    {
        var (pull, head) = PullAndHead(c);
        if (pull is not null && _closedPulls.Contains(pull))
            return PrClosed;
        if (head is not null && _okHeads.Any(o => o.Pull == pull && string.Equals(o.Head, head, StringComparison.OrdinalIgnoreCase) && o.At > c.Started))
            return Superseded;
        var superseded = c.Item.Subject switch
        {
            "task" => _batches.Any(b => Later(b, c) && Same(b, c) && b.Ok.Contains(c.Item.TaskId!)) || LaterRunOk(c) || AgainOk(c.Item.BatchRunId!, c.Item.TaskId!),
            "run" => LaterRunOk(c),
            "batch" => c.TaskIds.Count > 0 && _batches.Any(b => Later(b, c) && c.TaskIds.All(id => b.Ok.Contains(id) && b.Asked.GetValueOrDefault(id) == AskedOf(_index.Starts.GetValueOrDefault(c.Item.RunId!), id))),
            _ => false,
        };
        if (superseded)
            return Superseded;
        return c.Code is { } code && InfrastructureCodes.Contains(code) && _ok.Any(o => o.Started > c.Started && o.Path == c.Path) ? Infrastructure : null;
    }

    /// <summary>The pull request a failure is about and the head it failed at: a task's own pull request (its learning record), or the one a review run names, at the commit it reviewed.</summary>
    private (string? Pull, string? Head) PullAndHead(InboxCandidate c)
    {
        if (c.Item.Subject == "task" && c.Item.RunId is { } run && _byRun.GetValueOrDefault(run)?.Record.Pr is { } pr)
            return (PullOf(pr), pr.HeadSha);
        if (c.Item.Subject == "run" && ReviewedPull(c.Text) is { } reviewed)
            return (reviewed, _index.Starts.GetValueOrDefault(c.Item.RunId!)?.Request.Context.Repository?.Commit);
        return (null, null);
    }

    /// <summary>A batch of any account continued the task (<c>continue_from</c>) or retried it, and the task did its work there: the same task, run again.</summary>
    private bool AgainOk(string batch, string task) =>
        _index.ContinuationsOf(batch, task).Any(x => _batches.Any(b => b.RunId == x.Start.RunId && b.Ok.Contains(x.Task.Id)))
        || _batches.Any(b => b.Text == $"Retry of {batch}/{task}." && b.Ok.Contains(task));

    private static bool Later(LaterBatch b, InboxCandidate c) => b.Started > c.Started && b.Path == c.Path && b.Actor == c.Item.Actor;

    /// <summary>The batch asks a task of the candidate's id what the candidate's batch asked: an id such as <c>t1</c> alone names nothing across batches.</summary>
    private static bool Same(LaterBatch b, InboxCandidate c) => c.Asked is { } asked && b.Asked.TryGetValue(c.Item.TaskId!, out var other) && other == asked;

    private bool LaterRunOk(InboxCandidate c) =>
        _index.Starts.Values.Any(s => s.ParentRunId is null && s.Request.Driven is null && s.Started > c.Started && s.Actor == c.Item.Actor && s.Request.Context.Repository?.Path == c.Path
            && s.Request.Text == c.Text && _index.RunOf(s.RunId)?.Result.Status == ResultStatus.Completed);

    /// <param name="Ok">The tasks that completed or opened a pull request, from the batch's artifact and its learning records.</param>
    /// <param name="Asked">Task id to <see cref="AskedOf"/>.</param>
    private sealed record LaterBatch(string RunId, string? Path, DateTimeOffset Started, string? Actor, string Text, IReadOnlyDictionary<string, string> Asked, IReadOnlySet<string> Ok)
    {
        public static LaterBatch Of(StartRecord s, RunRecord? run, ILookup<string, string> learned)
        {
            var ok = learned[s.RunId].ToHashSet(StringComparer.Ordinal);
            if (ResultArtifacts.Content(run?.Result, "driven-batch") is { ValueKind: JsonValueKind.Object } artifact && artifact.TryGetProperty("tasks", out var rows) && rows.ValueKind == JsonValueKind.Array)
                foreach (var row in rows.EnumerateArray())
                    if (ResultArtifacts.String(row, "id") is { } id && (ResultArtifacts.String(row, "status") == "completed" || ResultArtifacts.String(row, "pr_url") is not null))
                        ok.Add(id);
            var asked = s.Request.Driven!.Tasks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => AskedOf(s, g.Key)!, StringComparer.Ordinal);
            return new(s.RunId, s.Request.Context.Repository?.Path, s.Started, s.Actor, s.Request.Text, asked, ok);
        }
    }
}
