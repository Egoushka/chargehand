using Chargehand.Modules.Accounts.Api;
using Microsoft.Extensions.Hosting;

namespace Chargehand.Server;

/// <summary>Tries each model credential with one call to the intake model: a paused one every <see cref="Period"/>, so it resumes soon after it works
/// again, and a working one every <see cref="HealthyEvery"/> ticks, so a revoked credential is found before a run fails on it. A probe that fails
/// for another reason (a timeout, a network error) changes nothing.</summary>
internal sealed class CredentialProbe(RunService runs, Func<string?, CancellationToken, Task<ModelProbe>> probe, TimeProvider? clock = null) : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    public const int HealthyEvery = 12;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Period, clock ?? TimeProvider.System);
        try
        {
            for (var tick = 1; await timer.WaitForNextTickAsync(ct); tick++)
                foreach (var key in runs.CredentialKeys.Where(k => runs.Credentials.Paused(k) is not null || tick % HealthyEvery == 0))
                    await TryAsync(key, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    internal async Task TryAsync(string key, CancellationToken ct)
    {
        try
        {
            var answer = await probe(key == CredentialHealth.Server ? null : key, ct);
            if (answer.Ok || CredentialHealth.Rejected(answer.Error))
                await runs.ReportCredentialAsync(key, answer.Ok ? null : answer.Error, ct);
        }
        catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
        {
            Console.Error.WriteLine($"chargehand serve: could not probe credential '{key}': {e.Message}");
        }
    }
}
