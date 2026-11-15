using System.Globalization;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Automation;

public sealed partial class GitHubIntake
{
    /// <summary>A blocking finding of chargehand's own review starts a round on the pull request's branch with the finding as the answer. The author is chargehand's review, so this path skips the allowed-authors check on purpose
    /// and nothing else does; it is still one of chargehand's branches only, and the round cap and the busy-branch rules apply as for any round.</summary>
    private async Task QueueReviewRoundAsync(PendingReview round, CancellationToken ct)
    {
        if (MaxReviewRounds <= 0)
            return;
        try
        {
            await QueueAsync($"review:{round.Repository}#{round.Pull.ToString(CultureInfo.InvariantCulture)}:{round.Id}", round, false, ct);
        }
        catch (IntakeException e)
        {
            _warn($"intake: {round.Repository}#{round.Pull}: review round not queued: {e.Message}");
        }
    }

    /// <summary>Every poll, for each open chargehand pull request of a repository that merges: the integrator merges it, pinned to its head, when the App's own clean review covers that head and the checks are green. A merge
    /// happens nowhere else, so a push after the review (a CI fix, a round) is merged only once its own review is clean. The marker counts only in a comment the App wrote.</summary>
    private async Task ApproveReviewedAsync(string name, CancellationToken ct)
    {
        if (approver is null || integrator?.Resolve(RemoteOf(name)) != IntegrationMode.Merge)
            return;
        try
        {
            foreach (var pull in await host.OpenPullsAsync(name, ct))
            {
                if (!IsChargehandBranch(pull.HeadBranch, pull.SameRepository))
                    continue;
                var reviewed = await CleanReviewOfAsync(name, pull, ct);
                var result = await approver.ApproveAsync(new IntegrationTarget(RemoteOf(name), pull.Number, pull.HeadBranch, false, ""), reviewed, ct);
                if (result.Action is IntegrationAction.Merged or IntegrationAction.Failed or IntegrationAction.Asked)
                    _info($"intake: {name}#{pull.Number.ToString(CultureInfo.InvariantCulture)}: {result.Detail}");
            }
        }
        catch (Exception e) when (e is IntakeException or IntegrationException)
        {
            _warn($"intake: {name}: approval not applied: {e.Message}");
        }
    }

    /// <summary>The head if the App's own comment holds the clean marker for it, else empty.</summary>
    private async Task<string> CleanReviewOfAsync(string name, ReviewablePull pull, CancellationToken ct)
    {
        if (settings.AppLogin is not { Length: > 0 } app || pull.HeadSha.Length < 7)
            return "";
        var marker = PullReviews.CleanMarkerFor(pull.HeadSha);
        return (await host.CommentBodiesByAsync(name, pull.Number, app, ct)).Any(b => b.Contains(marker, StringComparison.Ordinal)) ? pull.HeadSha : "";
    }

    /// <summary>One line per reason a repository that is set to <c>merge</c> will never merge: a merge waits for chargehand's own clean review, and that needs the whole chain. Empty when it is complete.</summary>
    public static IReadOnlyList<string> MergeChainGaps(AutomationGitHub? github, DrivenIntegrator integrator, IntakeSources? sources)
    {
        var merging = (integrator.Repositories ?? new Dictionary<string, string>()).Where(r => r.Value == "merge" && integrator.AlwaysAsk?.Contains(r.Key, StringComparer.OrdinalIgnoreCase) != true).Select(r => r.Key).ToList();
        if (merging.Count == 0)
            return [];
        if (github is null)
            return ["integrator repositories are set to merge but automation.github is missing: nothing reviews them, so nothing merges"];
        List<string> gaps = [];
        if (!github.ReviewPulls)
            gaps.Add("integrator repositories are set to merge but automation.github.review_pulls is off: no review clears a pull request, so nothing merges");
        if (string.IsNullOrWhiteSpace(github.AppLogin))
            gaps.Add("integrator repositories are set to merge but the App's login is unknown (set driven.integrator.app_slug): its pull requests are not reviewed, so nothing merges");
        gaps.AddRange(merging.Where(r => sources?.ReviewsOn(r) == false).Select(r => $"{r} is set to merge but its reviews are switched off in the console: nothing merges there"));
        gaps.AddRange(merging.Where(r => github.Repositories?.ContainsKey(r) != true && !github.Discover).Select(r => $"{r} is set to merge but automation.github does not watch it: nothing merges there"));
        return gaps;
    }
}
