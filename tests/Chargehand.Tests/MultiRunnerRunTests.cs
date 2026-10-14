using System.Collections.Concurrent;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.RunLog;
using Chargehand.Runtimes.ClaudeCode.Api;
using Chargehand.Workspace;

namespace Chargehand.Tests;

/// <summary>ADR 0058: a batch on a server that has several runners. Each runner is a fake engine with its own host figures: the box (amd64, shares the server's disk) and a laptop (arm64, remote).
/// Nothing starts a container or reaches a network.</summary>
public class MultiRunnerRunTests
{
    private const string Image = "ghcr.io/example/session@sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string ArmImage = "ghcr.io/example/session@sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";

    private sealed class FakeHost(HostSnapshot? snapshot) : IHostSource
    {
        public HostSnapshot? Snapshot { get; set; } = snapshot;
        public Exception? Throws { get; set; }

        public Task<HostSnapshot?> ReadAsync(CancellationToken ct) => Throws is null ? Task.FromResult(Snapshot) : Task.FromException<HostSnapshot?>(Throws);
    }

    private static HostSnapshot Free(long availableMb) => new(availableMb, 16000, 0, 0, 0.1, 4);

    /// <summary>One runner: its engine, the session volumes on it and what its host says.</summary>
    private sealed class Machine(DrivenRunnerEntry entry, long freeMb, DrivenRunTests.FakeVolumes volumes)
    {
        public DrivenRunnerEntry Entry { get; } = entry;
        public DrivenRunTests.FakeEngine Engine { get; } = new();
        public DrivenRunTests.FakeVolumes Volumes { get; } = volumes;
        public FakeHost Host { get; } = new(Free(freeMb));
        public RunnerSite Site => new(Entry, Engine, Engine, Volumes, Host);
    }

    private sealed class Rig : IDisposable
    {
        public TempDir Dir { get; } = new();
        public Machine Box { get; }
        public Machine Laptop { get; }
        public DrivenRunTests.FakeHandover Handover { get; } = new();
        public JsonlRunLog Log { get; }
        public Profile Profile { get; private set; }
        public DrivenRun Run { get; private set; }
        public ConcurrentQueue<RunStatus> Events { get; } = new();
        private DrivenServices _services;
        private readonly string? _state;

        public string StateDirectory => _state!;

        public Rig(long boxMb = 6000, long laptopMb = 5500, DrivenRunnerEntry? box = null, DrivenRunnerEntry? laptop = null, bool durable = false, Func<string, SessionStatus>? script = null,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? variants = null, DrivenNetwork? network = null, TimeSpan? outageGrace = null, IStackSeeder? stacker = null,
            Func<string, string, SessionOutcome?>? outcomes = null)
        {
            Log = new JsonlRunLog(Path.Combine(Dir.Path, "log.jsonl"));
            var run = script ?? (_ => SessionStatus.Completed);
            Box = new Machine(box ?? new DrivenRunnerEntry("box", "http://runner:4310", "box-key"), boxMb, new DrivenRunTests.FakeVolumes(run, outcomes));
            Laptop = new Machine(laptop ?? new DrivenRunnerEntry("laptop", "http://laptop.example.net:4310", "laptop-key", Platform: "linux/arm64"), laptopMb, new DrivenRunTests.FakeVolumes(run, outcomes));
            Profile = Runs.Profile(Dir.Path) with
            {
                ClaudeCode = new ClaudeCodeSettings("2.1.283", OauthTokenSecret: "claude-code-oauth-token"),
                Driven = new DrivenSettings(true, MaxParallel: 2, Images: [Image], PushSecret: "secret-push", Network: network ?? new DrivenNetwork(McpForward: "chargehand-host:4300"),
                    Runners: [Box.Entry, Laptop.Entry], ImageVariants: variants),
            };
            _services = new DrivenServices(Box.Engine, Box.Engine, Box.Volumes, _ => throw new NotSupportedException("the handover is faked"), null,
                (repo, _, _) => Task.FromResult(new DrivenCheckout("/srv/checkouts/repo-abc1234", Commit, "https://github.com/o/r.git", "main")), new DrivenRunTests.NoEvidenceProblems(),
                name => $"value-of-{name}", HandoverFor: (_, _) => Handover, Poll: TimeSpan.FromMilliseconds(1), Stacker: stacker, Runners: [Box.Site, Laptop.Site], OutageGrace: outageGrace);
            _state = durable ? Path.Combine(Dir.Path, "batches") : null;
            Run = new DrivenRun(Profile, Log, Repo.Root, _services, _state);
        }

