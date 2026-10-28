using System.Globalization;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Server;

/// <summary>A choice an inbox card offers (ADR 0085): a server call, a command to copy, or a page to open; never a manual step described in words.</summary>
/// <param name="Id"><c>retry</c>, <c>take_over</c>, <c>answer</c>, <c>exclude_reviews</c>, <c>raise_review_budget</c>, <c>open</c> or <c>dismiss</c>.</param>
/// <param name="Effect">What choosing it does, in one sentence.</param>
/// <param name="EstimateUsd">What it is likely to spend, when it spends: the task's own spend last time.</param>
/// <param name="Call">The request that carries it out; null for a command or a page.</param>
/// <param name="Command">A command to run in a clone of the repository.</param>
/// <param name="Url">A page to open: the pull request, or the run in the console.</param>
public sealed record InboxOption(string Id, string Label, string Effect, decimal? EstimateUsd = null, InboxCall? Call = null, string? Command = null, string? Url = null);

/// <param name="Body">The JSON body, when the route takes one. An answer's body is the item's <c>answer</c> with the person's answers added.</param>
public sealed record InboxCall(string Method, string Path, object? Body = null);

/// <summary>The words for one error code: the sentence that ends "A task on o/r …", the cause, and which options can succeed.</summary>
/// <param name="Verb">What happened, after the subject: "stopped at its budget".</param>
/// <param name="Why">The cause in plain words.</param>
/// <param name="Retry">Sending the same task again can succeed without a person changing anything first.</param>
/// <param name="TakeOver">The task ran a session that may have pushed a branch a person can finish.</param>
internal sealed record CatalogEntry(string Verb, string Why, bool Retry, bool TakeOver = false);

/// <summary>What every inbox card says (ADR 0085): one entry per error code of <c>docs/guide/reference.md</c> and per code the inbox adds (<c>partial</c>, <c>lost</c>, <c>failed</c>), so the console and
/// every other reader of <c>inbox/v1</c> use the same words. <c>InboxCatalogTests</c> fails on a code with no entry.</summary>
internal static class InboxCatalog
{
    public const string Decide = "decide";
    public const string Fix = "fix";
    public const string HandledLane = "handled";

    private static readonly Dictionary<string, CatalogEntry> Entries = new(StringComparer.Ordinal)
    {
        ["runtime_unavailable"] = new("could not start its agent", "the agent runtime (OpenCode or Claude Code) did not answer or could not start on the server", true),
        ["runtime_version_mismatch"] = new("was refused before it started", "the agent runtime's version differs from the version the profile pins", false),
        ["runtime_ambiguous"] = new("was refused before it started", "more than one agent CLI is installed and the profile names none", false),
        ["provider_unavailable"] = new("stopped because the model provider was down", "the model provider answered 502, 503 or 504", true),
        ["rate_limited"] = new("stopped on a rate limit", "the model provider limited requests", true),
        ["repository_not_allowed"] = new("was refused", "the repository is outside the profile's repository_roots", false),
        ["checkout_invalid"] = new("could not check out its repository", "the path is not a git checkout, the commit is not in it, or the clone failed", true),
        ["checkout_has_secrets"] = new("was refused", "the checkout holds files that look like secrets, so the preset will not read it", false),
        ["cost_cap_reached"] = new("stopped at its budget", "it used up its dollar or token budget before it finished", true, TakeOver: true),
        ["deadline_exceeded"] = new("ran out of time", "a turn ran past its deadline", true, TakeOver: true),
        ["invalid_result"] = new("finished without a usable result", "the agent's answer did not fit the result contract after a repair turn, or a writing run changed no files", true),
        ["intake_failed"] = new("could not be planned", "intake produced no valid task spec after one retry", true),
        ["invalid_request"] = new("was refused", "the request named an unknown preset or runtime, or carried a block that does not match its hash", false),
        ["internal"] = new("failed inside chargehand", "an error chargehand has no code for; the run's log has it", true),
        ["sandbox_unavailable"] = new("was refused", "the server has no sandbox (sandbox-exec or bwrap) for a writing preset", false),
        ["verification_failed"] = new("failed its tests", "chargehand ran the tests in a fresh container and they failed, or the repository has no test command", true, TakeOver: true),
        ["container_unavailable"] = new("could not start its container", "no container engine answered, or the runner is down", true),
        ["credential_unavailable"] = new("was refused", "the server has no model credential, or no push credential for this repository", false),
        ["session_failed"] = new("ended without a usable branch", "the session ended with an error; its log has the details", true, TakeOver: true),
        ["session_stalled"] = new("stalled", "the session stopped making progress, repeated a tool call, or ran out of turns or time", true, TakeOver: true),
        ["push_rejected"] = new("could not push its branch", "the remote refused the branch, or its address is not an https or ssh URL", false),
        ["pr_failed"] = new("pushed a branch but opened no pull request", "the forge refused the draft pull request; the branch is pushed", true, TakeOver: true),
        ["cancelled"] = new("was cancelled", "a person or a halt cancelled it", true),
        ["tasks_incomplete"] = new("ended with tasks unfinished", "at least one of its tasks opened no pull request", false),
        ["merge_conflict"] = new("could not start", "merging the branches of the tasks it depends on conflicted", true),
        ["local_only"] = new("was refused", "the repository is local-only and the request asked to push", false),
        ["delivery_failed"] = new("kept its branch in the run's clone", "the branch could not be fetched into the repository; the error's action has the command", false),
        ["partial"] = new("stopped with part of its work", "the task ended partial, and no pull request holds the work", true, TakeOver: true),
        ["lost"] = new("lost its process", "the server stopped or restarted before it finished", true),
        ["failed"] = new("failed", "it ended without an error code; its log has the details", true),
    };

