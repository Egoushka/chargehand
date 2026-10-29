using Chargehand.Modules.Forge.Api;

namespace Chargehand.Modules.Handover.Api;

public sealed partial class Integrator
{
    /// <summary>The names, among <paramref name="failing"/>, that also fail on the base branch's own head; empty when none do, or when the base's checks could not be read (fails open: a normal fix attempt follows).</summary>
    private async Task<IReadOnlyList<string>> BaseFailingAsync(IntegrationTarget target, PrSnapshot pr, IReadOnlyCollection<string> failing, CancellationToken ct)
    {
        try
        {
            var baseChecks = await host.BaseChecksAsync(target.Repository, pr.BaseBranch, ct);
            return [.. baseChecks.Where(c => c.Outcome == CheckOutcome.Failed && failing.Contains(c.Name)).Select(c => c.Name).Distinct()];
        }
        catch (IntegrationException)
        {
            return [];
        }
    }

    /// <summary>A pull request the integrator already acted on once (ADR 0046 acts only once per task), revisited after the task has ended: a pull request with auto-merge on, held only because its
    /// base branch's required checks were failing, is updated once the base turns green (CHARGEHAND-669). Everything else (merged, closed, auto-merge off, not failing, still failing because of the
    /// base) is left alone: this is not a second pass of <see cref="IntegrateAsync"/> or <see cref="FollowAsync"/>, only the one thing neither of them revisits.</summary>
    public async Task<IntegrationResult> ReconcileAsync(IntegrationTarget target, Action<string> report, CancellationToken ct)
    {
        if (target.LocalOnly)
            return new(IntegrationAction.None, "the repository is local-only: the forge is not called");
        if (ModeFor(target) != IntegrationMode.Merge)
            return new(IntegrationAction.None, "not reconciled: this repository does not auto-merge");
        try
        {
            var pr = await host.GetAsync(target.Repository, target.Number, ct);
            if (pr.Merged || pr.Closed)
                return new(IntegrationAction.None, pr.Merged ? "the pull request is already merged" : "the pull request is closed");
            if (!pr.AutoMerge)
                return new(IntegrationAction.None, "auto-merge is not on, so there is nothing held only by the base");
            var failing = pr.Checks.Where(c => c.Required && c.Outcome == CheckOutcome.Failed).Select(c => c.Name).Distinct().ToList();
            if (failing.Count == 0)
                return new(IntegrationAction.None, "no required check is failing");
            if (await BaseFailingAsync(target, pr, failing, ct) is { Count: > 0 })
                return new(IntegrationAction.None, "still waiting on the base branch's required checks");
            // The branch's own name may not be known here (a reconcile pass has only the pull request's last known head, from the learning store): the comparison needs only a commit-ish, and the head read just now is one.
            if (await host.BehindByAsync(target.Repository, pr.BaseBranch, pr.HeadSha, ct) == 0)
                return new(IntegrationAction.None, "the branch is already level with its base, so the failure is not the base's");
            var (stopped, _) = await UpdateBranchAsync(target, pr, report, null, ct);
            if (stopped is not null)
                return stopped;
            return Say(report, IntegrationAction.AutoMergeEnabled, "the base's required checks now pass; the branch was updated so the required checks run again");
        }
        catch (IntegrationException e)
        {
            return Say(report, IntegrationAction.Failed, $"reconcile stopped: {e.Message}");
        }
    }
}
