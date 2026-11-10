using Microsoft.Extensions.Hosting;

namespace Chargehand.Modules.Oversight.Api;

/// <summary>Reads back what became of the pull requests chargehand opened (ADR 0045): once when the host starts, then every <paramref name="every"/> (<c>driven.learning.pr_check_minutes</c>). A pass that fails is
/// reported and the next one tries again; it never stops the host. <paramref name="pass"/> returns how many outcomes it wrote.</summary>
public sealed class PrOutcomeRefresher(TimeSpan every, Func<CancellationToken, Task<int>> pass, TimeProvider? clock = null) : BackgroundService
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
                        Console.Error.WriteLine($"chargehand serve: read the outcome of {written} pull request(s)");
                }
                catch (Exception e) when (!(e is OperationCanceledException && stoppingToken.IsCancellationRequested))
                {
                    Console.Error.WriteLine($"chargehand serve: could not read pull request outcomes: {e.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
