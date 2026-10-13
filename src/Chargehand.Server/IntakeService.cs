using Chargehand.Automation;
using Chargehand.Contracts;
using Microsoft.Extensions.Hosting;

namespace Chargehand.Server;

/// <summary>Runs GitHub intake while the server runs (ADR 0049): started with the host, after <see cref="BatchResumer"/> has taken up interrupted batches, and stopped with it. A triggered batch is
/// an ordinary run of this server's <see cref="RunService"/>.</summary>
internal sealed class IntakeService(GitHubIntake intake, RunService runs) : IHostedService, IDisposable
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

/// <summary>Starts a batch on this server's run service; a halted server, or one with too many unfinished runs, refuses it and intake tries again at the next poll.</summary>
internal sealed class RunServiceStarter(RunService runs) : IBatchStarter
{
    public Task<BatchStart> StartAsync(RunRequest request, CancellationToken ct)
    {
        if (runs.Halted)
            return Task.FromResult(new BatchStart(null, RunService.HaltedMessage));
        return Task.FromResult(runs.Start(request) is { } run ? new BatchStart(run.Id) : new BatchStart(null, $"{RunService.MaxUnfinished} runs are unfinished"));
    }
}