        /// <summary>A new server process on the same run log, state directory and runners; the profile may have changed.</summary>
        public DrivenRun Restart(Func<DrivenSettings, DrivenSettings>? change = null, IReadOnlyList<RunnerSite>? runners = null)
        {
            Run.Dispose();
            if (change is not null)
                Profile = Profile with { Driven = change(Profile.Driven!) };
            _services = _services with { Runners = runners ?? [Box.Site, Laptop.Site] };
            return Run = new DrivenRun(Profile, Log, Repo.Root, _services, _state);
        }

        public Task<ResultContract> RunAsync(RunRequest request, string runId = "run-batch-m", BatchState? resume = null, bool cancelledByCaller = false, CancellationToken ct = default) =>
            Run.RunAsync(request, runId, g => $"tok-{g.RunId}", ct, Events.Enqueue, () => cancelledByCaller, resume);

        public void Dispose()
        {
            Run.Dispose();
            Dir.Dispose();
        }
    }

    private static RunRequest Request(params string[] goals) => Request(null, goals);

    private static RunRequest Request(int? parallel, params string[] goals) =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([.. goals.Select((g, i) => new DrivenTask($"t{i + 1}", Goal: g))], MaxParallel: parallel, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

    private static List<JsonElement> Rows(ResultContract result) =>
        [.. JsonDocument.Parse(result.Artifacts.Single(a => a.Kind == "driven-batch").Content!).RootElement.GetProperty("tasks").EnumerateArray().Select(e => e.Clone())];

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 400; i++)
        {
            if (condition())
                return;
            await Task.Delay(25);
        }
        Assert.Fail("the condition did not hold within 10 seconds");
    }

    private static IEnumerable<string> Networks(DrivenRunTests.FakeEngine engine) => engine.Calls.Where(c => c.StartsWith("network chargehand-net-", StringComparison.Ordinal));

    // --- placement, events and the batch result

    [Fact]
    public async Task Tasks_go_to_the_runner_with_the_most_free_memory_and_every_event_and_row_names_the_runner()
    {
        using var rig = new Rig();

        var result = await rig.RunAsync(Request("Add a retry", "Add a backoff"));

        Assert.Equal(ResultStatus.Completed, result.Status);
        // The box has 6000 free and the laptop 5500: t1 goes to the box, and the box's 4096 are then counted, so t2 goes to the laptop.
        Assert.Single(rig.Box.Engine.Started);
        Assert.Single(rig.Laptop.Engine.Started);
        var rows = Rows(result);
        Assert.Equal(["box", "laptop"], rows.Select(r => r.GetProperty("runner").GetString()));
        var started = rig.Events.Where(e => e.Event == RunEventKind.ContainerStarted).OrderBy(e => e.TaskId).ToList();
        Assert.Equal(["box", "laptop"], started.Select(e => e.Runner));
        Assert.EndsWith("session container started on runner 'box'", started[0].Detail);
        Assert.EndsWith("session container started on runner 'laptop'", started[1].Detail);
        Assert.All(rig.Events.Where(e => e.TaskId is not null && e.Event is not (RunEventKind.SessionProgress or null) && !e.Detail!.Contains("waiting")), e => Assert.NotNull(e.Runner));
        Assert.Equal(["box", "laptop"], rig.Events.Where(e => e.Event == RunEventKind.TaskFinished).OrderBy(e => e.TaskId).Select(e => e.Runner));
        Assert.Equal(2, rig.Handover.Inputs.Count);
    }

    [Fact]
    public async Task A_runner_no_task_goes_to_gets_no_network_no_egress_container_and_no_service()
    {
        using var rig = new Rig(boxMb: 20000, laptopMb: 5500);

        await rig.RunAsync(Request("Add a retry"));

        Assert.Single(Networks(rig.Box.Engine));
        Assert.DoesNotContain(rig.Laptop.Engine.Calls, c => !c.StartsWith("list", StringComparison.Ordinal));
        Assert.Contains("network-rm chargehand-net-run-batch-m", rig.Box.Engine.Calls);
    }

    [Fact]
    public async Task Each_runner_the_batch_uses_gets_its_own_network_and_it_is_removed_there_when_the_batch_ends()
    {
        using var rig = new Rig();

        await rig.RunAsync(Request("one", "two"));

        foreach (var machine in new[] { rig.Box, rig.Laptop })
        {
            Assert.Single(Networks(machine.Engine));
            Assert.Single(machine.Engine.Egresses);
            Assert.Contains("network-rm chargehand-net-run-batch-m", machine.Engine.Calls);
        }
        // The two egress containers carry the same token key, so a run token is good wherever the session is.
        Assert.Equal(rig.Box.Engine.Egresses[0].Gateway!.TokenKey, rig.Laptop.Engine.Egresses[0].Gateway!.TokenKey);
    }

    [Fact]
    public async Task The_result_says_the_runner_for_a_profile_that_names_runners_and_not_for_one_that_has_one()
    {
        using var named = new Rig();
        var withRunners = await named.RunAsync(Request("one"));
        Assert.True(Rows(withRunners).Single().TryGetProperty("runner", out _));

        using var legacy = new DrivenRunTests.Rig();
        var events = new List<RunStatus>();
        var one = await legacy.Run.RunAsync(new RunRequest("request/v1", "Run.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([new DrivenTask("t1", Goal: "one")], MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m)), "run-batch-l", g => $"tok-{g.RunId}", default, events.Add);

        Assert.False(Rows(one).Single().TryGetProperty("runner", out _));
        Assert.All(events, e => Assert.Null(e.Runner));
        Assert.DoesNotContain(events, e => e.Detail?.Contains("on runner", StringComparison.Ordinal) == true);
    }

    // --- what the platform and the forwards change

    [Fact]
    public async Task The_image_follows_the_runners_platform_for_the_session_and_the_egress_container()
    {
        var variants = new Dictionary<string, IReadOnlyDictionary<string, string>> { [Image] = new Dictionary<string, string> { ["linux/arm64"] = ArmImage } };
        using var rig = new Rig(boxMb: 1000, laptopMb: 9000, variants: variants);   // the box has no room, so the arm64 laptop takes it

        await rig.RunAsync(Request("one"));

        Assert.Equal(ArmImage, rig.Laptop.Engine.Started.Single().ImageDigest);
        Assert.Equal(ArmImage, rig.Laptop.Engine.Egresses.Single().Image);
        Assert.Empty(rig.Box.Engine.Started);
    }

    [Fact]
    public async Task A_runner_without_a_variant_uses_the_image_as_written_and_an_amd64_runner_is_not_changed_by_an_arm64_variant()
    {
        var variants = new Dictionary<string, IReadOnlyDictionary<string, string>> { [Image] = new Dictionary<string, string> { ["linux/arm64"] = ArmImage } };
        using var rig = new Rig(boxMb: 9000, laptopMb: 1000, variants: variants);

        await rig.RunAsync(Request("one"));

        Assert.Equal(Image, rig.Box.Engine.Started.Single().ImageDigest);
        Assert.Equal(Image, rig.Box.Engine.Egresses.Single().Image);
        using var plain = new Rig(boxMb: 1000, laptopMb: 9000);
        await plain.RunAsync(Request("one"));
        Assert.Equal(Image, plain.Laptop.Engine.Started.Single().ImageDigest);   // multi-platform digest: the same reference on every platform
    }

    [Fact]
    public async Task A_remote_runners_egress_container_carries_the_forward_to_the_server_as_that_runner_reaches_it()
    {
        var laptop = new DrivenRunnerEntry("laptop", "http://laptop.example.net:4310", "k", Platform: "linux/arm64", Workspace: WorkspaceMode.Bundle,
            Forwards: new DrivenRunnerForwards(Mcp: "server.example.net:4300", Outside: "bridge"));
        using var rig = new Rig(laptop: laptop, network: new DrivenNetwork(McpForward: "chargehand-host:4300", Outside: "chargehand-outside"));

        await rig.RunAsync(Request("one", "two"));

        Assert.Contains("egress run-batch-m [4300=chargehand-host:4300]", rig.Box.Engine.Calls);
        Assert.Contains("egress run-batch-m [4300=server.example.net:4300]", rig.Laptop.Engine.Calls);
        Assert.Contains(rig.Box.Engine.Calls, c => c == "connect egress-1 chargehand-outside");
        Assert.Contains(rig.Laptop.Engine.Calls, c => c == "connect egress-1 bridge");
        // A session calls the server by the same alias whatever runner it is on.
        Assert.All(rig.Box.Engine.Started.Concat(rig.Laptop.Engine.Started), spec => Assert.Equal("http://chargehand-driven:4300/v1/mcp", spec.Env["CHARGEHAND_MCP_URL"]));
    }

    [Fact]
    public async Task A_setting_that_cannot_work_for_one_runner_fails_the_batch_before_anything_is_created_on_either()
    {
        var laptop = new DrivenRunnerEntry("laptop", "http://l:4310", "k", Forwards: new DrivenRunnerForwards(ModelUrl: "http://gateway.example.net:4000/v1"));
        using var rig = new Rig(laptop: laptop);

        var result = await rig.RunAsync(Request("one"));

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Contains("driven.runners.laptop.forwards.model_url", result.Error!.Message);
        Assert.Empty(rig.Box.Engine.Calls);
        Assert.Empty(rig.Laptop.Engine.Calls);
    }

    // --- runners that are away, full or silent

    [Fact]
    public async Task A_runner_that_does_not_answer_is_skipped_and_the_batch_goes_on_without_it()
    {
        using var rig = new Rig(boxMb: 30000, laptopMb: 9000);
        rig.Box.Host.Throws = new RunnerUnreachableException("the runner could not be reached: connection refused");

        var result = await rig.RunAsync(Request("one", "two"));

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal(["laptop", "laptop"], Rows(result).Select(r => r.GetProperty("runner").GetString()));
        Assert.Empty(rig.Box.Engine.Calls);
    }

    [Fact]
    public async Task A_runner_at_its_session_limit_is_passed_over_while_its_session_runs_though_it_has_the_most_memory()
    {
        using var rig = new Rig(boxMb: 40000, laptopMb: 9000, box: new DrivenRunnerEntry("box", "http://runner:4310", "k", MaxSessions: 1));
        rig.Box.Engine.HoldNth = 1;     // the box's first session keeps running until released

        var batch = rig.RunAsync(Request("one", "two"));
        await Until(() => rig.Box.Engine.Started.Count == 1 && rig.Laptop.Engine.Started.Count == 1);
        rig.Box.Engine.Release();
        var result = await batch;

        Assert.Equal(["box", "laptop"], Rows(result).Select(r => r.GetProperty("runner").GetString()));
        Assert.Single(rig.Box.Engine.Started);
    }

    [Fact]
    public async Task A_task_whose_runner_stops_answering_before_its_session_starts_is_placed_on_another_runner()
    {
        using var rig = new Rig(boxMb: 30000, laptopMb: 9000);
        rig.Box.Engine.OnStart = _ => new RunnerUnreachableException("the runner could not be reached: network is unreachable");

        var result = await rig.RunAsync(Request("one"));

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal("laptop", Rows(result).Single().GetProperty("runner").GetString());
        Assert.Single(rig.Laptop.Engine.Started);
        Assert.Contains(rig.Events, e => e.Event == RunEventKind.SessionProgress && e.Detail!.Contains("runner 'box' could not be reached", StringComparison.Ordinal));
        // Nothing of the failed attempt is reported as a started session.
        Assert.DoesNotContain(rig.Events, e => e.Event == RunEventKind.ContainerStarted && e.Runner == "box");
        // The task has one branch, one pull request and one result.
        Assert.Single(rig.Handover.Inputs);
    }

    [Fact]
    public async Task A_runner_that_goes_silent_during_a_session_is_waited_for_and_the_task_finishes_when_it_answers()
    {
        using var rig = new Rig(boxMb: 30000, laptopMb: 1000, outageGrace: TimeSpan.FromSeconds(30));
        rig.Box.Engine.RunFor = 2;
        rig.Box.Engine.OnStart = n => { rig.Box.Engine.SilentInspections = 5; return null; };

        var result = await rig.RunAsync(Request("one"));

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal("box", Rows(result).Single().GetProperty("runner").GetString());
        Assert.Single(rig.Box.Engine.Started);
        Assert.Contains(rig.Events, e => e.Event == RunEventKind.SessionProgress && e.Detail!.Contains("the runner does not answer", StringComparison.Ordinal));
        Assert.Single(rig.Handover.Inputs);
    }

    [Fact]
    public async Task A_runner_that_stays_silent_past_the_grace_fails_the_task_as_container_unavailable()
    {
        using var rig = new Rig(boxMb: 30000, laptopMb: 1000, outageGrace: TimeSpan.FromMilliseconds(40));
        rig.Box.Engine.OnStart = _ => { rig.Box.Engine.SilentInspections = -1; return null; };

        var result = await rig.RunAsync(Request("one"));

        var row = Rows(result).Single();
        Assert.Equal("failed", row.GetProperty("status").GetString());
        Assert.Equal("container_unavailable", row.GetProperty("error_code").GetString());
        Assert.Empty(rig.Handover.Inputs);
    }

    [Fact]
    public async Task With_no_runner_usable_the_task_waits_and_the_hold_names_each_runner()
    {
        using var rig = new Rig(boxMb: 30000, laptopMb: 9000);
        rig.Box.Host.Throws = new RunnerUnreachableException("down");
        rig.Laptop.Host.Throws = new RunnerUnreachableException("down");
        using var stop = new CancellationTokenSource();

        var batch = rig.RunAsync(Request("one"), cancelledByCaller: true, ct: stop.Token);
        await Until(() => rig.Events.Any(e => e.Event == RunEventKind.SessionProgress && e.Detail!.Contains("waiting for a runner (box: unreachable (down)", StringComparison.Ordinal)));
        await stop.CancelAsync();
        var result = await batch;

        // Cancelled while it waited: the task never started, and the hold said why.
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
        Assert.Empty(rig.Box.Engine.Started);
        Assert.Empty(rig.Laptop.Engine.Started);
    }

    // --- restart, sweep

    [Fact]
    public async Task A_restarted_server_finds_each_task_on_the_runner_it_was_placed_on_and_starts_nothing_again()
    {
        using var rig = new Rig(durable: true);
        rig.Box.Engine.HoldNth = 1;
        rig.Laptop.Engine.HoldNth = 1;
        using var stop = new CancellationTokenSource();
        var first = rig.Run.RunAsync(Request("one", "two"), "run-batch-r", g => $"tok-{g.RunId}", stop.Token, null, () => false);
        await Until(() => rig.Box.Engine.Started.Count == 1 && rig.Laptop.Engine.Started.Count == 1);
        await stop.CancelAsync();
        await first;

        var state = Assert.Single(await rig.Restart().UnfinishedAsync(default));
        Assert.Equal(["box", "laptop"], state.Tasks.Select(t => t.Runner));
        Assert.Equal([TaskPhase.Started, TaskPhase.Started], state.Tasks.Select(t => t.Phase));
        rig.Box.Engine.Release();
        rig.Laptop.Engine.Release();
        var result = await rig.RunAsync(state.Request, "run-batch-r", state);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal(["box", "laptop"], Rows(result).Select(r => r.GetProperty("runner").GetString()));
        // Each session was taken up where it was: no container was started again on either runner, and each runner's batch network was made again.
        Assert.Single(rig.Box.Engine.Started);
        Assert.Single(rig.Laptop.Engine.Started);
        Assert.Equal(2, rig.Box.Engine.Egresses.Count);
        Assert.Equal(2, rig.Laptop.Engine.Egresses.Count);
        Assert.Equal(2, rig.Handover.Inputs.Count);
        Assert.Empty(Directory.GetFiles(rig.StateDirectory, "*.json"));
    }

    [Fact]
    public async Task A_task_that_began_on_a_runner_the_profile_no_longer_has_fails_saying_so_and_the_rest_of_the_batch_goes_on()
    {
        using var rig = new Rig(durable: true);
        rig.Laptop.Engine.HoldNth = 1;
        rig.Box.Engine.HoldNth = 1;
        using var stop = new CancellationTokenSource();
        var first = rig.Run.RunAsync(Request("one", "two"), "run-batch-g", g => $"tok-{g.RunId}", stop.Token, null, () => false);
        await Until(() => rig.Box.Engine.Started.Count == 1 && rig.Laptop.Engine.Started.Count == 1);
        await stop.CancelAsync();
        await first;
        var state = Assert.Single(await rig.Restart(d => d with { Runners = [rig.Box.Entry] }, [rig.Box.Site]).UnfinishedAsync(default));

        rig.Box.Engine.Release();
        var result = await rig.RunAsync(state.Request, "run-batch-g", state);

        var rows = Rows(result);
        Assert.Equal("completed", rows[0].GetProperty("status").GetString());
        Assert.Equal("failed", rows[1].GetProperty("status").GetString());
        Assert.Contains("runner 'laptop' is not in the profile any more", rows[1].GetProperty("detail").GetString());
    }

    [Fact]
    public async Task The_sweep_after_a_restart_cleans_every_runner_and_a_runner_that_is_away_is_swept_before_its_first_task()
    {
        using var rig = new Rig(boxMb: 1000, laptopMb: 9000);
        rig.Box.Engine.Resources = new(["chargehand-orphan-b"], ["chargehand-net-orphan-b"], ["chargehand-work-orphan-b", "chargehand-out-orphan-b"]);
        rig.Laptop.Engine.Resources = new(["chargehand-orphan-l"], ["chargehand-net-orphan-l"], ["chargehand-work-orphan-l"]);
        rig.Laptop.Engine.OnList = new RunnerUnreachableException("the runner could not be reached: asleep");

        var removed = await rig.Run.SweepOrphansAsync([], default);

        // The box was swept; the laptop did not answer, which does not stop the server from starting.
        Assert.Equal(["chargehand-orphan-b", "chargehand-net-orphan-b", "chargehand-work-orphan-b", "chargehand-out-orphan-b"], removed);
        Assert.Contains("rm chargehand-orphan-b", rig.Box.Engine.Calls);
        Assert.DoesNotContain(rig.Laptop.Engine.Calls, c => c.StartsWith("rm ", StringComparison.Ordinal));

        // It wakes; the first task placed on it finds it swept first.
        rig.Laptop.Engine.OnList = null;
        var result = await rig.RunAsync(Request("one"));

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal("laptop", Rows(result).Single().GetProperty("runner").GetString());
        var calls = rig.Laptop.Engine.Calls;
        Assert.True(calls.IndexOf("rm chargehand-orphan-l") < calls.IndexOf(calls.First(c => c.StartsWith("start ", StringComparison.Ordinal))));
        Assert.Contains("network-rm chargehand-net-orphan-l", calls);
        Assert.Contains("volume-rm chargehand-work-orphan-l", calls);
    }

    // --- tasks that read another task's work

    [Fact]
    public async Task A_task_that_depends_on_another_reads_its_bundle_from_the_runner_the_other_ran_on()
    {
        var stacker = new DrivenDependsOnTests.FakeStacker();
        using var rig = new Rig(boxMb: 8000, laptopMb: 7000, stacker: stacker);
        var request = new RunRequest("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([new DrivenTask("a", Goal: "first"), new DrivenTask("b", Goal: "second", DependsOn: ["a"])], MaxParallel: 1, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

        var result = await rig.RunAsync(request);

        Assert.Equal(ResultStatus.Completed, result.Status);
        var rows = Rows(result);
        var firstRun = rows[0].GetProperty("run_id").GetString()!;
        // The box has room for one task at a time, so the second goes to the laptop.
        Assert.Equal(["box", "laptop"], rows.Select(r => r.GetProperty("runner").GetString()));
        Assert.Single(stacker.Requests);
        // The bundle of the first task is read from the box, whichever runner took the second.
        Assert.Contains(firstRun, rig.Box.Volumes.Fetched);
        Assert.DoesNotContain(firstRun, rig.Laptop.Volumes.Fetched);
    }

    private static SessionOutcome Done(int commits = 4) => new(SessionStatus.Completed, "", 30, 1000, 200, 4000, null, 0, [], true, Commits: commits);

    [Fact]
    public async Task A_session_stopped_by_the_turn_limit_continues_on_the_runner_that_holds_its_bundle()
    {
        using var rig = new Rig(boxMb: 1000, laptopMb: 9000,
            outcomes: (_, goal) => goal.Contains("Continuation ", StringComparison.Ordinal) ? Done() : new SessionOutcome(SessionStatus.Stalled, "turn_limit", 80, 1000, 200, 4000, null, 0, [], true, Commits: 3));

        var result = await rig.RunAsync(Request("one"));

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal(2, rig.Laptop.Engine.Started.Count);
        Assert.Empty(rig.Box.Engine.Started);
        Assert.Equal("laptop", Rows(result).Single().GetProperty("runner").GetString());
        Assert.Single(rig.Laptop.Volumes.Seeds);
    }

    [Fact]
    public async Task A_task_of_a_later_batch_that_continues_an_earlier_one_reads_its_work_from_the_runner_the_earlier_one_ran_on()
    {
        using var rig = new Rig(boxMb: 1000, laptopMb: 9000,
            outcomes: (_, goal) => goal.Contains("The answers from the person", StringComparison.Ordinal) ? Done() : new SessionOutcome(SessionStatus.NeedsInput, "", 5, 1000, 200, 0, null, 0, ["Which database?"], true, Commits: 2));
        var first = await rig.RunAsync(Request("Add a retry"), "run-batch-q");
        var firstRun = Rows(first).Single().GetProperty("run_id").GetString()!;
        Assert.Equal("laptop", Rows(first).Single().GetProperty("runner").GetString());

        // The laptop is now busy and the box has room: the answering task goes to the box, and still finds the bundle where it was left.
        rig.Box.Host.Snapshot = Free(40000);
        rig.Laptop.Host.Snapshot = Free(500);
        rig.Restart();     // a new server process asks the hosts afresh
        var answer = new RunRequest("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([new DrivenTask("t1", Answers: ["Postgres"], ContinueFrom: new ContinueFrom("run-batch-q", "t1"))], MaxParallel: 1, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));
        var second = await rig.RunAsync(answer, "run-batch-a");

        Assert.Equal(ResultStatus.Completed, second.Status);
        Assert.Equal("box", Rows(second).Single().GetProperty("runner").GetString());
        Assert.Contains(firstRun, rig.Laptop.Volumes.Fetched.Skip(1));     // read again from the laptop, which kept it
        Assert.DoesNotContain(firstRun, rig.Box.Volumes.Fetched);
        Assert.Single(rig.Box.Volumes.Seeds);                              // and written to the box as the new session's seed
    }
}
