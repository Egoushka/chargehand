using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.RunLog;

namespace Chargehand.Automation;

/// <summary>Starts driven batches from GitHub (ADR 0051). Each poll reads the watched repositories as the GitHub App: open issues that carry the trigger label, and the comments since a cursor. A label
/// applied by an allowed author, or a comment of an allowed author that mentions the bot, starts a batch of one task whose goal is the issue; a reply of an allowed author to the questions of a task
/// that asked starts a continuation with the reply as the answer. chargehand comments on the issue when the batch starts and when it ends. Everything it has acted on is recorded in
/// <see cref="IntakeState"/> before the comment about it, so a restart cannot start a trigger twice.</summary>
public sealed partial class GitHubIntake(AutomationGitHub settings, IIntakeHost host, IRunLog log, IRepositoryHeads heads, IntakeState state, TimeProvider? clock = null, Action<string>? warn = null,
    IntegratorPolicy? integrator = null)
{
    public const string Preset = "driven";

    /// <summary>Hidden in every comment intake writes, so intake never takes its own comment, or a quote of it, for a trigger.</summary>
    public const string Marker = "<!-- chargehand-intake -->";

    /// <summary>A batch with no run record and an owner that is gone is lost after this long (a restart gives the resumer time to write the batch's start again).</summary>
    public static readonly TimeSpan LostAfter = TimeSpan.FromMinutes(30);

    /// <summary>How long a comment waits for a round (the branch busy, a cap): then it is dropped.</summary>
    private static readonly TimeSpan PendingFor = TimeSpan.FromDays(7);

    private const int MaxDetail = 600;
    private const int MaxQuestions = 10;
    private const int MaxQuestion = 500;

    private readonly TimeProvider _time = clock ?? TimeProvider.System;
    private readonly Action<string> _warn = warn ?? (message => Console.Error.WriteLine($"chargehand: {message}"));
    private readonly Regex _mention = new($@"(?<!\w){Regex.Escape(settings.Mention)}(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private readonly HashSet<string> _allowed = new(settings.AllowedAuthors ?? [], StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Repository, int Number), IntakePull> _pulls = [];

    private enum Outcome { Started, Settled, Deferred, Skipped }

    /// <summary>Polls until <paramref name="ct"/> ends. A failed poll is reported and tried again at the next one.</summary>
    public async Task RunAsync(IBatchStarter starter, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollAsync(starter, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _warn($"intake: poll failed: {e.Message}");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.PollSeconds), _time, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One pass: report the batches that ended, then read each watched repository. A repository GitHub will not answer for is skipped until the next pass.</summary>
    public async Task PollAsync(IBatchStarter starter, CancellationToken ct)
    {
        _pulls.Clear();
        await ReportEndedAsync(ct);
        foreach (var (name, repository) in settings.Repositories ?? new Dictionary<string, AutomationRepository>())
        {
            try
            {
                await LabelTriggersAsync(name, repository, starter, ct);
                await CommentTriggersAsync(name, repository, starter, ct);
                if (settings.MaxReviewRounds > 0)
                {
                    await ReviewCommentsAsync(name, ct);
                    await RoundsAsync(name, starter, ct);
                }
            }
            catch (IntakeException e)
            {
                _warn($"intake: {name}: {e.Message}");
            }
        }
    }

    private bool Allowed(string login) => login.Length > 0 && _allowed.Contains(login);

    private bool Mentions(string body) => _mention.IsMatch(QuotedLines().Replace(body, ""));

    private async Task LabelTriggersAsync(string name, AutomationRepository repository, IBatchStarter starter, CancellationToken ct)
    {
        foreach (var issue in await host.LabeledAsync(name, settings.Label, ct))
        {
            var seen = $"{name}#{issue.Number.ToString(CultureInfo.InvariantCulture)}";
            if (issue.IsPullRequest || (state.LabelSeen.TryGetValue(seen, out var at) && at == issue.UpdatedAt))
                continue;
            var applied = await host.LastLabelEventAsync(name, issue.Number, settings.Label, ct);
            var key = applied is null ? null : $"label:{seen}:{applied.Id.ToString(CultureInfo.InvariantCulture)}";
            var outcome = Outcome.Settled;
            if (key is not null && !state.Handled.Contains(key))
            {
                if (!Allowed(applied!.Actor))
                    state.Handled.Add(key);
                else
                    outcome = await StartAsync(key, name, repository, issue, null, null, starter, ct);
            }
            if (outcome != Outcome.Deferred)
            {
                state.LabelSeen[seen] = issue.UpdatedAt;
                state.Save(_time.GetUtcNow());
            }
        }
    }

    private async Task CommentTriggersAsync(string name, AutomationRepository repository, IBatchStarter starter, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        // The first poll of a repository starts at that moment: comments from before it are not requests.
        var cursor = state.CommentCursor.TryGetValue(name, out var stored) ? stored : now;
        var next = cursor;
        DateTimeOffset? held = null;
        foreach (var comment in (await host.CommentsSinceAsync(name, cursor, ct)).OrderBy(c => c.Id))
        {
            var outcome = Outcome.Skipped;
            var key = $"comment:{name}:{comment.Id.ToString(CultureInfo.InvariantCulture)}";
            if (!state.Handled.Contains(key) && !comment.Body.Contains(Marker, StringComparison.Ordinal))
                outcome = await HandleCommentAsync(key, name, repository, comment, starter, ct);
            if (outcome == Outcome.Deferred)
                held = held is { } h && h < comment.UpdatedAt ? h : comment.UpdatedAt;
            else if (comment.UpdatedAt > next)
                next = comment.UpdatedAt;
        }
        // A comment that has to wait for a slot is read again, from its own time.
        state.CommentCursor[name] = held ?? next;
        state.Save(now);
    }

    private async Task<Outcome> HandleCommentAsync(string key, string name, AutomationRepository repository, IntakeComment comment, IBatchStarter starter, CancellationToken ct)
    {
        var task = state.LatestFor(name, comment.IssueNumber);
        var answers = task is { Phase: IntakePhase.Asked, AskedCommentId: { } asked } && comment.Id > asked;
        if (!answers && !Mentions(comment.Body))
            return Outcome.Skipped;
        if (!Allowed(comment.Author))
        {
            state.Handled.Add(key);
            return Outcome.Settled;
        }
        if (answers)
            return await ContinueAsync(key, name, task!, comment, starter, ct);
        var issue = await host.GetAsync(name, comment.IssueNumber, ct);
        if (issue.IsPullRequest)
        {
            state.Handled.Add(key);
            if (settings.MaxReviewRounds > 0)
                await QueueAsync(new PendingReview($"c{comment.Id.ToString(CultureInfo.InvariantCulture)}", name, issue.Number, comment.Author, comment.Body, null, null, comment.UpdatedAt), ct);
            return Outcome.Settled;
        }
        return await StartAsync(key, name, repository, issue, comment.Body, comment.Author, starter, ct);
    }

    private async Task<Outcome> StartAsync(string key, string name, AutomationRepository repository, IntakeIssue issue, string? comment, string? commentAuthor, IBatchStarter starter, CancellationToken ct)
    {
        if (state.LatestFor(name, issue.Number) is { Phase: IntakePhase.Running } active)
        {
            await NotifyOnceAsync($"active:{key}", name, issue.Number, $"chargehand is already working on this issue (run `{active.BatchRunId}`); it will answer here when that run ends.", ct);
            state.Handled.Add(key);
            return Outcome.Settled;
        }
        // A repository the integrator merges into does not take work from text an allowed author did not write: the issue's body steers the session, and a merge is the end of a check, not of a review.
        if (integrator?.Resolve(RemoteOf(name)) == IntegrationMode.Merge && !Allowed(issue.Author))
        {
            await NotifyOnceAsync($"author:{key}", name, issue.Number, $"chargehand did not start: this repository merges chargehand's pull requests once their checks pass, so it only works from an issue written by an allowed author, and this one was written by {issue.Author}.", ct);
            state.Handled.Add(key);
            return Outcome.Settled;
        }
        if (await CapsAsync(key, name, issue.Number, $"Comment {settings.Mention} again later.", ct) is { } held)
            return held;
        string commit;
        try
        {
            commit = await heads.CurrentCommitAsync(repository.Path, ct);
        }
        catch (IntakeException e)
        {
            _warn($"intake: {name}#{issue.Number}: {e.Message}");
            return Outcome.Deferred;
        }
        var size = IntakeGoal.SizeOf(issue.Labels) ?? settings.BudgetClass;
        var taskId = TaskId(issue.Number);
        var goal = IntakeGoal.Issue(name, issue, comment, commentAuthor);
        return await LaunchAsync(key, name, issue.Number, new RepositoryRef(repository.Path, commit), new DrivenTask(taskId, Goal: goal, Size: size), run => new IntakeTask(name, issue.Number, run, taskId, _time.GetUtcNow(), size),
            starter, run => $"chargehand started run `{run}` (task `{taskId}`). It works on its own branch and opens a draft pull request when it is done, or asks here if it needs an answer.", null, null, ct);
    }

    private async Task<Outcome> ContinueAsync(string key, string name, IntakeTask earlier, IntakeComment reply, IBatchStarter starter, CancellationToken ct)
    {
        if (await CapsAsync(key, name, earlier.Issue, "Reply again later.", ct) is { } held)
            return held;
        // The continuation starts at the commit the earlier task started at: its kept work is a branch from there.
        if ((await log.ReadAsync(earlier.BatchRunId, ct)).Start?.Request.Context.Repository is not { } repository)
        {
            await NotifyOnceAsync($"gone:{key}", name, earlier.Issue, $"chargehand cannot continue run `{earlier.BatchRunId}`: it is no longer in the run log. Comment {settings.Mention} to start the task again.", ct);
            state.Replace(earlier, earlier with { Phase = IntakePhase.Done });
            state.Handled.Add(key);
            return Outcome.Settled;
        }
        var taskId = TaskId(earlier.Issue);
        var task = new DrivenTask(taskId, Size: earlier.Size, Answers: [IntakeGoal.Answer(reply.Body)], ContinueFrom: new ContinueFrom(earlier.BatchRunId, earlier.TaskId));
        return await LaunchAsync(key, name, earlier.Issue, repository, task, run => earlier with { BatchRunId = run, TaskId = taskId, Started = _time.GetUtcNow(), Phase = IntakePhase.Running, AskedCommentId = null, PrUrl = null },
            starter, run => $"chargehand continues with your answer: run `{run}` (task `{taskId}`), on the same branch.", earlier, null, ct);
    }

    /// <summary>Waits or refuses where a cap says so: null when the batch may start.</summary>
    private async Task<Outcome?> CapsAsync(string key, string name, int issue, string retry, CancellationToken ct)
    {
        if (state.Tasks.Count(t => t.Phase == IntakePhase.Running) >= settings.MaxConcurrent)
        {
            await NotifyOnceAsync($"wait:{key}", name, issue, $"chargehand is waiting for a free slot: {settings.MaxConcurrent.ToString(CultureInfo.InvariantCulture)} triggered batches are running. This starts when one ends.", ct);
            return Outcome.Deferred;
        }
        if (state.Starts.Count(s => s > _time.GetUtcNow() - TimeSpan.FromDays(1)) >= settings.MaxStartsPerDay)
        {
            await NotifyOnceAsync($"cap:{key}", name, issue, $"chargehand did not start: {settings.MaxStartsPerDay.ToString(CultureInfo.InvariantCulture)} triggered batches have started in the last 24 hours, which is the limit. {retry}", ct);
            state.Handled.Add(key);
            return Outcome.Settled;
        }
        return null;
    }

    private async Task<Outcome> LaunchAsync(string key, string name, int issue, RepositoryRef repository, DrivenTask task, Func<string, IntakeTask> track, IBatchStarter starter, Func<string, string> announce,
        IntakeTask? replaces, Action? settled, CancellationToken ct)
    {
        var request = new RunRequest("request/v1", $"Triggered from GitHub: {name}#{issue.ToString(CultureInfo.InvariantCulture)}.", new RequestContext(false, Preset, Repository: repository),
            Driven: new RequestDriven([task], MaxParallel: 1, MaxTokensTotal: settings.MaxTokensTotal, MaxUsdTotal: settings.MaxUsdTotal));
        var started = await starter.StartAsync(request, ct);
        if (started.RunId is not { } run)
        {
            _warn($"intake: {name}#{issue}: the server did not start the batch: {started.Refusal}");
            return Outcome.Deferred;
        }
        var now = _time.GetUtcNow();
        if (replaces is not null)
            state.Replace(replaces, replaces with { Phase = IntakePhase.Done });
        state.Tasks.Add(track(run));
        state.Starts.Add(now);
        state.Handled.Add(key);
        settled?.Invoke();
        state.Save(now);
        await TryCommentAsync(name, issue, announce(run), ct);
        return Outcome.Started;
    }

    /// <summary>Reads the inline review comments of the repository's pull requests since the cursor and queues those that ask a chargehand pull request for a change (ADR 0051). No mention is needed: a review
    /// comment of an allowed author is a request. A reply inside a thread is discussion and needs the mention.</summary>
    private async Task ReviewCommentsAsync(string name, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var cursor = state.ReviewCursor.TryGetValue(name, out var stored) ? stored : now;
        var next = cursor;
        foreach (var comment in (await host.ReviewCommentsSinceAsync(name, cursor, ct)).OrderBy(c => c.Id))
        {
            var key = $"review:{name}:{comment.Id.ToString(CultureInfo.InvariantCulture)}";
            if (comment.UpdatedAt > next)
                next = comment.UpdatedAt;
            if (state.Handled.Contains(key) || comment.Body.Contains(Marker, StringComparison.Ordinal))
                continue;
            state.Handled.Add(key);
            if (Allowed(comment.Author) && (!comment.IsReply || Mentions(comment.Body)))
                await QueueAsync(new PendingReview($"r{comment.Id.ToString(CultureInfo.InvariantCulture)}", name, comment.PullNumber, comment.Author, comment.Body, comment.Path, comment.Line, comment.UpdatedAt), ct);
        }
        state.ReviewCursor[name] = next;
        state.Save(now);
    }

    /// <summary>Keeps a comment for a round when the pull request is a chargehand's own: its head branch is <c>chargehand/*</c> in the repository itself, not a fork.</summary>
    private async Task QueueAsync(PendingReview comment, CancellationToken ct)
    {
        if (!IsChargehandBranch(await PullAsync(comment.Repository, comment.Pull, ct)))
            return;
        state.Pending.Add(comment);
        state.Save(_time.GetUtcNow());
    }

    private async Task<IntakePull> PullAsync(string repository, int number, CancellationToken ct)
    {
        if (!_pulls.TryGetValue((repository, number), out var pull))
            _pulls[(repository, number)] = pull = await host.GetPullAsync(repository, number, ct);
        return pull;
    }

    private static bool IsChargehandBranch(IntakePull pull) =>
        BranchName().IsMatch(pull.HeadBranch) && !pull.HeadBranch.Contains("..", StringComparison.Ordinal) && string.Equals(pull.HeadRepository, pull.BaseRepository, StringComparison.OrdinalIgnoreCase);

    /// <summary>Starts a review round for each pull request that has comments waiting (ADR 0051): one continuation of the task that opened it, with the comments as its answers. A comment waits while the
    /// branch is busy or a cap says wait; it is dropped when the pull request is closed, when its task cannot be found, when the round cap is used, or when a round already took it.</summary>
    private async Task RoundsAsync(string name, IBatchStarter starter, CancellationToken ct)
    {
        state.Pending.RemoveAll(p => _time.GetUtcNow() - p.At > PendingFor);
        foreach (var group in state.Pending.Where(p => p.Repository == name).GroupBy(p => p.Pull).ToList())
        {
            try
            {
                await RoundAsync(name, group.Key, [.. group], starter, ct);
            }
            catch (IntakeException e)
            {
                // One pull request GitHub will not answer for keeps its comments waiting and does not hold up the others.
                _warn($"intake: {name}#{group.Key}: {e.Message}");
            }
        }
        state.Save(_time.GetUtcNow());
    }

    private async Task RoundAsync(string name, int number, IReadOnlyList<PendingReview> waiting, IBatchStarter starter, CancellationToken ct)
    {
        void Drop(IEnumerable<PendingReview> items) => state.Pending.RemoveAll(p => items.Contains(p));
        var pull = await host.GetPullAsync(name, number, ct);
        if (!pull.Open || !IsChargehandBranch(pull))
        {
            Drop(waiting);
            return;
        }
        var found = await FindTaskAsync(pull.HeadBranch, ct);
        if (found is null)
        {
            await NotifyOnceAsync($"notask:{name}#{number}", name, number, $"chargehand cannot continue `{pull.HeadBranch}`: no finished task for this branch is in the run log, so it does not know what the branch was for.", ct);
            Drop(waiting);
            return;
        }
        // The branch is busy while its batch runs (the CI fixer works in it) or a round of ours is: the comments wait, and the next round takes them all.
        if (!found.Finished || state.Tasks.Any(t => t.Branch == pull.HeadBranch && t.Phase == IntakePhase.Running))
        {
            await NotifyOnceAsync($"busy:{name}#{number}:{found.Batch}", name, number, $"chargehand is still working on `{pull.HeadBranch}` (run `{found.Batch}`). The review comments are picked up when it ends.", ct);
            return;
        }
        var bodies = await host.CommentBodiesAsync(name, number, ct);
        var reviewed = FixMarker.ReviewedComments(bodies);
        var fresh = waiting.Where(w => !reviewed.Contains(w.Id)).OrderBy(w => w.At).ThenBy(w => w.Id, StringComparer.Ordinal).ToList();
        if (fresh.Count == 0)
        {
            Drop(waiting);
            return;
        }
        var used = FixMarker.Parse(bodies);
        if (used.Count >= DrivenCiFix.AttemptCap || used.Count(a => a.JobsKey == FixMarker.ReviewKey) >= settings.MaxReviewRounds)
        {
            await NotifyOnceAsync($"rounds:{name}#{number}", name, number,
                $"chargehand is not starting another round for these comments: {used.Count} of {DrivenCiFix.AttemptCap} attempts on this pull request are used (CI fix attempts and review rounds count together). A person needs to take it from here.", ct);
            Drop(waiting);
            return;
        }
        var key = $"round:{name}#{number}:{fresh[0].Id}";
        if (await CapsAsync(key, name, number, "Comment again later.", ct) is { } held)
        {
            if (held == Outcome.Settled)
                Drop(waiting);
            return;
        }
        if ((await log.ReadAsync(found.Batch, ct)).Start?.Request.Context.Repository is not { } repository)
        {
            await NotifyOnceAsync($"gone:{name}#{number}", name, number, $"chargehand cannot continue run `{found.Batch}`: it is no longer in the run log.", ct);
            Drop(waiting);
            return;
        }
        var taken = fresh.Take(IntakeGoal.MaxRoundComments).ToList();
        var round = used.Count == 0 ? 1 : used.Max(a => a.Number) + 1;
        var taskId = $"pr-{number.ToString(CultureInfo.InvariantCulture)}";
        var task = new DrivenTask(taskId, Size: settings.BudgetClass, Answers: [.. taken.Select(t => IntakeGoal.ReviewAnswer(t.Author, t.Path, t.Line, t.Body))],
            ContinueFrom: new ContinueFrom(found.Batch, found.Task));
        var authors = string.Join(", ", taken.Select(t => t.Author).Distinct(StringComparer.OrdinalIgnoreCase).Select(a => $"@{a}"));
        await LaunchAsync(key, name, number, repository, task, run => new IntakeTask(name, number, run, taskId, _time.GetUtcNow(), settings.BudgetClass, Branch: pull.HeadBranch, Round: round), starter,
            run => $"{FixMarker.ReviewRound(round, taken.Select(t => t.Id))}\nchargehand is starting review round {round.ToString(CultureInfo.InvariantCulture)} of {DrivenCiFix.AttemptCap.ToString(CultureInfo.InvariantCulture)} "
                + $"(run `{run}`) for {taken.Count.ToString(CultureInfo.InvariantCulture)} comment(s) by {authors}, on `{pull.HeadBranch}`. Rounds and CI fix attempts share the cap of {DrivenCiFix.AttemptCap.ToString(CultureInfo.InvariantCulture)} per pull request.",
            null, () => Drop(waiting.Where(w => taken.Any(t => t.Id == w.Id) || reviewed.Contains(w.Id))), ct);
    }

    /// <param name="Batch">The batch that holds the task's last run.</param>
    /// <param name="Task">The task's id in that batch; empty while the batch has not ended.</param>
    /// <param name="Finished">The batch has ended, so nothing else is working on the branch.</param>
    private sealed record BranchTask(string Batch, string Task, bool Finished);

    /// <summary>The task whose last run pushed <paramref name="branch"/>, from the run log: the newest run that names it, its batch, and the task's id there.</summary>
    private async Task<BranchTask?> FindTaskAsync(string branch, CancellationToken ct)
    {
        foreach (var run in (await log.ListAsync(new RunListQuery(Limit: 500), ct)).Where(r => r.Branch == branch && r.ParentRunId is not null).OrderByDescending(r => r.StartedAt))
        {
            var batch = await log.ReadAsync(run.ParentRunId!, ct);
            if (batch.Run is null)
                return new BranchTask(run.ParentRunId!, "", false);
            if (TaskOf(batch.Run.Result, run.RunId) is { } task)
                return new BranchTask(run.ParentRunId!, task, true);
        }
        return null;
    }

    /// <summary>The id of the task whose last run is <paramref name="run"/>, in a batch result's <c>driven-batch</c> artifact.</summary>
    private static string? TaskOf(ResultContract result, string run)
    {
        if (result.Artifacts.FirstOrDefault(a => a.Kind == "driven-batch")?.Content is not { } content)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(content);
            foreach (var row in doc.RootElement.GetProperty("tasks").EnumerateArray())
                if (Text(row, "run_id") == run)
                    return Text(row, "id");
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // A result this reader cannot read names no task.
        }
        return null;
    }

    private static RemoteRepository RemoteOf(string name) => new(name[..name.IndexOf('/', StringComparison.Ordinal)], name[(name.IndexOf('/', StringComparison.Ordinal) + 1)..]);

    private static string TaskId(int issue) => $"issue-{issue.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Says how the batches that ended ended, and frees their slots.</summary>
    private async Task ReportEndedAsync(CancellationToken ct)
    {
        foreach (var task in state.Tasks.Where(t => t.Phase == IntakePhase.Running).ToList())
        {
            try
            {
                var entry = await log.ReadAsync(task.BatchRunId, ct);
                if (entry.Run is { } run)
                    await FinishAsync(task, run.Result, ct);
                else if (_time.GetUtcNow() - task.Started > LostAfter && (entry.Start is null || !entry.Start.OwnerAlive()))
                    await FinishAsync(task, null, ct);
            }
            catch (IntakeException e)
            {
                _warn($"intake: {task.Repository}#{task.Issue}: {e.Message}");
            }
        }
    }

    private async Task FinishAsync(IntakeTask task, ResultContract? result, CancellationToken ct)
    {
        var row = result is null ? null : RowOf(result, task.TaskId);
        var phase = IntakePhase.Done;
        string text;
        if (result is null)
            text = $"chargehand lost run `{task.BatchRunId}`: the server that ran it stopped and did not take it up again. Comment {settings.Mention} to start the task again.";
        else if (row is null)
            text = $"chargehand's run `{task.BatchRunId}` ended without starting the task: {Cut(result.Error?.Message ?? result.Summary, MaxDetail)}";
        else if (row.Status == "completed" && task.Round is { } round)
            text = $"chargehand finished review round {round.ToString(CultureInfo.InvariantCulture)} (run `{task.BatchRunId}`): the changes are on {row.PrUrl ?? task.Branch}"
                + (row.RunId is null || (await log.ReadAsync(row.RunId, ct)).Run?.Result.Summary is not { Length: > 0 } summary ? "" : $"\n\n{Cut(summary, MaxDetail)}");
        else if (row.Status == "completed")
            text = $"chargehand finished run `{task.BatchRunId}`: draft pull request {row.PrUrl}";
        else if (row.Status == "needs_input")
        {
            phase = IntakePhase.Asked;
            var questions = row.RunId is null ? [] : (await log.ReadAsync(row.RunId, ct)).Run?.Result.OpenQuestions ?? [];
            text = $"chargehand needs an answer before it can go on (run `{task.BatchRunId}`):\n\n"
                + string.Join('\n', (questions.Count == 0 ? [row.Detail ?? "the session did not say what it needs"] : questions).Take(MaxQuestions).Select(q => $"- {Cut(q, MaxQuestion)}"))
                + $"\n\nReply on this issue. An answer from an allowed author continues the task on the same branch.";
        }
        else if (task.Round is { } failedRound)
            text = $"chargehand's review round {failedRound.ToString(CultureInfo.InvariantCulture)} (run `{task.BatchRunId}`) ended without pushing changes ({row.Status.Replace('_', ' ')}): {Cut(row.Detail ?? "no detail", MaxDetail)}";
        else
            text = $"chargehand's run `{task.BatchRunId}` ended without a finished pull request ({row.Status.Replace('_', ' ')}): {Cut(row.Detail ?? "no detail", MaxDetail)}"
                + (row.PrUrl is null ? "" : $"\n\nWhat was done so far: {row.PrUrl}");
        long? commentId = null;
        try
        {
            commentId = await host.CommentAsync(task.Repository, task.Issue, $"{Marker}\n{text}", ct);
        }
        catch (IntakeException e) when (e.Status is 404 or 410)
        {
            // The issue is gone: there is nobody to ask, and nothing more to say.
            phase = IntakePhase.Done;
        }
        state.Replace(task, task with { Phase = phase, AskedCommentId = phase == IntakePhase.Asked ? commentId : null, PrUrl = row?.PrUrl, Branch = row?.Branch });
        state.Save(_time.GetUtcNow());
    }

    private sealed record Row(string Status, string? RunId, string? PrUrl, string? Branch, string? Detail);

    private static Row? RowOf(ResultContract result, string taskId)
    {
        if (result.Artifacts.FirstOrDefault(a => a.Kind == "driven-batch")?.Content is not { } content)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(content);
            foreach (var row in doc.RootElement.GetProperty("tasks").EnumerateArray())
                if (Text(row, "id") == taskId)
                    return new Row(Text(row, "status") ?? "failed", Text(row, "run_id"), Text(row, "pr_url"), Text(row, "branch"), Text(row, "detail"));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // A result this reader cannot read is reported as a run that never started the task.
        }
        return null;
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + " [cut]";

    /// <summary>A note that is written once per key. The key is kept only after the comment is up, so a comment GitHub refused is tried again.</summary>
    private async Task NotifyOnceAsync(string key, string repository, int issue, string text, CancellationToken ct)
    {
        if (state.Notified.Contains(key))
            return;
        if (await TryCommentAsync(repository, issue, text, ct))
        {
            state.Notified.Add(key);
            state.Save(_time.GetUtcNow());
        }
    }

    private async Task<bool> TryCommentAsync(string repository, int issue, string text, CancellationToken ct)
    {
        try
        {
            await host.CommentAsync(repository, issue, $"{Marker}\n{text}", ct);
            return true;
        }
        catch (IntakeException e)
        {
            _warn($"intake: {repository}#{issue}: the comment was not posted: {e.Message}");
            return false;
        }
    }

    [GeneratedRegex(@"^[ \t]*>.*$", RegexOptions.Multiline)]
    private static partial Regex QuotedLines();

    [GeneratedRegex(@"^chargehand/[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex BranchName();
}
