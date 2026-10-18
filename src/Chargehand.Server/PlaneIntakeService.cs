using Chargehand.Automation;
using Microsoft.Extensions.Hosting;

namespace Chargehand.Server;

/// <summary>Runs Plane intake while the server runs (ADR 0067), started and stopped like <see cref="IntakeService"/>; a triggered batch is an ordinary run of this server's <see cref="RunService"/>.</summary>
internal sealed class PlaneIntakeService(PlaneIntake intake, RunService runs) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;

    public Task StartAsync(CancellationToken ct)
    {
        _loop = Task.Run(() => intake.RunAsync(new RunServiceStarter(runs), _stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _stop.CancelAsync();
        await _loop.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public void Dispose() => _stop.Dispose();
}
