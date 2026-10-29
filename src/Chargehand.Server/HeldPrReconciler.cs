using Chargehand.Driven;
using Microsoft.Extensions.Hosting;

namespace Chargehand.Server;

/// <summary>Updates a pull request held only by its base branch's failing required checks, once the base turns green (CHARGEHAND-669): once when the server starts, then every
/// <c>driven.learning.pr_check_minutes</c>, the same cadence <see cref="PrOutcomeRefresher"/> uses. A pass that fails is reported and the next one tries again; it never stops the server.</summary>
internal sealed class HeldPrReconciler(DrivenRun driven, TimeProvider? clock = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(driven.PrCheckEvery, clock ?? TimeProvider.System);
        try
        {
            do
            {
                try
                {
                    if (await driven.ReconcileHeldPrsAsync(ct) is > 0 and var updated)
                        Console.Error.WriteLine($"chargehand serve: updated {updated} pull request(s) held only by their base branch");
                }
                catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
                {
                    Console.Error.WriteLine($"chargehand serve: could not reconcile held pull requests: {e.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
