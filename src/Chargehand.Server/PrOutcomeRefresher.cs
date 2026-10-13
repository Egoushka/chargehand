using Chargehand.Driven;
using Microsoft.Extensions.Hosting;

namespace Chargehand.Server;

/// <summary>Reads back what became of the pull requests chargehand opened (ADR 0045): once when the server starts, then every <c>driven.learning.pr_check_minutes</c>. A pass that fails is reported and the next
/// one tries again; it never stops the server.</summary>
internal sealed class PrOutcomeRefresher(DrivenRun driven) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(driven.PrCheckEvery);
        try
        {
            do
            {
                try
                {
                    if (await driven.RefreshPrOutcomesAsync(ct) is > 0 and var written)
                        Console.Error.WriteLine($"chargehand serve: read the outcome of {written} pull request(s)");
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"chargehand serve: could not read pull request outcomes: {e.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
