using Chargehand.Kernel;
using Chargehand.Modules.Forge.Api;

namespace Chargehand.Modules.Oversight.Api;

/// <summary>Issue #452: once a pull request's outcome reads back as <see cref="PrOutcomes.MergedEdited"/>, fetches the commits a person added after chargehand's own last push,
/// classifies each with <see cref="HumanEditClassifier"/>, and asks <see cref="HumanEditLearner"/> whether the one just classified is the occurrence to write up. Nothing here pushes
/// a commit or opens a pull request; <see cref="IProposalSink"/> only records the proposal for a person to act on. Read-only on the forge, like <see cref="PrOutcomeTracker"/>, and
/// meant to run right after it, on the same learning entries.</summary>
public sealed class HumanEditTracker(ILearningStore learning, IHumanEditStore edits, IProposalSink proposals, Func<RemoteRepository, IIntegrationHost?> hostFor, TimeProvider? clock = null)
{
    /// <returns>The human commits classified and recorded.</returns>
    public async Task<int> RefreshAsync(CancellationToken ct)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var history = await edits.ReadAsync(ct);
        var seenShas = history.Select(e => e.Sha).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var written = 0;
        foreach (var entry in await learning.ReadAsync(ct))
        {
            if (entry.Record.Pr is not { HeadSha: { } pushedHead } pr || entry.PrOutcome?.Outcome != PrOutcomes.MergedEdited || hostFor(pr.Remote) is not { } host)
                continue;
            IReadOnlyList<PrCommit> commits;
            try
            {
                commits = await host.CommitsAfterAsync(pr.Remote, pr.Number, pushedHead, ct);
            }
            catch (Exception e) when (e is IntegrationException or HttpRequestException or ChargehandException || e is TaskCanceledException && !ct.IsCancellationRequested)
            {
                // The forge is down, its credential is gone, or the branch no longer holds pushedHead: it is read again at the next pass.
                continue;
            }
            foreach (var commit in commits)
            {
                if (!seenShas.Add(commit.Sha))
                    continue;
                var edit = new HumanEditCommit(now, entry.Record.RunId, pr.Url, commit.Sha, HumanEditClassifier.Classify(commit.Message, commit.ChangedPaths), commit.Message);
                await edits.AppendAsync(edit, ct);
                written++;
                if (HumanEditLearner.Propose(history, edit, now) is { } proposal)
                    await proposals.WriteAsync(proposal, ct);
                history = [.. history, edit];
            }
        }
        return written;
    }
}
