namespace Chargehand.Modules.Oversight.Api;

/// <summary>One cluster the weekly failure-autopsy job found (ADR 0045): autopsies of one class and repository in its window, at least <c>driven.learning.autopsy_cluster_min</c> of them.</summary>
public sealed record AutopsyCluster(string Class, string Repository, int Count, DateTimeOffset WindowStart, DateTimeOffset WindowEnd);

/// <summary>Where a failure-autopsy cluster goes: the extension point for filing a tracker item, <see cref="IRetroSink"/>'s sibling. chargehand has no write path to a tracker yet (a task source only
/// reads), so no sink ships; a deployment that adds one is called for each of a run's clusters, most frequent first, and a sink that throws is reported and ignored.</summary>
public interface IAutopsyClusterSink
{
    Task PublishAsync(AutopsyCluster cluster, CancellationToken ct);
}

/// <summary>The weekly job over the failure autopsies in the learning log (ADR 0045): groups the window's autopsies by class and repository, keeps the clusters with at least <paramref name="min"/>
/// of them, and files a tracker item for the <paramref name="top"/> most frequent through the sink, most frequent first. Read-only on the store; does nothing without a sink.</summary>
public sealed class AutopsyClusterJob(ILearningStore store, IAutopsyClusterSink sink, TimeProvider? clock = null)
{
    /// <returns>The clusters filed.</returns>
    public async Task<int> RunAsync(int days, int min, int top, CancellationToken ct)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var since = now - TimeSpan.FromDays(days);
        var clusters = (await store.ReadAsync(ct, includeBench: true))
            .Where(e => e.Autopsy is { } a && a.At >= since)
            .GroupBy(e => (e.Autopsy!.Class, e.Record.Repository))
            .Select(g => new AutopsyCluster(g.Key.Class, g.Key.Repository, g.Count(), since, now))
            .Where(c => c.Count >= min)
            .OrderByDescending(c => c.Count)
            .Take(top)
            .ToList();
        var filed = 0;
        foreach (var cluster in clusters)
        {
            try
            {
                await sink.PublishAsync(cluster, ct);
                filed++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Console.Error.WriteLine($"chargehand: the autopsy cluster sink failed for {cluster.Class}/{cluster.Repository}: {e.Message}");
            }
        }
        return filed;
    }
}
