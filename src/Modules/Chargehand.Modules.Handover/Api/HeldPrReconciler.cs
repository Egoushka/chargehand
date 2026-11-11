using Microsoft.Extensions.Hosting;

namespace Chargehand.Modules.Handover.Api;

/// <summary>Updates a pull request held only by its base branch's failing required checks, once the base turns green (CHARGEHAND-669): once when the host starts, then every <paramref name="every"/>
/// (<c>driven.learning.pr_check_minutes</c>, the cadence the pull-request outcome refresher uses). A pass that fails is reported and the next one tries again; it never stops the host.
/// <paramref name="pass"/> returns how many pull requests it updated.</summary>
public sealed class HeldPrReconciler(TimeSpan every, Func<CancellationToken, Task<int>> pass, TimeProvider? clock = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(every, clock ?? TimeProvider.System);
        try
        {
            do
            {
                try
                {
                    if (await pass(stoppingToken) is > 0 and var updated)
                        Console.Error.WriteLine($"chargehand serve: updated {updated} pull request(s) held only by their base branch");
                }
                catch (Exception e) when (!(e is OperationCanceledException && stoppingToken.IsCancellationRequested))
                {
                    Console.Error.WriteLine($"chargehand serve: could not reconcile held pull requests: {e.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
