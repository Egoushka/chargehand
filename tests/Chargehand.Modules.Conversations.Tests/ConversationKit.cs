using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Conversations.Api;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Modules.Conversations.Tests;

/// <summary>A clock that moves only when a test says so.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>A run log that knows only what an actor spent, which is all the service asks of it.</summary>
internal sealed class SpendLog(Func<string, DateTimeOffset, ActorSpend> spend) : IRunLog
{
    public Task AppendAsync(StartRecord record, CancellationToken ct) => throw new NotSupportedException();

    public Task AppendAsync(CallRecord record, CancellationToken ct) => throw new NotSupportedException();

    public Task AppendAsync(RunRecord record, CancellationToken ct) => throw new NotSupportedException();

    public Task<RunEntry> ReadAsync(string runId, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<RunSummary>> ListAsync(RunListQuery query, CancellationToken ct) => throw new NotSupportedException();

    public Task<ActorSpend> SpendAsync(string actor, DateTimeOffset since, CancellationToken ct) => Task.FromResult(spend(actor, since));
}

internal static class People
{
    public static Principal Account(string name, decimal? usdPerDay = null, int? concurrent = null, string[]? repositories = null) =>
        new(name, new AccountSettings(name, MaxUsdPerDay: usdPerDay, MaxConcurrentRuns: concurrent, Repositories: repositories, AllowServerCredential: true));
}
