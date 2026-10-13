using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary>ADR 0045, admission: a batch asks the host for the memory its repository's sessions have measured, through <see cref="DrivenRun"/> and the scheduler, with the host and the learning
/// records faked. The unit rules are in <see cref="MemoryRequestTests"/> and <see cref="HostAdmissionTests"/>.</summary>
public class AdmissionRequestTests
{
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static string Token(TaskGrant g) => $"tok-{g.RunId}";

    private static RunRequest Two() =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([new DrivenTask("t1", Goal: "one"), new DrivenTask("t2", Goal: "two")], MaxParallel: 2, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

    private sealed class FixedHost(HostSnapshot snapshot) : IHostSource
    {
        public Task<HostSnapshot?> ReadAsync(CancellationToken ct) => Task.FromResult<HostSnapshot?>(snapshot);
    }

    private static HostSnapshot Free(long availableMb, double? pressure = null) => new(availableMb, 16000, 0, 0, 0.1, 4, pressure);

    private static async Task Seed(JsonlLearningStore store, int peakMb, int count = 10, string repository = "o/r")
    {
        for (var i = 0; i < count; i++)
            await store.AppendAsync(LearningTests.Record($"{repository}-{i}", repository, at: Now.AddDays(-1)) with { MemoryPeakMb = peakMb }, default);
    }

    private static async Task<(List<RunStatus> Events, DrivenRunTests.FakeEngine Engine)> Run(IHostSource host, Func<JsonlLearningStore, Task>? seed = null, DrivenLimits? limits = null)
    {
        using var dir = new TempDir();
        var store = new JsonlLearningStore(Path.Combine(dir.Path, "learning.jsonl"));
        if (seed is not null)
            await seed(store);
        using var rig = new DrivenRunTests.Rig(learning: store, host: host, limits: limits, outcomes: (_, _) => new SessionOutcome(SessionStatus.Completed, "", 5, 100, 20, 0, null, 0, [], true, Commits: 1));
        rig.Engine.RunFor = 30;
        List<RunStatus> events = [];
        await rig.Run.RunAsync(Two(), "run-batch-adm", Token, default, e => { lock (events) events.Add(e); });
        return (events, rig.Engine);
    }

    private static List<string> Holds(List<RunStatus> events) => [.. events.Where(e => e.Event == RunEventKind.SessionProgress && e.Detail is { } d && d.Contains("waiting for", StringComparison.Ordinal)).Select(e => e.Detail!)];

    [Fact]
    public async Task Recorded_peaks_let_two_sessions_start_where_their_limit_would_let_in_one()
    {
        // 4000 available, 1024 reserved: 2976 free. Two requests of 900 plus the margin fit; two containers limited to 4096 never would.
        var (events, engine) = await Run(new FixedHost(Free(4000)), seed: s => Seed(s, 900));

        Assert.Empty(Holds(events));
        Assert.Equal(2, engine.Started.Count);
    }

    [Fact]
    public async Task Without_history_a_task_asks_for_the_default_and_the_second_waits_when_that_does_not_fit()
    {
        // 2900 - 1024 = 1876: one request of 1536 fits; the second sees 1536 + 128 counted as just started.
        var (events, engine) = await Run(new FixedHost(Free(2900)));

        var hold = Assert.Single(Holds(events));
        Assert.Equal("task t2: waiting for memory (2900 MiB available, 1024 reserved, 1664 just started, 1536 needed)", hold);
        Assert.Equal(2, engine.Started.Count);   // it starts once the first has ended: nothing else is running then
    }

    [Fact]
    public async Task The_request_is_cut_to_the_containers_limit()
    {
        var (events, _) = await Run(new FixedHost(Free(2300)), limits: new DrivenLimits(MemoryMb: 1024));

        Assert.Equal("task t2: waiting for memory (2300 MiB available, 1024 reserved, 1152 just started, 1024 needed)", Assert.Single(Holds(events)));
    }

    [Fact]
    public async Task Peaks_of_another_repository_do_not_set_the_request_while_the_repository_has_enough_of_its_own_and_do_when_it_has_none()
    {
        // This repository's own ten peaks are small; the other repository's are large and must not raise the request.
        var (own, _) = await Run(new FixedHost(Free(4000)), seed: async s => { await Seed(s, 700); await Seed(s, 3000, repository: "o/other"); });
        Assert.Empty(Holds(own));

        // With no peaks of its own the pool of all repositories decides: ten at 3000 MiB make the request 3000 and the second task waits.
        var (pooled, _) = await Run(new FixedHost(Free(6000)), seed: s => Seed(s, 3000, repository: "o/other"));
        Assert.Equal("task t2: waiting for memory (6000 MiB available, 1024 reserved, 3128 just started, 3000 needed)", Assert.Single(Holds(pooled)));
    }

    [Fact]
    public async Task Memory_pressure_holds_the_second_task_through_a_whole_batch_and_it_starts_once_nothing_else_runs()
    {
        var (events, engine) = await Run(new FixedHost(Free(12000, pressure: 35)));

        Assert.StartsWith("task t2: waiting for memory pressure", Assert.Single(Holds(events)));
        Assert.Equal(2, engine.Started.Count);
    }
}