    private static readonly Dictionary<string, string> PullRequestReasons = new(StringComparer.Ordinal)
    {
        ["policy_ask"] = "the repository's integrator policy is ask, so chargehand does not merge it",
        ["test_integrity"] = "the change edits tests in a way that could hide a failure, so chargehand will not merge it",
        ["partial_draft"] = "the task stopped with part of its work, so the pull request is a draft",
    };

    /// <summary>Codes whose cause is the repository's own: one card per repository (ADR 0085).</summary>
    private static readonly HashSet<string> PerRepository = new(["checkout_has_secrets", "verification_failed", "push_rejected", "merge_conflict"], StringComparer.Ordinal);

    /// <summary>What failures share when one fix answers them all: the code, and the repository only where the cause is the repository's. A review's <c>cost_cap_reached</c> is the <c>review</c> preset's budget, so it is
    /// one cause across repositories.</summary>
    internal static string CauseKeyOf(string code, string? repository, bool review) =>
        review && code == "cost_cap_reached" ? "cost_cap_reached:review" : PerRepository.Contains(code) ? $"{code}:{repository}" : code;

    /// <summary>The entry for <paramref name="code"/>; null for a code the catalog does not know.</summary>
    public static CatalogEntry? Entry(string code) => Entries.GetValueOrDefault(code);

    public static string? PullRequestWhy(string reason) => PullRequestReasons.GetValueOrDefault(reason);

    /// <summary>An item a rule or a person resolved, as the Handled lane lists it: its one option puts it back.</summary>
    public static InboxItem Handled(InboxItem item, string reason) => item with
    {
        Lane = HandledLane,
        ResolvedBy = reason,
        Options = [new("undo", "Undo", "Puts it back in the inbox; a rule does not resolve it again.", Call: new InboxCall("POST", "/v1/inbox/restore", new { ids = new[] { item.Id } }))],
    };

    /// <summary>The item with its <c>what</c>, <c>why</c>, <c>options</c> and lane.</summary>
    /// <param name="learned">The task's learning record, for the spend of a retry and the budget it stopped at.</param>
    public static InboxItem Describe(InboxCandidate c, LearningRecord? learned)
    {
        var item = c.Item;
        var dismiss = new InboxOption("dismiss", "Dismiss", "Takes it out of the inbox for everyone who sees it; Undo puts it back.", Call: new InboxCall("POST", "/v1/inbox/dismiss", new { ids = new[] { item.Id } }));
        return item.Kind switch
        {
            "question" => item with
            {
                Lane = Decide,
                What = $"{Subject(c)} asked a question",
                Why = "its session cannot go on without an answer",
                Options = [new("answer", "Answer and continue", "Starts a batch that continues the task with your answer.", Call: new InboxCall("POST", "/v1/runs")), dismiss],
            },
            "pull_request" => item with
            {
                Lane = Decide,
                What = $"Pull request {item.Title} waits for you",
                Why = PullRequestWhy(item.Reason ?? "") ?? "chargehand will not merge it on its own",
                Options = [new("open", "Open pull request", "Opens it on the forge to review and merge.", Url: item.PullRequest?.Url), dismiss],
            },
            _ => Failure(c, learned, dismiss),
        };
    }

