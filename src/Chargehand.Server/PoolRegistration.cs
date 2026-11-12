using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Pool.Api;
using Chargehand.Modules.Ledger.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Chargehand.Server;

/// <summary>Registers the task pool (ADR 0099) when the profile has <c>http.pool</c>: the store, the pool with its decisions valve, and the governor.</summary>
internal static class PoolRegistration
{
    public static void AddPool(IServiceCollection services, ServerSettings settings, IRunLog log, Chargehand.Driven.DrivenRun? driven, AccountSettings? account)
    {
        if (settings.Pool is not { } pool)
            return;
        var path = pool.Database ?? throw new InvalidOperationException("http.pool needs a database path (pool.db beside the run log)");
        Action<string> write = line => System.Console.Error.WriteLine("chargehand serve: " + line);
        services.AddSingleton(_ => SqlitePoolStore.Open(path));
        services.AddSingleton(sp =>
        {
            var runs = sp.GetRequiredService<RunService>();
            var dismissals = settings.InboxDismissalsPath is { } file ? new InboxDismissals(file) : null;
            var context = new ConsoleContext(settings, runs, log, driven, new SecretPresence(settings.SecretPresent), dismissals);
            var decisions = new DecisionsCheck(async ct => (await ConsoleInbox.CountsAsync(context, null, ct).ConfigureAwait(false)).Decide, pool.MaxOpenDecisions);
            var starter = new PoolRunStarter(runs, account, settings.Profile?.Automation?.Github?.Repositories, sp.GetRequiredService<TaskPool>, pool, write);
            return new TaskPool(sp.GetRequiredService<SqlitePoolStore>(), pool, [decisions], starter, settings.Clock ?? TimeProvider.System);
        });
        services.AddHostedService(sp => new PoolService(sp.GetRequiredService<TaskPool>(), write));
    }
}
