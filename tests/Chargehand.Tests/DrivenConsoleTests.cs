using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Tests;

/// <summary>What <see cref="DrivenRun"/> shows the console (CHARGEHAND-209): the engine, the host source, the session slots, the saved state of unfinished batches and a usage-limit pause.</summary>
public class DrivenConsoleTests
{
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";

    private static string Token(TaskGrant g) => $"tok-{g.RunId}";

    private static RunRequest Request(int parallel, params string[] goals) =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([.. goals.Select((g, i) => new DrivenTask($"t{i + 1}", Goal: g))], MaxParallel: parallel, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

    [Fact]
    public void A_server_with_driven_sessions_off_has_no_engine_host_or_slots_in_use()
    {
        using var off = new DrivenRunTests.Rig(enabled: false);
        Assert.False(off.Run.Enabled);
        Assert.Null(off.Run.Engine);
        Assert.Null(off.Run.Host);
        Assert.Equal(0, off.Run.SessionsRunning);
        Assert.Equal(4, off.Run.MaxParallelTotal);
        Assert.Empty(off.Run.LiveBatches());

        using var on = new DrivenRunTests.Rig();
        Assert.True(on.Run.Enabled);
        Assert.Same(on.Engine, on.Run.Engine);
    }

    [Fact]
    public async Task A_running_batch_holds_session_slots_and_shows_in_the_saved_state_until_it_ends()
    {
        using var rig = new DrivenRunTests.Rig(durable: true);
        rig.Engine.HoldRunning = true;
        using var cts = new CancellationTokenSource();
        var running = rig.Run.RunAsync(Request(2, "Add a retry", "Add a backoff"), "run-batch-live", Token, cts.Token, cancelledByCaller: () => true);
        while (rig.Engine.Started.Count < 2)
            await Task.Delay(5);

        Assert.Equal(2, rig.Run.SessionsRunning);
        var live = Assert.Single(rig.Run.LiveBatches());
        Assert.Equal("run-batch-live", live.RunId);
        Assert.Equal([TaskPhase.Started, TaskPhase.Started], live.Tasks.Select(t => t.Phase));
        Assert.All(live.Tasks, t => Assert.NotNull(t.RunId));

        await cts.CancelAsync();
        await running;

        Assert.Equal(0, rig.Run.SessionsRunning);
        Assert.Empty(rig.Run.LiveBatches());
    }

    [Fact]
    public async Task A_usage_limit_pause_is_published_and_known_while_the_batch_waits_and_gone_when_it_ends()
    {
        using var rig = new DrivenRunTests.Rig(script: goal => goal == "Add a backoff" ? SessionStatus.RateLimited : SessionStatus.Completed);
        using var cts = new CancellationTokenSource();
        List<RunStatus> events = [];
        // One task at a time: the second hits the limit, and the batch holds the third until the limit lifts (at least 15 minutes by default).
        var running = rig.Run.RunAsync(Request(1, "Add a retry", "Add a backoff", "Write the docs"), "run-batch-pause", Token, cts.Token, e => { lock (events) events.Add(e); }, cancelledByCaller: () => true);
        for (var i = 0; i < 400; i++)
        {
            lock (events)
                if (events.Any(e => e.Detail?.Contains("held until", StringComparison.Ordinal) == true))
                    break;
            await Task.Delay(25);
        }

        var pause = Assert.Single(rig.Run.RateLimitPauses(DateTimeOffset.UtcNow));
        Assert.Equal(("run-batch-pause", "t2"), (pause.BatchRunId, pause.TaskId));
        Assert.True(pause.ResetsAt > DateTimeOffset.UtcNow.AddMinutes(14));
        Assert.Empty(rig.Run.RateLimitPauses(pause.ResetsAt.AddSeconds(1)));
        RunStatus said;
        lock (events)
            said = events.Single(e => e.Detail?.Contains("held until", StringComparison.Ordinal) == true);
        Assert.Equal((RunEventKind.SessionProgress, "t2"), (said.Event, said.TaskId));
        Assert.Contains("usage or rate limit", said.Detail, StringComparison.Ordinal);

        await cts.CancelAsync();
        await running;

        Assert.Empty(rig.Run.RateLimitPauses(DateTimeOffset.UtcNow));
    }
}
