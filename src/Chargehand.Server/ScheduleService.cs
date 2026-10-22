using Chargehand.Automation;
using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Microsoft.Extensions.Hosting;

namespace Chargehand.Server;

/// <summary>Runs the scheduler while the server runs (ADR 0068): started with the host after <see cref="BatchResumer"/>, stopped with it. A scheduled run is an ordinary run of this server's <see cref="RunService"/>.</summary>
internal sealed class ScheduleService(Scheduler scheduler, RunService runs, AccountSettings? account = null) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;

    public Task StartAsync(CancellationToken ct)
    {
        _loop = Task.Run(() => scheduler.RunAsync(new RunServiceStarter(runs, account), _stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _stop.CancelAsync();
        await _loop.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public void Dispose() => _stop.Dispose();
}

/// <summary>The morning report's numbers from the run log and the inbox of the console (the owner's view: every account).</summary>
internal sealed class ServerMorningSource(Func<ConsoleContext> context) : IMorningSource
{
    private const int Titles = 8;

    public async Task<MorningData> ReadAsync(DateTimeOffset since, CancellationToken ct)
    {
        var c = context();
        var index = await c.IndexAsync(ct);
        var starts = index.Starts.Values.Where(s => s.ParentRunId is null && s.Started >= since).ToList();
        var results = starts.Select(s => index.RunOf(s.RunId)?.Result).ToList();
        var inbox = await ConsoleInbox.BuildAsync(c, null, c.Now - ConsoleInbox.DefaultWindow, Titles, ct);
        return new MorningData(starts.Count, results.Count(r => r?.Status == ResultStatus.Completed), results.Count(r => r?.Status == ResultStatus.Failed), results.Count(r => r?.Status == ResultStatus.NeedsInput),
            results.Count(r => r?.Status == ResultStatus.Denied), results.Count(r => r is null), results.Sum(r => r?.Usage.Usd ?? 0), inbox.Counts.Total, [.. inbox.Items.Select(i => $"{i.Kind}: {i.Title}")]);
    }
}