    private static InboxItem Failure(InboxCandidate c, LearningRecord? learned, InboxOption dismiss)
    {
        var item = c.Item;
        var entry = Entry(item.Reason ?? "failed") ?? Entries["failed"];
        var why = entry.Why;
        if (item.Reason == "cost_cap_reached" && learned?.Spend.UsdEstimate is { } spent)
            why += learned.Budget.Usd is { } cap ? $" (spent {Usd(spent)} of {Usd(cap)})" : $" (spent {Usd(spent)})";
        var options = new List<InboxOption>();
        var task = item.Subject == "task" && item.BatchRunId is not null && item.TaskId is not null;
        if (task && entry.Retry)
            options.Add(new("retry", learned?.Spend.UsdEstimate is { } usd ? $"Retry, ≈ {Usd(usd)}" : "Retry", "Starts a new batch of this one task, continuing its work if it kept any.",
                learned?.Spend.UsdEstimate, new InboxCall("POST", $"/v1/batches/{Uri.EscapeDataString(item.BatchRunId!)}/tasks/{Uri.EscapeDataString(item.TaskId!)}/retry")));
        if (task && entry.TakeOver)
            options.Add(new("take_over", "Take over", "Finish it yourself: the command checks out its branch in a worktree with a handoff note; it refuses a task that pushed no branch.",
                Command: $"chargehand takeover {item.BatchRunId}/{item.TaskId}"));
        var lane = Fix;
        if (item.Subject == "run" && InboxRules.ReviewedPull(c.Text) is { } pull)
        {
            var repository = pull[..pull.LastIndexOf('#')];
            if (item.Reason == "checkout_has_secrets")
                (options, lane) = ([.. options, new("exclude_reviews", $"Exclude {repository} from reviews", "Stops reviewing this repository's pull requests; Intake lists it, and Undo puts it back.",
                    Call: new InboxCall("POST", "/v1/intake/reviews/exclude", new { repository }))], Decide);
            else if (item.Reason == "cost_cap_reached" && learned?.Budget.Usd is { } spentCap && spentCap > 0)
                (options, lane) = ([.. options, new("raise_review_budget", $"Raise the review budget to {Usd(spentCap * 2)}", $"Lets one review spend up to {Usd(spentCap * 2)} instead of {Usd(spentCap)}; Intake shows it, and Undo restores the old one.",
                    Call: new InboxCall("POST", "/v1/intake/reviews/budget", new { usd = spentCap * 2 }))], Decide);
        }
        // Nothing on the card can fix it: the run's page has its log and result.
        if (options.Count == 0 && (task ? $"/console/runs/{Uri.EscapeDataString(item.BatchRunId!)}/tasks/{Uri.EscapeDataString(item.TaskId!)}" : item.RunId is { } run ? $"/console/runs/{Uri.EscapeDataString(run)}" : null) is { } page)
            options.Add(new("open", task ? "Open task" : "Open run", "Opens it with its result and its log.", Url: page));
        options.Add(dismiss);
        return item with { What = $"{Subject(c)} {entry.Verb}", Why = why, Options = options, Lane = lane, CauseKey = CauseKeyOf(item.Reason ?? "failed", item.Repository, item.Subject == "run" && InboxRules.ReviewedPull(c.Text) is not null) };
    }

    /// <summary>The sentence's subject: the review's pull request, or the kind of work and its repository.</summary>
    private static string Subject(InboxCandidate c)
    {
        if (c.Item.Subject == "run" && InboxRules.ReviewedPull(c.Text) is { } pull)
            return $"A review of pull request {pull}";
        var kind = c.Item.Kind == "question" ? "A task" : c.Item.Subject switch { "batch" => "A batch", "run" => "A run", _ => "A task" };
        return c.Item.Repository is { Length: > 0 } repository ? $"{kind} on {Repository(repository)}" : kind;
    }

    /// <summary>A path's last two segments (<c>owner/name</c>), so a server's checkout path never reaches a card.</summary>
    private static string Repository(string repository)
    {
        var parts = repository.TrimEnd('/').Split('/');
        return parts.Length > 2 ? string.Join('/', parts[^2..]) : repository;
    }

    private static string Usd(decimal usd) => "$" + usd.ToString("0.00", CultureInfo.InvariantCulture);
}
