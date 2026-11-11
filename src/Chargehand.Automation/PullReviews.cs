using System.Globalization;
using System.Text;
using Chargehand.Contracts;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Automation;

/// <summary>Reviews every open, non-draft pull request of the watched repositories (ADR 0051), once per head commit: the head is fetched into the server's checkout (a read-only one: into the repository's managed clone), a read-only run of the
/// <c>review</c> preset gets the diff, and when it ends one comment carries the findings. The commit is recorded in <see cref="IntakeState.Reviewed"/> after the run starts, and the comment's hidden
/// marker names the commit, so neither a restart nor a lost state file reviews or comments twice.</summary>
internal sealed class PullReviews(AutomationGitHub settings, IIntakeHost host, IRunLog log, IntakeState state, TimeProvider time, Action<string> warn)
{
    public const string Preset = "review";
    public const int MaxDiff = 60000;
    public const int MaxFindings = 20;
    private const int MaxSummary = 1000;
    private const int MaxFinding = 1500;
    private const int MaxLocator = 120;

    private readonly HashSet<string> _stale = [];
    private readonly HashSet<string> _requested = new(StringComparer.OrdinalIgnoreCase);

    public static string MarkerFor(string sha) => $"<!-- chargehand-review {sha} -->";

    private static string Short(string sha) => sha[..Math.Min(sha.Length, 7)];

    private static string Key(string repository, int number, string sha) => $"{repository}#{number.ToString(CultureInfo.InvariantCulture)}@{sha}";

    private static string Asked(string repository, int number) => $"{repository}#{number.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>An allowed author asked for a review of this pull request (<c>@chargehand review</c>): its current head is reviewed on a coming poll even with <c>review_pulls</c> off, a draft or a bot's pull request
    /// included. The request is kept in memory only: a restart before the review starts drops it.</summary>
    public void Request(string repository, int number) => _requested.Add(Asked(repository, number));

    public bool HasRequests(string repository) => _requested.Any(r => r.StartsWith(repository + "#", StringComparison.OrdinalIgnoreCase));

