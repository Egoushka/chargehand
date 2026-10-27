using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.RunLog;

namespace Chargehand.Automation;

/// <summary>Starts driven batches from GitHub (ADR 0051). Each poll reads the watched repositories as the GitHub App: open issues that carry the trigger label, and the comments since a cursor. A label
/// applied by an allowed author, or a comment of an allowed author that mentions the bot, starts a batch of one task whose goal is the issue; a reply of an allowed author to the questions of a task
/// that asked starts a continuation with the reply as the answer. chargehand comments on the issue when the batch starts and when it ends. Everything it has acted on is recorded in
/// <see cref="IntakeState"/> before the comment about it, so a restart cannot start a trigger twice. The watched repositories are the profile's, and with <c>discover</c> every one the App can see, each
/// cloned into <paramref name="clones"/>; one switched off in <paramref name="sources"/> is not read.</summary>
public sealed partial class GitHubIntake(AutomationGitHub settings, IIntakeHost host, IRunLog log, IRepositoryHeads heads, IntakeState state, TimeProvider? clock = null, Action<string>? warn = null,
    IntegratorPolicy? integrator = null, IntakeSources? sources = null, ManagedClones? clones = null, Action<string>? info = null)
{
    public const string Preset = "driven";

    /// <summary>Hidden in every comment intake writes, so intake never takes its own comment, or a quote of it, for a trigger.</summary>
    public const string Marker = "<!-- chargehand-intake -->";

    /// <summary>A batch with no run record and an owner that is gone is lost after this long (a restart gives the resumer time to write the batch's start again).</summary>
    public static readonly TimeSpan LostAfter = TimeSpan.FromMinutes(30);

    /// <summary>How long a comment waits for a round (the branch busy, no free slot): then it is dropped.</summary>
    private static readonly TimeSpan PendingFor = TimeSpan.FromDays(7);

    private const int MaxDetail = 600;
    private const int MaxQuestions = 10;
    private const int MaxQuestion = 500;

    private readonly TimeProvider _time = clock ?? TimeProvider.System;
    private readonly Action<string> _warn = warn ?? Warned;
    private readonly Action<string> _info = info ?? Informed;
    private readonly Regex _mention = new($@"(?<!\w){Regex.Escape(settings.Mention)}(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private readonly HashSet<string> _allowed = new(settings.AllowedAuthors ?? [], StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Repository, int Number), IntakePull> _pulls = [];
    private readonly CheckoutSources _checkouts = new(heads, clones, "intake", warn ?? Warned, info ?? Informed);
    private readonly PullReviews _reviews = new(settings, host, log, state, clock ?? TimeProvider.System, warn ?? Warned);

    /// <summary>The waiting triggers this poll read again; one of a repository that was not is withdrawn.</summary>
    private readonly HashSet<string> _stillWaiting = [];

    private static void Warned(string message) => Console.Error.WriteLine($"chargehand: {message}");

    private static void Informed(string message) => Console.Out.WriteLine($"chargehand: {message}");

    private static readonly TimeSpan PollHeartbeat = TimeSpan.FromHours(1);

    private (int Repositories, int Running)? _polled;
    private DateTimeOffset _polledAt;

    private enum Outcome { Started, Settled, Deferred, Skipped }

    private IReadOnlyDictionary<string, WatchedRepository> _watched = Listed(settings);

    /// <summary>The switches of the watched repositories, which the server's <c>/v1/intake/sources</c> reads and sets.</summary>
    public IntakeSources Sources { get; } = sources ?? new IntakeSources();

    /// <summary>Whether the App's repositories are watched as well as the profile's.</summary>
    public bool Discovers => settings.Discover && clones is not null;

    /// <summary>The repositories the last poll watched (before the first, the profile's), switched off or not.</summary>
    public IReadOnlyDictionary<string, WatchedRepository> Watched => _watched;

    /// <summary>Polls until <paramref name="ct"/> ends. A failed poll is reported and tried again at the next one.</summary>
    public async Task RunAsync(IBatchStarter starter, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollAsync(starter, ct);
            }
            // An HttpClient timeout is a cancellation too: only the caller's own ends the loop.
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
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

    /// <summary>The poll line says something only when a count changed, or as a heartbeat once an hour: every poll's line was 52 of 90 server log lines (CHARGEHAND-316).</summary>
    private void LogPoll(int repositories, int running)
    {
        var now = _time.GetUtcNow();
        if (_polled == (repositories, running) && now - _polledAt < PollHeartbeat)
            return;
        (_polled, _polledAt) = ((repositories, running), now);
        _info($"intake: poll: {repositories.ToString(CultureInfo.InvariantCulture)} repositories, {running.ToString(CultureInfo.InvariantCulture)} running");
    }

    /// <summary>One pass: report the batches that ended, then read each watched repository. A repository GitHub will not answer for is skipped until the next pass.</summary>
    public async Task PollAsync(IBatchStarter starter, CancellationToken ct)
    {
        _pulls.Clear();
        _stillWaiting.Clear();
        await ReportEndedAsync(ct);
        await _reviews.ReportEndedAsync(ct);
        _watched = await WatchAsync(ct);
        LogPoll(_watched.Keys.Count(Sources.IsOn), state.Tasks.Count(t => t.Phase == IntakePhase.Running));
        foreach (var (name, watched) in _watched)
        {
            if (!Sources.IsOn(name))
                continue;
            var repository = new AutomationRepository(watched.Path);
            try
            {
                await LabelAsync(name, ct);
                await LabelTriggersAsync(name, repository, starter, ct);
                await CommentTriggersAsync(name, repository, starter, ct);
                await LostAsync(name, repository, starter, ct);
                if (settings.MaxReviewRounds > 0)
                {
                    await ReviewCommentsAsync(name, ct);
                    await RoundsAsync(name, starter, ct);
                }
                if (settings.ReviewPulls)
                    await _reviews.StartAsync(name, repository, _checkouts, starter, ct);
                Withdraw(name);
            }
            catch (IntakeException e)
            {
                _warn($"intake: {name}: {e.Message}");
            }
        }
    }

    private static Dictionary<string, WatchedRepository> Listed(AutomationGitHub settings) =>
        (settings.Repositories ?? new Dictionary<string, AutomationRepository>()).ToDictionary(r => r.Key, r => new WatchedRepository(r.Value.Path, false), StringComparer.OrdinalIgnoreCase);

    /// <summary>The profile's repositories and, with <c>discover</c>, the App's: a repository it cannot list or clone now keeps what the last poll had.</summary>
    private async Task<IReadOnlyDictionary<string, WatchedRepository>> WatchAsync(CancellationToken ct)
    {
        var watched = Listed(settings);
        if (!settings.Discover || clones is null)
            return watched;
        IReadOnlyList<InstalledRepository> installed;
        try
        {
            installed = await host.InstalledRepositoriesAsync(ct);
        }
        catch (IntakeException e)
        {
            _warn($"intake: the App's repositories could not be listed: {e.Message}");
            return _watched;
        }
        foreach (var repository in installed.Where(r => !r.Archived && !watched.ContainsKey(r.FullName)))
        {
            try
            {
                watched[repository.FullName] = new WatchedRepository(await clones.EnsureAsync(repository.FullName, repository.CloneUrl, ct), true);
            }
            catch (IntakeException e)
            {
                _warn($"intake: {e.Message}");
            }
        }
        return watched;
    }

    /// <summary>The first time a repository is watched, its trigger label is created if it has none, so a person can apply it.</summary>
    private async Task LabelAsync(string name, CancellationToken ct)
    {
        if (state.Labeled.Contains(name))
            return;
        await host.EnsureLabelAsync(name, settings.Label, ct);
        state.Labeled.Add(name);
        state.Save(_time.GetUtcNow());
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
                {
                    state.Handled.Add(key);
                    Log(key, "ignored", "author_not_allowed", applied.Actor);
                }
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
            Log(key, "ignored", "author_not_allowed", comment.Author);
            return Outcome.Settled;
        }
        if (answers)
            return await ContinueAsync(key, name, task!, comment, starter, ct);
        var issue = await host.GetAsync(name, comment.IssueNumber, ct);
        if (issue.IsPullRequest)
        {
            state.Handled.Add(key);
            if (settings.MaxReviewRounds > 0)
                await QueueAsync(key, new PendingReview($"c{comment.Id.ToString(CultureInfo.InvariantCulture)}", name, issue.Number, comment.Author, comment.Body, null, null, comment.UpdatedAt), true, ct);
            else
                Log(key, "ignored", "review_rounds_off", comment.Author);
            return Outcome.Settled;
        }
        return await StartAsync(key, name, repository, issue, comment.Body, comment.Author, starter, ct);
    }

    /// <param name="goal">The goal of a lost run that starts again; null: made from the issue.</param>
    private async Task<Outcome> StartAsync(string key, string name, AutomationRepository repository, IntakeIssue issue, string? comment, string? commentAuthor, IBatchStarter starter, CancellationToken ct,
        string? goal = null)
    {
        if (state.LatestFor(name, issue.Number) is { Phase: IntakePhase.Running } active)
        {
            await NotifyOnceAsync($"active:{key}", name, issue.Number, $"chargehand is already working on this issue (run `{active.BatchRunId}`); it will answer here when that run ends.", ct);
            state.Handled.Add(key);
            Log(key, "ignored", "active");
            return Outcome.Settled;
        }
        // A repository the integrator merges into does not take work from text an allowed author did not write: the issue's body steers the session, and a merge is the end of a check, not of a review.
        if (integrator?.Resolve(RemoteOf(name)) == IntegrationMode.Merge && !Allowed(issue.Author))
        {
            await NotifyOnceAsync($"author:{key}", name, issue.Number, $"chargehand did not start: this repository merges chargehand's pull requests once their checks pass, so it only works from an issue written by an allowed author, and this one was written by {issue.Author}.", ct);
            state.Handled.Add(key);
            Log(key, "ignored", "author", issue.Author);
            return Outcome.Settled;
        }
        if (await SlotAsync(key, name, issue.Number, ct) is { } held)
            return held;
        RepositoryRef source;
        try
        {
            source = await _checkouts.CurrentAsync(name, repository.Path, ct);
        }
        catch (IntakeException e)
        {
            _warn($"intake: {name}#{issue.Number}: {e.Message}");
            Wait(key, name, issue.Number, "checkout", e.Message);
            return Outcome.Deferred;
        }
        var size = IntakeGoal.SizeOf(issue.Labels) ?? settings.BudgetClass;
        var taskId = TaskId(issue.Number);
        goal ??= IntakeGoal.Issue(name, issue, comment, commentAuthor);
        return await LaunchAsync(key, name, issue.Number, source, new DrivenTask(taskId, Goal: goal, Size: size), run => new IntakeTask(name, issue.Number, run, taskId, _time.GetUtcNow(), size, Trigger: key),
            starter, run => $"chargehand started run `{run}` (task `{taskId}`). It works on its own branch and opens a draft pull request when it is done, or asks here if it needs an answer.", null, null, ct);
    }

    private async Task<Outcome> ContinueAsync(string key, string name, IntakeTask earlier, IntakeComment reply, IBatchStarter starter, CancellationToken ct)
    {
        if (await SlotAsync(key, name, earlier.Issue, ct) is { } held)
            return held;
        // The continuation starts at the commit the earlier task started at: its kept work is a branch from there.
        if ((await log.ReadAsync(earlier.BatchRunId, ct)).Start?.Request.Context.Repository is not { } repository)
        {
            await NotifyOnceAsync($"gone:{key}", name, earlier.Issue, $"chargehand cannot continue run `{earlier.BatchRunId}`: it is no longer in the run log. Comment {settings.Mention} to start the task again.", ct);
            state.Replace(earlier, earlier with { Phase = IntakePhase.Done });
            state.Handled.Add(key);
            Log(key, "ignored", "gone", reply.Author);
            return Outcome.Settled;
        }
        var taskId = TaskId(earlier.Issue);
        var task = new DrivenTask(taskId, Size: earlier.Size, Answers: [IntakeGoal.Answer(reply.Body)], ContinueFrom: new ContinueFrom(earlier.BatchRunId, earlier.TaskId));
        return await LaunchAsync(key, name, earlier.Issue, repository, task, run => earlier with { BatchRunId = run, TaskId = taskId, Started = _time.GetUtcNow(), Phase = IntakePhase.Running, AskedCommentId = null, PrUrl = null, Trigger = null },
            starter, run => $"chargehand continues with your answer: run `{run}` (task `{taskId}`), on the same branch.", earlier, null, ct);
    }

    /// <summary>Waits while every slot is taken: null when the batch may start. Spend is bounded by the account the start is admitted as (ADR 0076), not by a count of starts.</summary>
    private async Task<Outcome?> SlotAsync(string key, string name, int issue, CancellationToken ct)
    {
        if (state.Tasks.Count(t => t.Phase == IntakePhase.Running) < settings.MaxConcurrent)
            return null;
        _info($"intake: {key}: waiting: max_concurrent");
        Wait(key, name, issue, "max_concurrent");
        await NotifyOnceAsync($"wait:{key}", name, issue, $"chargehand is waiting for a free slot: {settings.MaxConcurrent.ToString(CultureInfo.InvariantCulture)} triggered batches are running. This starts when one ends.", ct);
        return Outcome.Deferred;
    }

    /// <summary>Keeps a trigger that cannot start yet in the state file, saved before any comment about it; a later poll reads it again and starts it.</summary>
    private void Wait(string key, string name, int issue, string reason, string? detail = null)
    {
        _stillWaiting.Add(key);
        var index = state.Waiting.FindIndex(w => w.Key == key);
        var since = index >= 0 ? state.Waiting[index].Since : _time.GetUtcNow();
        var waiting = new WaitingTrigger(key, name, issue, since, reason, detail);
        if (index >= 0)
            state.Waiting[index] = waiting;
        else
            state.Waiting.Add(waiting);
        state.Save(_time.GetUtcNow());
    }

    /// <summary>After a repository's pass: a waiting trigger that settled leaves the queue, and one that was not read again (its label taken off, its comment deleted, its issue closed) is withdrawn.</summary>
    private void Withdraw(string name)
    {
        var gone = state.Waiting.Where(w => w.Repository == name && (state.Handled.Contains(w.Key) || !_stillWaiting.Contains(w.Key))).ToList();
        if (gone.Count == 0)
            return;
        foreach (var w in gone.Where(w => !state.Handled.Contains(w.Key)))
            Log(w.Key, "ignored", "withdrawn");
        state.Waiting.RemoveAll(gone.Contains);
        state.Save(_time.GetUtcNow());
    }

    private async Task<Outcome> LaunchAsync(string key, string name, int issue, RepositoryRef repository, DrivenTask task, Func<string, IntakeTask> track, IBatchStarter starter, Func<string, string> announce,
        IntakeTask? replaces, Action? settled, CancellationToken ct)
    {
        // A continuation's goal starts with this text and a finished one replaces the pull request's body, so the closing keyword is here as well as in the issue's goal. A round's number, and that of
        // a reply to a round's questions, is a pull request's.
        var closes = key.StartsWith("round:", StringComparison.Ordinal) || replaces?.Round is not null ? "" : $"\n\n{IntakeGoal.Closes(issue)}";
        var request = new RunRequest("request/v1", $"Triggered from GitHub: {name}#{issue.ToString(CultureInfo.InvariantCulture)}.{closes}", new RequestContext(false, Preset, Repository: repository),
            Driven: new RequestDriven([task], MaxParallel: 1, MaxTokensTotal: settings.MaxTokensTotal, MaxUsdTotal: settings.MaxUsdTotal));
        var started = await TriggeredStart.StartAsync(starter, log, state.Starting, state.Save, key, request, _time.GetUtcNow(), ct);
        if (started.RunId is not { } run)
        {
            _warn($"intake: {name}#{issue}: the server did not start the batch: {started.Refusal}");
            Wait(key, name, issue, "admission", started.Refusal);
            return Outcome.Deferred;
        }
        var now = _time.GetUtcNow();
        state.Starting.Remove(key);
        if (replaces is not null)
            state.Replace(replaces, replaces with { Phase = IntakePhase.Done });
        state.Tasks.Add(track(run));
        state.Handled.Add(key);
        state.Waiting.RemoveAll(w => w.Key == key);
        Log(key, key.StartsWith("round:", StringComparison.Ordinal) ? "round_started" : replaces is not null ? "continued" : "started");
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
            // Only a queued review comment is logged: a comment of anyone on any pull request of the repository is read, and most of them are not requests.
            if (Allowed(comment.Author) && (!comment.IsReply || Mentions(comment.Body)))
                await QueueAsync(key, new PendingReview($"r{comment.Id.ToString(CultureInfo.InvariantCulture)}", name, comment.PullNumber, comment.Author, comment.Body, comment.Path, comment.Line, comment.UpdatedAt), false, ct);
        }
        state.ReviewCursor[name] = next;
        state.Save(now);
    }

    /// <summary>Keeps a comment for a round when the pull request is a chargehand's own: its head branch is <c>chargehand/*</c> in the repository itself, not a fork.</summary>
    private async Task QueueAsync(string key, PendingReview comment, bool asked, CancellationToken ct)
    {
        if (!IsChargehandBranch(await PullAsync(comment.Repository, comment.Pull, ct)))
        {
            // A comment that names the bot on someone else's pull request was a request; a review comment there is not.
            if (asked)
                Log(key, "ignored", "not_chargehand_branch", comment.Author);
            return;
        }
        state.Pending.Add(comment);
        Log(key, "queued", null, comment.Author);
        state.Save(_time.GetUtcNow());
    }

    /// <summary>Keeps what came of a trigger for the console and says it in the server log.</summary>
    private void Log(string key, string outcome, string? reason = null, string? author = null)
    {
        state.Triggers.Add(new IntakeTrigger(_time.GetUtcNow(), key, outcome, reason, author));
        _info($"intake: {key}: {outcome}{(reason is null ? "" : $": {reason}")}{(author is null ? "" : $" ({author})")}");
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
        if (await SlotAsync(key, name, number, ct) is not null)
            return;
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

    private const string LostPrefix = "lost:";

    /// <summary>A run a restart lost before its batch had state to resume from (issue #557) waits in the queue to start again, once: only a fresh start from an issue, never a run that was itself
    /// such a second start. Kept before the comment that says so.</summary>
    private bool Requeue(IntakeTask task)
    {
        if (task.Trigger is not { } trigger || trigger.StartsWith(LostPrefix, StringComparison.Ordinal))
            return false;
        var key = $"{LostPrefix}{task.Repository}#{task.Issue.ToString(CultureInfo.InvariantCulture)}:{task.BatchRunId}";
        if (state.Waiting.All(w => w.Key != key))
            state.Waiting.Add(new WaitingTrigger(key, task.Repository, task.Issue, _time.GetUtcNow(), "lost", task.BatchRunId));
        Log(key, "queued", "lost");
        state.Save(_time.GetUtcNow());
        return true;
    }

    /// <summary>Starts the lost runs that wait in the queue, after the triggers that waited before them, with the goal the lost run had when the run log still holds it.</summary>
    private async Task LostAsync(string name, AutomationRepository repository, IBatchStarter starter, CancellationToken ct)
    {
        foreach (var lost in state.Waiting.Where(w => w.Repository == name && w.Key.StartsWith(LostPrefix, StringComparison.Ordinal)).ToList())
        {
            _stillWaiting.Add(lost.Key);
            IntakeIssue issue;
            try
            {
                issue = await host.GetAsync(name, lost.Issue, ct);
            }
            catch (IntakeException e) when (e.Status is 404 or 410)
            {
                state.Waiting.Remove(lost);
                Log(lost.Key, "ignored", "gone");
                continue;
            }
            var run = lost.Key[(lost.Key.LastIndexOf(':') + 1)..];
            var goal = (await log.ReadAsync(run, ct)).Start?.Request.Driven?.Tasks is [{ Goal: { } earlier }, ..] ? earlier : null;
            await StartAsync(lost.Key, name, repository, issue, null, null, starter, ct, goal);
        }
    }

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
        if (result is null && Requeue(task))
            text = $"chargehand lost run `{task.BatchRunId}`: the server that ran it stopped and did not take it up again. chargehand starts the task again, once, when a slot is free.";
        else if (result is null)
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
        _info($"intake: {task.Repository}#{task.Issue.ToString(CultureInfo.InvariantCulture)}: run {task.BatchRunId} ended: {row?.Status ?? (result is null ? "lost" : "task_not_started")}");
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
