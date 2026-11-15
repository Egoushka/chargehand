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
        return Say(report, IntegrationAction.AwaitingReview, "marked ready; chargehand merges it once its review of this head has no blocking finding and the required checks are green");
    }

    /// <summary>Only GitHub has chargehand's review of a pull request (intake's <c>review_pulls</c>), so only there does a merge wait for it; another forge merges by the policy alone, as before.</summary>
    private static bool Reviewed(RemoteRepository repository) => repository.Forge == ForgeKind.GitHub;

    private Task<IntegrationResult> MergeStepAsync(IntegrationTarget target, PrSnapshot pr, Action<string> report, CancellationToken ct) =>
        Reviewed(target.Repository) ? AwaitReviewAsync(target, pr, report, ct) : AutoMergeAsync(target, pr, report, ct);

    /// <summary>A merge repository on a forge with no review of chargehand's: the forge's own auto-merge, pinned to the head, or a merge now when it is mergeable already.</summary>
    private async Task<IntegrationResult> AutoMergeAsync(IntegrationTarget target, PrSnapshot pr, Action<string> report, CancellationToken ct)
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
            // A forge may refuse auto-merge on a pull request that is mergeable already; then the merge itself goes through the same rules.
            var fresh = await host.GetAsync(target.Repository, target.Number, ct);
            if (fresh.IsDraft || fresh.MergeStatus is not ("CLEAN" or "UNSTABLE"))
                throw;
            return await MergeNowAsync(target, fresh.HeadSha, report, ct);
        }
        return Say(report, IntegrationAction.AutoMergeEnabled, $"auto-merge (squash) enabled for {Short(pr.HeadSha)}; {host.Name} merges once the required checks pass");
    }

    /// <summary>GraphQL names a bot without the <c>[bot]</c> suffix its REST login carries; a person's own auto-merge is never turned off, so an unknown author or App login is not the App.</summary>
    private static bool SameAccount(string? enabledBy, string? appLogin) =>
        !string.IsNullOrEmpty(enabledBy) && !string.IsNullOrEmpty(appLogin)
        && string.Equals(enabledBy.Replace("[bot]", "", StringComparison.OrdinalIgnoreCase), appLogin.Replace("[bot]", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Run on every poll for a chargehand pull request. Auto-merge, if one was armed earlier, is turned off: a merge happens only here. When <paramref name="reviewedSha"/> (the head chargehand's own clean review
    /// covers; empty when none does) is still the head, the pull request is merged now, pinned to it, in a repository whose mode is merge, when it is ready, every required check is green, it changes no migration and
    /// carries none of the markers that ask a person instead. Checks still running return <see cref="IntegrationAction.AwaitingReview"/>; a moved head merges nothing, the next review of the new head decides.</summary>
    public async Task<IntegrationResult> ApproveAsync(IntegrationTarget target, string reviewedSha, CancellationToken ct, Action<string>? report = null, IReadOnlyList<string>? appComments = null, string? appLogin = null)
    {
        report ??= _ => { };
        var mode = ModeFor(target);
        if (target.LocalOnly || target.Partial || mode == IntegrationMode.Off)
            return new(IntegrationAction.None, "this repository does not merge by itself");
        try
        {
            var pr = await host.GetAsync(target.Repository, target.Number, ct);
            if (pr.Merged || pr.Closed)
                return new(IntegrationAction.None, pr.Merged ? "the pull request is already merged" : "the pull request is closed");
            if (pr.AutoMerge && SameAccount(pr.AutoMergeBy, appLogin))
            {
                await host.DisableAutoMergeAsync(target.Repository, pr.NodeId, ct);
                report("auto-merge that chargehand armed turned off: chargehand merges only the head its own review cleared");
            }
            if (mode != IntegrationMode.Merge)
                return new(IntegrationAction.None, "this repository does not merge by itself");
            pr = pr with { Comments = [.. pr.Comments, .. appComments ?? []] };
            if (string.IsNullOrEmpty(reviewedSha) || !string.Equals(pr.HeadSha, reviewedSha, StringComparison.OrdinalIgnoreCase))
                return new(IntegrationAction.None, "no clean review of the current head; not merged");
            return await MergeReviewedAsync(target, pr, report, ct);
        }
        catch (IntegrationException e)
        {
            return Say(report, IntegrationAction.Failed, $"approval stopped: {e.Message}");
        }
    }

    private async Task<IntegrationResult> MergeReviewedAsync(IntegrationTarget target, PrSnapshot pr, Action<string> report, CancellationToken ct)
    {
        if (pr.IsDraft || pr.Comments.Any(c => c.Contains(TestIntegrityMarker, StringComparison.Ordinal) || c.Contains(AskMarker, StringComparison.Ordinal) || c.Contains(NoChecksMarker, StringComparison.Ordinal)))
            return new(IntegrationAction.None, "a draft or a pull request asked about is left for a person; not merged");
        var required = pr.Checks.Where(c => c.Required).ToList();
        if (required.Any(c => c.Outcome is CheckOutcome.Failed or CheckOutcome.Other))
            return new(IntegrationAction.None, "a required check has not passed; not merged");
        if (required.Count == 0 || required.Any(c => c.Outcome == CheckOutcome.Pending))
            return new(IntegrationAction.AwaitingReview, required.Count == 0 ? "no required checks reported; merged once they are green" : "the required checks are still running; merged once they are green");
        if (MigrationPaths.Find(await host.ChangedPathsAsync(target.Repository, target.Number, ct)) is { } migration)
        {
            var told = pr.Comments.Any(c => c.Contains(MigrationMarker, StringComparison.Ordinal));
            await CommentOnceAsync(target, pr, MigrationMarker, $"chargehand does not merge this pull request: it adds a database migration ({migration}). The repository owner merges it.", ct);
            if (!told)
                await NeedsYouAsync(target, ct);
            return Say(report, IntegrationAction.Asked, $"not merged because it adds a database migration ({migration})");
        }
        if (await host.ChecksGateAsync(target.Repository, pr.BaseBranch, ct) != ChecksGate.Required)
            return new(IntegrationAction.None, "the base branch requires no status check; not merged");
        if (pr.MergeStatus == "DIRTY")
            return new(IntegrationAction.None, "the pull request conflicts with its base; not merged");
        return await MergeNowAsync(target, pr.HeadSha, report, ct);
    }
}
