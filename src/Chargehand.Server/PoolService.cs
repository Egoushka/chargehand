using Chargehand.Modules.Pool.Api;
using Microsoft.Extensions.Hosting;

namespace Chargehand.Server;

/// <summary>The pool's governor loop (ADR 0099): on start, claims left by a process that is gone go back to the queue; then one tick every
/// <see cref="Period"/>, each starting at most one worker.</summary>
internal sealed class PoolService(TaskPool pool, Action<string> log, TimeSpan? period = null) : IHostedService, IDisposable
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
        // A worker lives only as long as this process watches its run, so after a restart no worker is live.
        var lost = await pool.ReleaseLostAsync(new HashSet<string>(), ct).ConfigureAwait(false);
        if (lost > 0)
            log($"pool: {lost} item(s) of a stopped process went back to the queue");
        using var timer = new PeriodicTimer(period ?? Period);
        do
        {
            try
            {
                var tick = await pool.TickAsync(ct).ConfigureAwait(false);
                if (tick.Worker is not null)
                    log($"pool: started worker {tick.Worker} for {tick.Scope!.Repository} {tick.Scope.Area}");
                else if (tick.Reason is not "empty")
                    log($"pool: held {tick.Scope?.Repository} {tick.Scope?.Area}: {tick.Reason}");
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
