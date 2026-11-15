using Chargehand.Modules.Ledger.Api;
using Microsoft.Extensions.Hosting;

namespace Chargehand.Server;

/// <summary>Sweeps worker_root/.checkouts of its oldest clones (<see cref="RunService.SweepCheckouts"/>, issue #509), then runs <see cref="RunService.ResumeAsync"/>, when the host starts
/// (ADR 0043). A failure (the runner not up yet) is reported and does not stop the server; the batches it could not take up stay <c>lost</c>.
/// Then it closes the other runs the restart left open, in the run log (CHARGEHAND-260); not after a failed resume, which leaves the batches it could not take up for the next boot.</summary>
internal sealed class BatchResumer(RunService runs, IRunLog? log = null) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            var sweptCheckouts = runs.SweepCheckouts();
            if (sweptCheckouts.Count > 0)
                Console.Error.WriteLine($"chargehand serve: removed {sweptCheckouts.Count} old checkout(s) under worker_root/.checkouts");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A checkout sweep failure (a permission error reading .checkouts) must not keep interrupted batches from resuming below.
            Console.Error.WriteLine($"chargehand serve: could not sweep worker_root/.checkouts: {e.Message}");
        }
        try
        {
            var swept = await runs.ResumeAsync(ct);
            if (swept.Count > 0)
                Console.Error.WriteLine($"chargehand serve: removed {swept.Count} leftover containers, networks or volumes");
            if (log is not null)
            {
                // Only the batches ResumeAsync took up are held at this point.
                var closed = await InterruptedRuns.CloseAsync(log, runs.Held.Select(h => h.Id).ToHashSet(), DateTimeOffset.UtcNow, ct);
                if (closed > 0)
                    Console.Error.WriteLine($"chargehand serve: closed {closed} runs a restart left open");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine($"chargehand serve: could not take up interrupted batches: {e.Message}");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
