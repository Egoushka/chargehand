using System.Globalization;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;

namespace Chargehand.Automation;

public sealed partial class GitHubIntake
{
    /// <summary>A blocking finding of chargehand's own review starts a round on the pull request's branch with the finding as the answer. The author is chargehand, so this path skips the allowed-authors check on purpose
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

    /// <summary>A clean review of a chargehand pull request's head lets the integrator enable auto-merge, where the repository merges. Anything else on the pull request is the integrator's to refuse.</summary>
    private async Task ApproveCleanAsync(string name, int number, string sha, CancellationToken ct)
    {
        if (approver is null)
            return;
        try
        {
            var pull = await host.GetPullAsync(name, number, ct);
            if (!pull.Open || !IsChargehandBranch(pull))
                return;
            var result = await approver.ApproveAsync(new IntegrationTarget(RemoteOf(name), number, pull.HeadBranch, false, ""), sha, ct);
            _info($"intake: {name}#{number.ToString(CultureInfo.InvariantCulture)}: clean review of {sha[..Math.Min(sha.Length, 7)]}: {result.Detail}");
        }
        catch (Exception e) when (e is IntakeException or IntegrationException)
        {
            _warn($"intake: {name}#{number}: approval not applied: {e.Message}");
        }
    }
}
