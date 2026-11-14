using Microsoft.Extensions.Hosting;

namespace Chargehand.Modules.Oversight.Api;

/// <summary>Issue #452: fetches, classifies and proposes from the human commits of pull requests chargehand opened and a person then edited before merge (<see cref="HumanEditTracker"/>) — once when the
/// host starts, then every <paramref name="every"/> (the cadence the pull-request outcome refresher uses). A pass that fails is reported and the next one tries again; it never stops the host.
/// <paramref name="pass"/> returns how many human commits it classified and recorded.</summary>
public sealed class HumanEditRefresher(TimeSpan every, Func<CancellationToken, Task<int>> pass, TimeProvider? clock = null) : BackgroundService
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
                    if (await pass(stoppingToken) is > 0 and var written)
                        Console.Error.WriteLine($"chargehand serve: classified {written} human pull request commit(s)");
                }
                catch (Exception e) when (!(e is OperationCanceledException && stoppingToken.IsCancellationRequested))
                {
                    Console.Error.WriteLine($"chargehand serve: could not read human pull request edits: {e.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
