using Microsoft.Extensions.Hosting;

namespace Chargehand.Modules.Oversight.Api;

/// <summary>The failure autopsy's weekly job (issue #454): files the window's top root-cause clusters once when the host starts, then every <paramref name="every"/>
/// (<c>driven.learning.cluster_check_days</c>). A pass that fails is reported and the next one tries again; it never stops the host.</summary>
public sealed class FailureClusterJob(TimeSpan every, Func<CancellationToken, Task<int>> pass, TimeProvider? clock = null) : BackgroundService
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
                    if (await pass(stoppingToken) is > 0 and var filed)
                        Console.Error.WriteLine($"chargehand serve: filed {filed} failure cluster(s)");
                }
                catch (Exception e) when (!(e is OperationCanceledException && stoppingToken.IsCancellationRequested))
                {
                    Console.Error.WriteLine($"chargehand serve: could not file the failure clusters: {e.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
