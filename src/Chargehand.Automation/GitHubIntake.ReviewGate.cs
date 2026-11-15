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

    private readonly HashSet<string> _approvalsTold = [];

    /// <summary>Every poll, for each open chargehand pull request of a repository that merges or asks: the integrator merges it, pinned to its head, when the App's own clean review covers that head and the checks are green;
    /// in a repository that does not merge it only turns off an auto-merge armed earlier. A merge happens nowhere else, so a push after the review (a CI fix, a round) is merged only once its own review is clean. The marker
    /// counts only at the start of a comment the App wrote. One pull request that cannot be read does not stop the others.</summary>
    private async Task ApproveReviewedAsync(string name, CancellationToken ct)
    {
        if (approver is null || integrator?.Resolve(RemoteOf(name)) is not (IntegrationMode.Merge or IntegrationMode.Ask))
            return;
        IReadOnlyList<ReviewablePull> pulls;
        try
        {
            pulls = await host.OpenPullsAsync(name, ct);
        }
        catch (IntakeException e)
        {
            _warn($"intake: {name}: approval not applied: {e.Message}");
            return;
        }
        foreach (var pull in pulls.Where(p => IsChargehandBranch(p.HeadBranch, p.SameRepository)))
        {
            try
            {
                var app = settings.AppLogin is { Length: > 0 } login ? await host.CommentBodiesByAsync(name, pull.Number, login, ct) : [];
                var reviewed = pull.HeadSha.Length >= 7 && app.Any(b => PullReviews.IsCleanComment(b, pull.HeadSha)) ? pull.HeadSha : "";
                var result = await approver.ApproveAsync(new IntegrationTarget(RemoteOf(name), pull.Number, pull.HeadBranch, false, ""), reviewed, ct, appComments: app);
                if (result.Action is IntegrationAction.Merged or IntegrationAction.Failed or IntegrationAction.Asked && _approvalsTold.Add($"{name}#{pull.Number}@{pull.HeadSha}:{result.Action}"))
                    _info($"intake: {name}#{pull.Number.ToString(CultureInfo.InvariantCulture)}: {result.Detail}");
            }
            catch (Exception e) when (e is IntakeException or IntegrationException)
            {
                if (_approvalsTold.Add($"{name}#{pull.Number}@{pull.HeadSha}:error"))
                    _warn($"intake: {name}#{pull.Number.ToString(CultureInfo.InvariantCulture)}: approval not applied: {e.Message}");
            }
        }
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
