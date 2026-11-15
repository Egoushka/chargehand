using Chargehand.Automation;
using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Events.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Pool.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Chargehand.Server;

/// <summary>Composes the issue sweep when intake and the task pool are both configured: it shares intake's GitHub host, heads, state and sources, and the server's pool, run service and event log.
/// The sweep always exists then, even with <c>automation.sweep</c> off, so the console can switch the schedule on and <c>POST /v1/sweep</c> runs one.</summary>
internal static class SweepRegistration
{
    public static void AddSweep(IServiceCollection services, ServerSettings settings, SweepParts? parts, GitHubIntake? intake, IRunLog log, AccountSettings? account)
    {
        if (parts is null || intake is null || settings.Pool is null)
            return;
        var clock = settings.Clock ?? TimeProvider.System;
        Action<string> warn = line => System.Console.Error.WriteLine("chargehand serve: " + line);
        // One instance per file: the state and the splitter's file are each written by one holder.
        var state = new SweepState(Path.Combine(parts.Directory, "sweep.json"));
        services.AddSingleton(sp =>
        {
            var splitter = new EpicSplitter(parts.Host, new RunServiceStarter(sp.GetRequiredService<RunService>(), account), log, async (repository, ct) =>
            {
                var path = parts.Repositories[repository].Path;
                return new RepositoryRef(path, await parts.Heads.CurrentCommitAsync(path, ct));
            }, Path.Combine(parts.Directory, "split.json"), clock, warn);
            var pool = sp.GetRequiredService<TaskPool>();
            var events = settings.Events?.Log ?? new NoEvents();
            SweepHost? host = null;
            host = new SweepHost(parts.Settings, intake.Sources, parts.Repositories, repositories => new IssueSweep(host!.Effective(), repositories, parts.Host, parts.Sorter, state, pool, events,
                parts.State, parts.Heads, parts.Trusted, splitter, clock), clock, Path.Combine(parts.Directory, "sweep-last.json"), warn);
            return host;
        });
        services.AddHostedService(sp => new SweepService(sp.GetRequiredService<SweepHost>()));
    }

    private sealed class NoEvents : IEventLog
    {
        public EventSignal Signal { get; } = new();

        public Task<IReadOnlyList<long>> AppendAsync(IReadOnlyList<NewEvent> batch, CancellationToken ct) => Task.FromResult<IReadOnlyList<long>>([]);

        public Task<IReadOnlyList<StoredEvent>> ReadAsync(EventFilter filter, CancellationToken ct) => Task.FromResult<IReadOnlyList<StoredEvent>>([]);
    }
}

/// <summary>Runs the sweep's clock while the server runs, after the batch resumer and intake have started.</summary>
internal sealed class SweepService(SweepHost host) : IHostedService
{
    private Task _loop = Task.CompletedTask;

    public Task StartAsync(CancellationToken ct)
    {
        _loop = Task.Run(() => host.LoopAsync(CancellationToken.None), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await host.StopAsync(ct);
        await _loop.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
}
