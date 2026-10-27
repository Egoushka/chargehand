using System.Globalization;
using Chargehand.Automation;
using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Microsoft.Extensions.Hosting;

namespace Chargehand.Server;

/// <summary>Runs GitHub intake while the server runs (ADR 0051): started with the host, after <see cref="BatchResumer"/> has taken up interrupted batches, and stopped with it. A triggered batch is
/// an ordinary run of this server's <see cref="RunService"/>.</summary>
internal sealed class IntakeService(GitHubIntake intake, RunService runs, AccountSettings? account = null) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;

    public Task StartAsync(CancellationToken ct)
    {
        _loop = Task.Run(() => intake.RunAsync(new RunServiceStarter(runs, account), _stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _stop.CancelAsync();
        await _loop.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public void Dispose() => _stop.Dispose();
}

/// <summary>Starts a batch on this server's run service; a halted server, or one with too many unfinished runs, refuses it and intake tries again at the next poll. With
/// <paramref name="account"/> (<c>automation.account</c>, ADR 0076) the batch is admitted and charged as that account: its caps and clamps apply, and with <c>pause_on_forecast</c> a month
/// forecast over its cap holds the start too. Every refusal is retried later by intake and schedules alike.</summary>
internal sealed class RunServiceStarter(RunService runs, AccountSettings? account = null) : IBatchStarter
{
    public async Task<BatchStart> StartAsync(RunRequest request, CancellationToken ct)
    {
        if (runs.Halted)
            return new BatchStart(null, RunService.HaltedMessage);
        var actor = Principal.Owner.Name;
        // Admitted without an account too: a paused credential refuses the start there as well, and intake retries it.
        var (admitted, refusal) = await runs.AdmitAsync(account is null ? null : new Principal(account.Name, account), request, ct);
        if (refusal is not null)
            return new BatchStart(null, account is null ? refusal.Message : $"account '{account.Name}': {refusal.Message}");
        if (account is not null)
        {
            if (account.PauseOnForecast && await runs.ForecastAsync(account, ct, runs.ReservedUsd(account.Name) + (admitted.Driven?.MaxUsdTotal ?? 0)) is { OverCap: true } forecast)
                return new BatchStart(null, string.Create(CultureInfo.InvariantCulture,
                    $"account '{account.Name}' is paused: its month forecast of ${forecast.ProjectedUsd:0.00} passes max_usd_per_month ${forecast.CapUsd:0.00} (pause_on_forecast)"));
            // A budget cut by what running batches have reserved waits for them (ADR 0083); cut by spend alone, it still runs on what is left.
            if (runs.ReservedUsd(account.Name) > 0 && request.Driven?.MaxUsdTotal is { } wanted && admitted.Driven?.MaxUsdTotal < wanted)
                return new BatchStart(null, string.Create(CultureInfo.InvariantCulture,
                    $"account '{account.Name}': the budget of ${wanted:0.00} does not fit beside the ${runs.ReservedUsd(account.Name):0.00} reserved by its running work; waiting for it to finish"));
            (request, actor) = (admitted, account.Name);
        }
        return runs.Start(request, actor: actor) is { } run ? new BatchStart(run.Id) : new BatchStart(null, $"{RunService.MaxUnfinished} runs are unfinished");
    }
}
