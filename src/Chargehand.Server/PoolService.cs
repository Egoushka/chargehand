using Chargehand.Modules.Pool.Api;
using Microsoft.Extensions.Hosting;

namespace Chargehand.Server;

/// <summary>The pool's governor loop (ADR 0099): on start, workers whose batch the run service took up again are adopted and every other claim goes
/// back to the queue; then one tick every <see cref="Period"/>, each starting at most one worker.</summary>
/// <param name="adopt">Returns the workers still live after a restart and watches their end; runs after <see cref="BatchResumer"/>, which is registered first.</param>
internal sealed class PoolService(TaskPool pool, Func<CancellationToken, Task<IReadOnlySet<string>>> adopt, Action<string> log, TimeSpan? period = null) : IHostedService, IDisposable
{
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;

    public Task StartAsync(CancellationToken ct)
    {
        _loop = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var live = await adopt(ct).ConfigureAwait(false);
            var lost = await pool.ReleaseLostAsync(live, ct).ConfigureAwait(false);
            if (lost > 0)
                log($"pool: {lost} worker(s) of a stopped process released; their items went back to the queue");
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            log($"pool: could not settle the workers of a stopped process: {e.GetType().Name}");
        }
        using var timer = new PeriodicTimer(period ?? Period);
        string? held = null;
        do
        {
            try
            {
                var tick = await pool.TickAsync(ct).ConfigureAwait(false);
                var line = tick.Worker is not null ? $"pool: started worker {tick.Worker} for {tick.Scope!.Repository} {tick.Scope.Area}"
                    : tick.Reason is not "empty" ? $"pool: held {tick.Scope?.Repository} {tick.Scope?.Area}: {tick.Reason}" : null;
                // A hold repeats every tick while it lasts; it is said once.
                if (line is not null && line != held)
                    log(line);
                held = tick.Worker is null ? line : null;
            }
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
            {
                log($"pool: tick failed: {e.GetType().Name}");
            }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _stop.CancelAsync();
        await _loop.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public void Dispose() => _stop.Dispose();
}
