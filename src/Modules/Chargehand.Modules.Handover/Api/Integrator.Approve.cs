using Chargehand.Modules.Forge.Api;

namespace Chargehand.Modules.Handover.Api;

public sealed partial class Integrator
{
    /// <summary>The label that says a pull request waits for the owner.</summary>
    public const string NeedsYouLabel = "agent:needs-you";

    /// <summary>A ready pull request on a merge repository waits for chargehand's own review of its head; nothing is enabled here.</summary>
    private async Task<IntegrationResult> AwaitReviewAsync(IntegrationTarget target, PrSnapshot pr, Action<string> report, CancellationToken ct)
    {
        if (pr.MergeStatus == "DIRTY")
        {
            await CommentOnceAsync(target, pr, ConflictMarker, "This pull request conflicts with its base branch, so chargehand is not merging it; a person needs to resolve it.", ct);
            return Say(report, IntegrationAction.NeedsAttention, "the pull request conflicts with its base; not merged");
        }
        return pr.AutoMerge
            ? Say(report, IntegrationAction.AutoMergeEnabled, "auto-merge was already enabled")
            : Say(report, IntegrationAction.AwaitingReview, "marked ready; auto-merge is enabled once chargehand's review of this head has no blocking finding");
    }

    private async Task NeedsYouAsync(IntegrationTarget target, CancellationToken ct)
    {
        try
        {
            await host.AddLabelAsync(target.Repository, target.Number, NeedsYouLabel, ct);
        }
        catch (IntegrationException)
        {
            // The marker comment already says it; a label that cannot be set changes nothing else.
        }
    }

    /// <summary>Called once chargehand's own review of <paramref name="headSha"/> has no blocking finding: enables auto-merge (or merges a pull request that is mergeable already) in a repository whose mode is merge,
    /// when the head is still that commit, no required check failed and no migration changed. Checks still running are fine: GitHub merges only after the required ones pass. Anything else enables nothing.</summary>
    public async Task<IntegrationResult> ApproveAsync(IntegrationTarget target, string headSha, CancellationToken ct, Action<string>? report = null)
    {
        report ??= _ => { };
        if (target.LocalOnly || target.Partial || ModeFor(target) != IntegrationMode.Merge)
            return new(IntegrationAction.None, "this repository does not merge by itself");
        try
        {
            var pr = await host.GetAsync(target.Repository, target.Number, ct);
            if (pr.Merged || pr.Closed)
                return Say(report, IntegrationAction.None, pr.Merged ? "the pull request is already merged" : "the pull request is closed");
            if (!string.Equals(pr.HeadSha, headSha, StringComparison.OrdinalIgnoreCase))
                return Say(report, IntegrationAction.None, $"the head moved to {Short(pr.HeadSha)} after the review of {Short(headSha)}; not merged");
            if (pr.IsDraft)
                return Say(report, IntegrationAction.None, "the pull request is still a draft; not merged");
            if (pr.Checks.Any(c => c.Required && c.Outcome is CheckOutcome.Failed or CheckOutcome.Other))
                return Say(report, IntegrationAction.None, "a required check has not passed; not merged");
            if (MigrationPaths.Find(await host.ChangedPathsAsync(target.Repository, target.Number, ct)) is { } migration)
                return Say(report, IntegrationAction.None, $"it adds a database migration ({migration}); not merged");
            if (await host.ChecksGateAsync(target.Repository, pr.BaseBranch, ct) != ChecksGate.Required)
                return Say(report, IntegrationAction.None, "the base branch requires no status check; not merged");
            return await MergeAsync(target, pr, report, ct);
        }
        catch (IntegrationException e)
        {
            return Say(report, IntegrationAction.Failed, $"approval stopped: {e.Message}");
        }
    }

    private async Task<IntegrationResult> MergeAsync(IntegrationTarget target, PrSnapshot pr, Action<string> report, CancellationToken ct)
    {
        if (pr.MergeStatus == "DIRTY")
        {
            await CommentOnceAsync(target, pr, ConflictMarker, "This pull request conflicts with its base branch, so chargehand is not merging it; a person needs to resolve it.", ct);
            return Say(report, IntegrationAction.NeedsAttention, "the pull request conflicts with its base; not merged");
        }
        if (pr.AutoMerge)
            return Say(report, IntegrationAction.AutoMergeEnabled, "auto-merge was already enabled");
        if (pr.MergeStatus == "CLEAN")
            return await MergeNowAsync(target, pr.HeadSha, report, ct);
        try
        {
            await host.EnableAutoMergeAsync(target.Repository, pr.NodeId, pr.HeadSha, ct);
        }
        catch (IntegrationException)
        {
            // GitHub refuses auto-merge on a pull request that is mergeable already; then the merge itself goes through the same rules.
            var fresh = await host.GetAsync(target.Repository, target.Number, ct);
            if (fresh.IsDraft || fresh.MergeStatus is not ("CLEAN" or "UNSTABLE"))
                throw;
            return await MergeNowAsync(target, fresh.HeadSha, report, ct);
        }
        return Say(report, IntegrationAction.AutoMergeEnabled, $"auto-merge (squash) enabled for {Short(pr.HeadSha)}; {host.Name} merges once the required checks pass");
    }
}