    /// <summary>Starts a review for each pull request whose head commit has none (<paramref name="all"/> off: only for the requested ones). No free slot or a busy server leaves the rest for a later poll. A repository the
    /// owner left out of reviews (<paramref name="policy"/>) is skipped, and its pull requests are reviewed once it is put back.</summary>
    public async Task StartAsync(string name, AutomationRepository repository, CheckoutSources checkouts, IBatchStarter starter, IntakeSources policy, bool all, CancellationToken ct)
    {
        if (!policy.ReviewsOn(name))
            return;
        var pulls = await host.OpenPullsAsync(name, ct);
        // A request for a pull request that is no longer open is dropped.
        _requested.RemoveWhere(r => r.StartsWith(name + "#", StringComparison.OrdinalIgnoreCase) && !pulls.Any(p => Asked(name, p.Number).Equals(r, StringComparison.OrdinalIgnoreCase)));
        foreach (var pull in pulls)
        {
            var asked = _requested.Contains(Asked(name, pull.Number));
            if (!asked && (!all || pull.Draft || pull.Author.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (pull.HeadSha.Length < 7 || state.Reviewed.Contains(Key(name, pull.Number, pull.HeadSha)))
            {
                _requested.Remove(Asked(name, pull.Number));
                continue;
            }
            if (Full(policy))
                return;
            try
            {
                if (!await TryStartAsync(name, repository, pull, checkouts, starter, policy, ct))
                    return;
                if (state.Reviewed.Contains(Key(name, pull.Number, pull.HeadSha)))
                    _requested.Remove(Asked(name, pull.Number));
            }
            catch (IntakeException e)
            {
                warn($"intake: {name}#{pull.Number}: review not started: {e.Message}");
                if (!await StillOpenAsync(name, pull.Number, ct))
                {
                    // Merged or closed since the listing: its head ref is gone, and asking again would fail the same way.
                    _requested.Remove(Asked(name, pull.Number));
                    Settle(Key(name, pull.Number, pull.HeadSha));
                }
            }
        }
    }

    /// <summary>False when the server refused the run: the rest wait for the next poll.</summary>
    private async Task<bool> TryStartAsync(string name, AutomationRepository repository, ReviewablePull pull, CheckoutSources checkouts, IBatchStarter starter, IntakeSources policy, CancellationToken ct)
    {
        var key = Key(name, pull.Number, pull.HeadSha);
        if (await HasCommentAsync(name, pull.Number, pull.HeadSha, ct))
        {
            Settle(key);
            return true;
        }
        var source = await checkouts.PullHeadAsync(name, repository.Path, pull.Number, pull.HeadSha, ct);
        if (source.Commit != pull.HeadSha)
        {
            // A push between the listing and the fetch, or a ref a host job has not brought up yet: the next poll tries again, and the line is written once per head.
            if (_stale.Add(key))
                warn($"intake: {name}#{pull.Number}: review waits: the checkout has {Short(source.Commit)} for the head, GitHub lists {Short(pull.HeadSha)}");
            return true;
        }
        string diff;
        try
        {
            diff = await host.PullDiffAsync(name, pull.Number, ct);
        }
        catch (IntakeException e) when (e.Status is 406 or 422)
        {
            // GitHub does not render a diff this large; asking again would not change that.
            warn($"intake: {name}#{pull.Number}: not reviewed, GitHub would not render its diff: {e.Message}");
            Settle(key);
            return true;
        }
        var budget = policy.ReviewBudgetUsd;
        var request = new RunRequest("request/v1", $"Review pull request {name}#{pull.Number.ToString(CultureInfo.InvariantCulture)}.", new RequestContext(false, Preset, BudgetUsd: budget, Repository: source),
            [new CallerInput("goal", "goal", IntakeGoal.Pull(pull.Title, pull.Body)), new CallerInput("diff", "diff", Cut(diff)), new CallerInput("tests", "test-output", "not run by chargehand; see the pull request's checks")]);
        var started = await starter.StartAsync(request, budget is not null, ct);
        if (started.RunId is not { } run)
        {
            warn($"intake: {name}#{pull.Number}: the server did not start the review: {started.Refusal}");
            return false;
        }
        var now = time.GetUtcNow();
        state.Reviews.Add(new PullReview(name, pull.Number, pull.HeadSha, run, now));
        Settle(key);
        return true;
    }

    private async Task<bool> StillOpenAsync(string name, int number, CancellationToken ct)
    {
        try
        {
            return (await host.GetPullAsync(name, number, ct)).Open;
        }
        catch (IntakeException)
        {
            return true;
        }
    }

    private void Settle(string key)
    {
        state.Reviewed.Add(key);
        state.Save(time.GetUtcNow());
    }

    /// <summary>As many batches and reviews run as <c>max_concurrent</c> allows. Spend is bounded by the account a review is admitted as (ADR 0076).</summary>
    private bool Full(IntakeSources policy) => policy.Paused || state.Tasks.Count(t => t.Phase == IntakePhase.Running) + state.Reviews.Count >= (policy.MaxConcurrent ?? settings.MaxConcurrent);

    private static string Cut(string diff) => diff.Length <= MaxDiff ? diff : diff[..MaxDiff] + $"\n[diff truncated at {MaxDiff.ToString(CultureInfo.InvariantCulture)} characters]";

    private async Task<bool> HasCommentAsync(string repository, int number, string sha, CancellationToken ct) =>
        (await host.CommentBodiesAsync(repository, number, ct)).Any(b => b.Contains(MarkerFor(sha), StringComparison.Ordinal));

    /// <summary>Comments the findings of the reviews that ended. A review whose server stopped without a result is dropped and its commit becomes reviewable again.</summary>
    public async Task ReportEndedAsync(CancellationToken ct)
    {
        foreach (var review in state.Reviews.ToList())
        {
            try
            {
                var entry = await log.ReadAsync(review.RunId, ct);
                if (entry.Run is { } run)
                    await PostAsync(review, run.Result, ct);
                else if (time.GetUtcNow() - review.Started > GitHubIntake.LostAfter && (entry.Start is null || !entry.Start.OwnerAlive()))
                {
                    state.Reviews.Remove(review);
                    state.Reviewed.Remove(Key(review.Repository, review.Pull, review.Sha));
                    state.Save(time.GetUtcNow());
                }
            }
            catch (IntakeException e)
            {
                warn($"intake: {review.Repository}#{review.Pull}: {e.Message}");
            }
        }
    }

    private async Task PostAsync(PullReview review, ResultContract result, CancellationToken ct)
    {
        try
        {
            if (!await HasCommentAsync(review.Repository, review.Pull, review.Sha, ct))
                await host.CommentAsync(review.Repository, review.Pull, Comment(review.Sha, result), ct);
        }
        catch (IntakeException e) when (e.Status is 404 or 410)
        {
            // The pull request is gone: nobody to tell.
        }
        state.Reviews.Remove(review);
        state.Save(time.GetUtcNow());
    }

    /// <summary>The comment for a finished review. Model text is neutralised: an @ gets a zero-width space so it pings no one, &lt; becomes an entity so it opens no HTML, and line breaks become spaces.</summary>
    public static string Comment(string sha, ResultContract result)
    {
        var text = new StringBuilder(MarkerFor(sha)).Append('\n').Append(GitHubIntake.Marker).Append("\n### chargehand review\n\n");
        if (result.Status == ResultStatus.Failed)
            text.Append(CultureInfo.InvariantCulture, $"The review failed (`{result.Error?.Code.ToString() ?? "unknown"}`): {Plain(result.Error?.Message ?? result.Summary, MaxSummary)}");
        else if (result.Status != ResultStatus.Completed)
            text.Append(CultureInfo.InvariantCulture, $"The review stopped with status `{result.Status.ToString().ToLowerInvariant()}`: {Plain(result.Summary, MaxSummary)}");
        else
            Findings(text, result);
        text.Append(CultureInfo.InvariantCulture, $"\n\nRun `{result.TaskId}` at `{sha[..Math.Min(sha.Length, 12)]}`.");
        return text.ToString();
    }

    private static void Findings(StringBuilder text, ResultContract result)
    {
        text.Append(Plain(result.Summary, MaxSummary)).Append("\n\n");
        if (result.Claims.Count == 0)
        {
            text.Append("No findings.");
            return;
        }
        var shown = result.Claims.OrderByDescending(c => c.Confidence).Take(MaxFindings).ToList();
        if (result.Claims.Count > shown.Count)
            text.Append(CultureInfo.InvariantCulture, $"Showing {shown.Count} of {result.Claims.Count} findings, highest confidence first.\n\n");
        text.AppendJoin('\n', shown.Select(c => $"- {Plain(c.Text, MaxFinding)}\n  Confidence {c.Confidence.ToString("0.##", CultureInfo.InvariantCulture)}. Evidence: {Locators(c, result)}"));
    }

    private static string Locators(Claim claim, ResultContract result)
    {
        var found = claim.Evidence.Select(id => result.Evidence.FirstOrDefault(e => e.Id == id)?.Locator ?? id).Select(Code).ToList();
        return found.Count == 0 ? "none" : string.Join(", ", found);
    }

    private static string Code(string locator)
    {
        var one = locator.Replace('`', '\'').ReplaceLineEndings(" ");
        return $"`{(one.Length > MaxLocator ? one[..MaxLocator] + "..." : one)}`";
    }

    private static string Plain(string text, int max)
    {
        var one = text.Replace("@", "@​", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).ReplaceLineEndings(" ").Trim();
        return one.Length > max ? one[..max].TrimEnd() + "..." : one;
    }
}
