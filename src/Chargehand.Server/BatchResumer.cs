using Chargehand.Contracts;
using Microsoft.Extensions.Hosting;

namespace Chargehand.Server;

/// <summary>Runs <see cref="RunService.ResumeAsync"/> when the host starts (ADR 0043). A failure (the runner not up yet) is reported and does not stop the server; the batches it
/// could not take up stay <c>lost</c>.</summary>
internal sealed class BatchResumer(RunService runs) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            var swept = await runs.ResumeAsync(ct);
            if (swept.Count > 0)
                Console.Error.WriteLine($"chargehand serve: removed {swept.Count} leftover containers, networks or volumes");
        }
        catch (ChargehandException e)
        {
            Console.Error.WriteLine($"chargehand serve: could not take up interrupted batches: {e.Message}");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
