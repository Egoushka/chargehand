using Chargehand.Driven;
using Microsoft.Extensions.Hosting;

namespace Chargehand.Server;

/// <summary>Runs the weekly failure-autopsy cluster job (ADR 0045), <see cref="PrOutcomeRefresher"/>'s sibling: once when the server starts, then every <c>driven.learning.autopsy_cluster_days</c>. A
/// pass that fails is reported and the next one tries again; it never stops the server.</summary>
internal sealed class AutopsyClusterRefresher(DrivenRun driven, TimeProvider? clock = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(driven.AutopsyClusterEvery, clock ?? TimeProvider.System);
        try
        {
            do
            {
                try
                {
                    if (await driven.RunAutopsyClustersAsync(ct) is > 0 and var filed)
                        Console.Error.WriteLine($"chargehand serve: filed {filed} failure-autopsy cluster(s)");
                }
                catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
                {
                    Console.Error.WriteLine($"chargehand serve: could not run the failure-autopsy cluster job: {e.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
