using Chargehand.Kernel;
using Chargehand.Modules.Forge.Api;

namespace Chargehand.Driven;

/// <summary>Reads back, through the forge, what became of the pull requests chargehand opened (ADR 0045): merged with the head chargehand left, merged after edits, closed unmerged, still open. It writes
/// a line to the learning store only when the outcome changed, and looks again only at pull requests of tasks that ended in the last <c>days</c> days and are not merged or closed. Read-only on the forge.
/// <paramref name="hostFor"/> gives the forge of a repository, or null when no credential is configured for it: that repository's pull requests are then left unread.</summary>
public sealed class PrOutcomeTracker(ILearningStore store, Func<RemoteRepository, IIntegrationHost?> hostFor, TimeProvider? clock = null)
{
    /// <returns>The outcomes written.</returns>
    public async Task<int> RefreshAsync(int days, CancellationToken ct)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        Dictionary<(ForgeKind, string?), IIntegrationHost?> hosts = [];
        var written = 0;
        foreach (var entry in await store.ReadAsync(ct))
        {
            if (entry.Record.Pr is not { } pr || now - entry.Record.At > TimeSpan.FromDays(days) || entry.PrOutcome is { } known && PrOutcomes.Resolved(known.Outcome))
                continue;
            var key = (pr.Remote.Forge, pr.Remote.Host);
            if (!hosts.TryGetValue(key, out var host))
                hosts[key] = host = hostFor(pr.Remote);
            if (host is null)
                continue;
            PrSnapshot snapshot;
            try
            {
                snapshot = await host.GetAsync(pr.Remote, pr.Number, ct);
            }
            catch (Exception e) when (e is IntegrationException or HttpRequestException or ChargehandException || e is TaskCanceledException && !ct.IsCancellationRequested)
            {
                // The forge is down, its credential is gone or the pull request is: it is read again at the next pass.
                continue;
            }
            var outcome = PrOutcomes.Classify(snapshot, pr.HeadSha);
            if (outcome == entry.PrOutcome?.Outcome)
                continue;
            await store.AppendAsync(new PrOutcomeRecord(entry.Record.RunId, now, outcome, snapshot.HeadSha), ct);
            written++;
        }
        return written;
    }
}
