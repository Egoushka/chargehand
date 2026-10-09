using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Containers;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.RunLog;
using Chargehand.Verification;

namespace Chargehand.Tests;

/// <summary>ADR 0039: a request with a <c>driven</c> block becomes a batch: a task runner per batch, a session container per task, chargehand's handover, one result per task and
/// one for the batch. The runner service, the session and GitHub are fakes; nothing starts a container or reaches a network.</summary>
public class DrivenRunTests
{
    private const string Image = "ghcr.io/example/session@sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";
    private const string PushSecret = "secret-push";
    private const string Report = """{"summary":"Did the task.","claims":[],"tests":{"command":"npm test","exit_code":0}}""";

    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed class FakeEngine : IContainerEngine, IWorkspaceEngine
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _stopped = [];

        public List<string> Calls { get; } = [];
        public List<ContainerSpec> Started { get; } = [];
        public List<EgressSpec> Egresses { get; } = [];
        public bool HoldRunning { get; set; }
        /// <summary>Each container answers running to this many inspections before it exits.</summary>
        public int RunFor { get; set; }
        private readonly Dictionary<string, int> _inspections = [];
        /// <summary>The start call is cancelled after docker made the container, as a cancel in flight does.</summary>
        public bool CancelOnStart { get; set; }

        private void Record(string call)
        {
            lock (_gate)
                Calls.Add(call);
        }

        public Task<string> StartAsync(ContainerSpec spec, CancellationToken ct)
        {
            lock (_gate)
                Started.Add(spec);
            Record($"start {spec.RunId}");
            if (CancelOnStart)
                throw new OperationCanceledException();
            return Task.FromResult($"cid-{spec.RunId}");
        }

        public Task SignalAsync(string id, string signal, CancellationToken ct)
        {
            Record($"signal {id} {signal}");
            lock (_gate)
                _stopped.Add(id);
            return Task.CompletedTask;
        }

        public Task<ContainerState> InspectAsync(string id, CancellationToken ct)
        {
            bool running;
            lock (_gate)
            {
                _inspections[id] = _inspections.GetValueOrDefault(id) + 1;
                running = (HoldRunning || _inspections[id] <= RunFor) && !_stopped.Contains(id);
            }
            return Task.FromResult(running ? new ContainerState(ContainerStatus.Running, null, false) : new ContainerState(ContainerStatus.Exited, 0, false));
        }

        public Task RemoveAsync(string id, CancellationToken ct) { Record($"rm {id}"); return Task.CompletedTask; }
        public Task KillAllAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> OwnsAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string> LogsTailAsync(string id, int bytes, CancellationToken ct) => Task.FromResult("");
        public Task CreateVolumeAsync(string name, string runId, CancellationToken ct) { Record($"volume {name}"); return Task.CompletedTask; }
        public Task RemoveVolumeAsync(string name, CancellationToken ct) { Record($"volume-rm {name}"); return Task.CompletedTask; }
        public Task CreateNetworkAsync(string name, string batchId, CancellationToken ct) { Record($"network {name}"); return Task.CompletedTask; }
        public Task RemoveNetworkAsync(string name, CancellationToken ct) { Record($"network-rm {name}"); return Task.CompletedTask; }
        public Task<string> StartEgressAsync(EgressSpec spec, CancellationToken ct) { lock (_gate) Egresses.Add(spec); Record($"egress {spec.BatchId} [{string.Join(' ', spec.Forwards ?? [])}]"); return Task.FromResult("egress-1"); }
        public Task ConnectNetworkAsync(string container, string network, CancellationToken ct) { Record($"connect {container} {network}"); return Task.CompletedTask; }
        public Task PrepareWorkspaceAsync(WorkspaceSpec spec, CancellationToken ct) { Record($"prepare {spec.RunId} {spec.Branch} {spec.Commit}"); return Task.CompletedTask; }
    }

    /// <summary>Plays the session: the output volume holds an outcome for the task's goal, and a bundle and report when it completed.</summary>
    private sealed class FakeVolumes(Func<string, SessionStatus> script) : ISessionVolumes
    {
        private readonly Dictionary<string, string> _goals = [];

        /// <summary>What the session's running tally says while it works; null: it has written none.</summary>
        public TaskUsage? Usage { get; set; }

        public int UsageReads;

        public Task<TaskUsage?> ReadUsageAsync(string runId, CancellationToken ct)
        {
            Interlocked.Increment(ref UsageReads);
            return Task.FromResult(Usage);
        }

        public Task WriteTaskAsync(string runId, SessionTask task, CancellationToken ct)
        {
            lock (_goals)
                _goals[runId] = task.Goal;
            return Task.CompletedTask;
        }

        public Task FetchOutputAsync(string runId, string directory, CancellationToken ct)
        {
            string goal;
            lock (_goals)
                goal = _goals[runId];
            var status = script(goal);
            var outcome = new SessionOutcome(status, status == SessionStatus.Failed ? "session_error" : "", 9, 1000, 200, 0, null, 0, [], status == SessionStatus.Completed);
            File.WriteAllText(Path.Combine(directory, "session-outcome.json"),
                JsonSerializer.Serialize(outcome, Snake));
            if (status == SessionStatus.Completed)
            {
                File.WriteAllText(Path.Combine(directory, "driven-report.json"), Report);
                File.WriteAllText(Path.Combine(directory, "chargehand.bundle"), "bundle");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHandover : IBranchHandover
    {
        public List<HandoverInput> Inputs { get; } = [];

        public Task<HandoverOutcome> RunAsync(HandoverInput input, CancellationToken ct)
        {
            int number;
            lock (Inputs)
            {
                Inputs.Add(input);
                number = Inputs.Count;
            }
            input.Report?.Invoke(new HandoverEvent(RunEventKind.VerifyFinished, "tests passed"));
            input.Report?.Invoke(new HandoverEvent(RunEventKind.Pushed, $"pushed {input.Branch}"));
            input.Report?.Invoke(new HandoverEvent(RunEventKind.PrOpened, "draft pull request opened", $"https://example.test/o/r/pull/{number}"));
            return Task.FromResult(new HandoverOutcome(HandoverStatus.Pushed, "", null, input.Branch, Commit, new PullRequestRef($"https://example.test/o/r/pull/{number}", number),
                new VerificationRecord(["npm", "test"], "detected", 0, "ok", false, TimeSpan.FromSeconds(3), true), ["a.txt"], [], []));
        }
    }

    private sealed class NoEvidenceProblems : IEvidenceResolver
    {
        public Task<IReadOnlyList<EvidenceFailure>> ResolveAsync(ResultContract contract, EvidenceScope scope, CancellationToken ct) => Task.FromResult<IReadOnlyList<EvidenceFailure>>([]);
    }

    private sealed class Rig : IDisposable
    {
        public TempDir Dir { get; } = new();
        public FakeEngine Engine { get; } = new();
        public FakeHandover Handover { get; } = new();
        public JsonlRunLog Log { get; }
        public DrivenRun Run { get; }
        public Profile Profile { get; }

        public Rig(bool enabled = true, Func<string, SessionStatus>? script = null, string? mcpForward = null, string? dir = null, JsonlRunLog? log = null, FakeVolumes? volumes = null,
            string? modelUrl = null, bool apiKey = false)
        {
            Log = log ?? new JsonlRunLog(Path.Combine(Dir.Path, "log.jsonl"));
            Profile = Runs.Profile(dir ?? Dir.Path) with
            {
                ClaudeCode = apiKey ? new ClaudeCodeSettings("2.1.283", ApiKeySecret: "gateway-key") : new ClaudeCodeSettings("2.1.283", OauthTokenSecret: "claude-code-oauth-token"),
                Driven = new DrivenSettings(enabled, MaxParallel: 2, Images: [Image], PushSecret: PushSecret, Network: new DrivenNetwork(McpForward: mcpForward, ModelUrl: modelUrl)),
            };
            var services = enabled
                ? new DrivenServices(Engine, Engine, volumes ?? new FakeVolumes(script ?? (_ => SessionStatus.Completed)), _ => throw new NotSupportedException("the handover is faked"), null,
                    (repo, _, _) => Task.FromResult(new DrivenCheckout("/srv/checkouts/repo-abc1234", Commit, "https://github.com/o/r.git", "main")), new NoEvidenceProblems(),
                    name => $"value-of-{name}", HandoverFor: (_, _) => Handover, Poll: TimeSpan.FromMilliseconds(1))
                : null;
            Run = new DrivenRun(Profile, Log, Repo.Root, services);
        }

        public void Dispose()
        {
            Run.Dispose();
            Dir.Dispose();
        }
    }

    private static RunRequest Request(params string[] goals) =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([.. goals.Select((g, i) => new DrivenTask($"t{i + 1}", Goal: g))], MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

    private static string Token(TaskGrant g) => $"tok-{g.RunId}";

    private static JsonElement BatchTasks(ResultContract result) =>
        JsonDocument.Parse(result.Artifacts.Single(a => a.Kind == "driven-batch").Content!).RootElement.GetProperty("tasks");

    [Fact]
    public async Task Two_tasks_end_in_two_draft_pull_requests_each_with_its_own_result()
    {
        using var rig = new Rig(mcpForward: "chargehand-host:4300");
        var result = await rig.Run.RunAsync(Request("Add a retry", "Add a backoff"), "run-batch-1", Token, default);

        Assert.Equal(ResultStatus.Completed, result.Status);
        var tasks = BatchTasks(result).EnumerateArray().ToList();
        Assert.Equal(["t1", "t2"], tasks.Select(t => t.GetProperty("id").GetString()));
        Assert.Equal(["completed", "completed"], tasks.Select(t => t.GetProperty("status").GetString()));
        Assert.Equal(2, tasks.Select(t => t.GetProperty("pr_url").GetString()).Distinct().Count());
        Assert.Equal("environment", JsonDocument.Parse(result.Artifacts.Single(a => a.Kind == "driven-batch").Content!).RootElement.GetProperty("credential_delivery").GetString());

        Assert.Equal(2, rig.Handover.Inputs.Count);
        Assert.All(rig.Handover.Inputs, i => Assert.Equal(Commit, i.BaseCommit));
        Assert.All(rig.Handover.Inputs, i => Assert.Equal("https://github.com/o/r.git", i.RemoteUrl));
        Assert.All(rig.Handover.Inputs, i => Assert.Equal($"value-of-{PushSecret}", i.Credential!.Value));

        // One container per task, from the one image's CLI verb, on the batch network, with the model credential and the run token and never the push credential.
        Assert.Equal(2, rig.Engine.Started.Count);
        foreach (var spec in rig.Engine.Started)
        {
            Assert.Equal(["session"], spec.Command);
            Assert.Equal("chargehand-net-run-batch-1", spec.Network);
            Assert.Equal("value-of-claude-code-oauth-token", spec.Env["CLAUDE_CODE_OAUTH_TOKEN"]);
            Assert.Equal($"tok-{spec.RunId}", spec.Env["CHARGEHAND_RUN_TOKEN"]);
            Assert.Equal("http://chargehand-driven:4300/v1/mcp", spec.Env["CHARGEHAND_MCP_URL"]);
            Assert.Equal("chargehand-egress-run-batch-1,chargehand-driven", spec.Env["NO_PROXY"]);
            // The token admits exactly this path and commit, so the session is told them instead of having to guess.
            Assert.Equal(Commit, spec.Env["CHARGEHAND_BASE_COMMIT"]);
            Assert.False(string.IsNullOrEmpty(spec.Env["CHARGEHAND_REPOSITORY_PATH"]));
            Assert.DoesNotContain("value-of-secret-push", spec.Env.Values);
            Assert.Contains($"rm cid-{spec.RunId}", rig.Engine.Calls);
            Assert.Contains($"volume-rm chargehand-work-{spec.RunId}", rig.Engine.Calls);
        }
        Assert.Contains("egress run-batch-1 [4300=chargehand-host:4300]", rig.Engine.Calls);
        Assert.Contains("network-rm chargehand-net-run-batch-1", rig.Engine.Calls);

        // Each task's own result is in the run log under its own id, with the batch as its parent; so is the batch.
        foreach (var task in tasks)
        {
            var entry = await rig.Log.ReadAsync(task.GetProperty("run_id").GetString()!, default);
            Assert.Equal("run-batch-1", entry.Start!.ParentRunId);
            Assert.Equal(ResultStatus.Completed, entry.Run!.Result.Status);
            Assert.Contains(entry.Run.Result.Artifacts, a => a.Kind == "pull-request");
        }
        Assert.Equal(ResultStatus.Completed, (await rig.Log.ReadAsync("run-batch-1", default)).Run!.Result.Status);
    }

    [Fact]
    public async Task A_running_task_shows_on_the_batch_run_step_by_step()
    {
        var volumes = new FakeVolumes(_ => SessionStatus.Completed) { Usage = new TaskUsage(48_200, 0, 12, SessionStage.Write) };
        using var rig = new Rig(volumes: volumes);
        rig.Engine.RunFor = 3;
        List<RunStatus> events = [];
        await rig.Run.RunAsync(Request("Add a retry"), "run-batch-11", Token, default, e => { lock (events) events.Add(e); });

        var task = events.Where(e => e.TaskId == "t1").ToList();
        Assert.All(task, e => Assert.Equal(("run-batch-11", RunState.Running), (e.RunId, e.Status)));
        Assert.Equal([RunEventKind.ContainerStarted, RunEventKind.SessionProgress, RunEventKind.VerifyFinished, RunEventKind.Pushed, RunEventKind.PrOpened, RunEventKind.TaskFinished],
            task.Select(e => e.Event!.Value));
        var progress = task.Single(e => e.Event == RunEventKind.SessionProgress);     // the tally did not change between polls, so it is published once
        Assert.Equal((48_200L, 12, SessionStage.Write), (progress.Tokens, progress.Turns, progress.Stage));
        Assert.Contains("48200 tokens", progress.Detail);
        Assert.Equal("https://example.test/o/r/pull/1", task.Single(e => e.Event == RunEventKind.PrOpened).PrUrl);
        var finished = task.Single(e => e.Event == RunEventKind.TaskFinished);
        Assert.Equal("https://example.test/o/r/pull/1", finished.PrUrl);
        Assert.StartsWith("chargehand/", finished.Branch);
        Assert.Contains("completed", finished.Detail);
        Assert.Equal(RunEventKind.RunFinished, events[^1].Event);
    }

    [Fact]
    public async Task One_failed_task_makes_the_batch_tasks_incomplete_naming_it()
    {
        using var rig = new Rig(script: goal => goal == "Add a backoff" ? SessionStatus.Failed : SessionStatus.Completed);
        var result = await rig.Run.RunAsync(Request("Add a retry", "Add a backoff"), "run-batch-2", Token, default);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Equal(ErrorCode.TasksIncomplete, result.Error!.Code);
        Assert.Contains("Tasks that did not finish: t2.", result.Error.Action);
        var tasks = BatchTasks(result).EnumerateArray().ToList();
        Assert.Equal(["completed", "failed"], tasks.Select(t => t.GetProperty("status").GetString()));
        Assert.Equal("session_failed", tasks[1].GetProperty("error_code").GetString());
        Assert.Single(rig.Handover.Inputs);                                  // the failed session was never handed over
        Assert.Contains("network-rm chargehand-net-run-batch-2", rig.Engine.Calls);
    }

    [Fact]
    public async Task A_cancel_signals_the_sessions_and_hands_nothing_over()
    {
        using var rig = new Rig();
        rig.Engine.HoldRunning = true;
        using var cts = new CancellationTokenSource();
        List<RunStatus> events = [];
        var running = rig.Run.RunAsync(Request("Add a retry", "Add a backoff"), "run-batch-3", Token, cts.Token, e => { lock (events) events.Add(e); }, cancelledByCaller: () => true);
        while (rig.Engine.Started.Count < 2)
            await Task.Delay(5);
        await cts.CancelAsync();
        var result = await running;

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Equal(ErrorCode.TasksIncomplete, result.Error!.Code);
        Assert.Equal(["cancelled", "cancelled"], BatchTasks(result).EnumerateArray().Select(t => t.GetProperty("status").GetString()));
        Assert.Equal(["t1", "t2"], events.Where(e => e.Event == RunEventKind.TaskFinished).Select(e => e.TaskId!).Order());
        Assert.Empty(rig.Handover.Inputs);
        foreach (var spec in rig.Engine.Started)
        {
            var calls = rig.Engine.Calls.Where(c => c.StartsWith($"signal cid-{spec.RunId} ", StringComparison.Ordinal)).ToList();
            Assert.Equal([$"signal cid-{spec.RunId} SIGINT", $"signal cid-{spec.RunId} SIGKILL"], calls);
            Assert.Contains($"rm cid-{spec.RunId}", rig.Engine.Calls);
        }
        Assert.Contains("network-rm chargehand-net-run-batch-3", rig.Engine.Calls);
    }

    [Fact]
    public async Task A_task_whose_live_usage_passes_its_cap_is_stopped_before_it_ends()
    {
        var volumes = new FakeVolumes(_ => SessionStatus.Completed) { Usage = new TaskUsage(1_000_000_000, 0) };
        using var rig = new Rig(volumes: volumes);
        rig.Engine.HoldRunning = true;
        var result = await rig.Run.RunAsync(Request("Add a retry"), "run-batch-10", Token, default);

        var task = BatchTasks(result).EnumerateArray().Single();
        Assert.Equal("failed", task.GetProperty("status").GetString());
        Assert.Equal(["signal cid-" + rig.Engine.Started.Single().RunId + " SIGINT", "signal cid-" + rig.Engine.Started.Single().RunId + " SIGKILL"],
            rig.Engine.Calls.Where(c => c.StartsWith("signal ", StringComparison.Ordinal)));
        Assert.Empty(rig.Handover.Inputs);
        Assert.True(volumes.UsageReads >= 1);
    }

    [Fact]
    public async Task A_cancel_while_a_container_is_starting_still_removes_it_by_name()
    {
        using var rig = new Rig();
        rig.Engine.CancelOnStart = true;
        using var cts = new CancellationTokenSource();
        await rig.Run.RunAsync(Request("Add a retry"), "run-batch-9", Token, cts.Token, cancelledByCaller: () => true);

        var spec = rig.Engine.Started.Single();
        Assert.Contains($"rm chargehand-{spec.RunId}", rig.Engine.Calls);
    }

    [Fact]
    public async Task A_profile_with_driven_sessions_off_refuses_the_batch_and_touches_nothing()
    {
        using var rig = new Rig(enabled: false);
        var result = await rig.Run.RunAsync(Request("Add a retry"), "run-batch-4", Token, default);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Equal(ErrorCode.InvalidRequest, result.Error!.Code);
        Assert.Contains("driven.enabled", result.Error.Action);
        Assert.Empty(rig.Engine.Calls);
        Assert.Equal(ErrorCode.InvalidRequest, (await rig.Log.ReadAsync("run-batch-4", default)).Run!.Result.Error!.Code);
    }

    [Fact]
    public async Task A_batch_without_a_push_credential_is_refused_before_any_container_exists()
    {
        using var rig = new Rig();
        var withoutPush = new DrivenRun(rig.Profile with { Driven = rig.Profile.Driven! with { PushSecret = null } }, rig.Log, Repo.Root,
            new DrivenServices(rig.Engine, rig.Engine, new FakeVolumes(_ => SessionStatus.Completed), _ => throw new NotSupportedException(), null,
                (_, _, _) => throw new NotSupportedException(), new NoEvidenceProblems(), n => n));
        var result = await withoutPush.RunAsync(Request("Add a retry"), "run-batch-5", Token, default);
        Assert.Equal(ErrorCode.CredentialUnavailable, result.Error!.Code);
        Assert.Empty(rig.Engine.Calls);
    }

    [Fact]
    public async Task A_gateway_url_gives_the_session_a_base_url_through_a_second_forward_and_the_key_it_was_issued()
    {
        using var rig = new Rig(mcpForward: "chargehand-host:4300", modelUrl: "http://gateway-host:4001/anthropic", apiKey: true);
        var result = await rig.Run.RunAsync(Request("Add a retry"), "run-batch-1", Token, default);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal("gateway_key", JsonDocument.Parse(result.Artifacts.Single(a => a.Kind == "driven-batch").Content!).RootElement.GetProperty("credential_delivery").GetString());
        Assert.Contains("egress run-batch-1 [4300=chargehand-host:4300 4001=gateway-host:4001]", rig.Engine.Calls);
        var spec = rig.Engine.Started.Single();
        Assert.Equal("http://chargehand-driven:4001/anthropic", spec.Env["ANTHROPIC_BASE_URL"]);
        Assert.Equal("value-of-gateway-key", spec.Env["ANTHROPIC_API_KEY"]);
        Assert.DoesNotContain("CLAUDE_CODE_OAUTH_TOKEN", spec.Env.Keys);
        Assert.Equal("chargehand-egress-run-batch-1,chargehand-driven", spec.Env["NO_PROXY"]);
        // The key is among the values the diff scan looks for, like any model credential.
        Assert.Contains("value-of-gateway-key", rig.Handover.Inputs.Single().Secrets!);
    }

    [Fact]
    public async Task A_gateway_without_the_chargehand_forward_still_bypasses_the_proxy_for_the_forward()
    {
        using var rig = new Rig(modelUrl: "http://gateway-host:4001/anthropic", apiKey: true);
        await rig.Run.RunAsync(Request("Add a retry"), "run-batch-1", Token, default);

        Assert.Contains("egress run-batch-1 [4001=gateway-host:4001]", rig.Engine.Calls);
        var spec = rig.Engine.Started.Single();
        Assert.Equal("chargehand-egress-run-batch-1,chargehand-driven", spec.Env["NO_PROXY"]);
        Assert.DoesNotContain("CHARGEHAND_MCP_URL", spec.Env.Keys);
    }

    [Fact]
    public async Task An_https_gateway_is_reached_through_the_proxy_so_its_host_joins_the_allowlist_and_no_forward_is_made()
    {
        using var rig = new Rig(modelUrl: "https://gateway.example/anthropic", apiKey: true);
        await rig.Run.RunAsync(Request("Add a retry"), "run-batch-1", Token, default);

        var egress = rig.Engine.Egresses.Single();
        Assert.Contains("gateway.example", egress.Allow);
        Assert.Null(egress.Forwards);
        Assert.Equal("https://gateway.example/anthropic", rig.Engine.Started.Single().Env["ANTHROPIC_BASE_URL"]);
    }

    [Theory]
    [InlineData("http://gateway-host:4001/anthropic", false, "api_key_secret")]             // a subscription token is not a gateway key
    [InlineData("gateway-host:4001", true, "http or https URL")]
    [InlineData("ftp://gateway-host/anthropic", true, "http or https URL")]
    [InlineData("http://user:pw@gateway-host:4001/anthropic", true, "credentials")]
    [InlineData("http://gateway-host:4001/anthropic?key=x", true, "query")]
    [InlineData("http://gateway-host:4300/anthropic", true, "port 4300")]                  // the chargehand forward listens there
    public async Task A_gateway_url_that_cannot_work_is_refused_before_anything_is_created(string url, bool apiKey, string reason)
    {
        using var rig = new Rig(mcpForward: "chargehand-host:4300", modelUrl: url, apiKey: apiKey);
        var result = await rig.Run.RunAsync(Request("Add a retry"), "run-batch-1", Token, default);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Equal(ErrorCode.InvalidRequest, result.Error!.Code);
        Assert.Contains(reason, result.Error.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Engine.Started);
        Assert.DoesNotContain(rig.Engine.Calls, c => c.StartsWith("network ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_request_with_a_driven_block_over_http_runs_the_batch_and_mints_a_token_the_server_accepts()
    {
        Rig? rig = null;
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), driven: (root, log) => (rig = new Rig(mcpForward: "chargehand-host:4300", dir: root, log: log)).Run);
        var res = await s.PostAsync(JsonContent.Create(Request("Add a retry"), options: ContractJson.Options), waitSeconds: 30);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("completed", body.GetProperty("status").GetString());
        var token = rig!.Engine.Started.Single().Env["CHARGEHAND_RUN_TOKEN"];
        var claims = s.Tokens.Validate(token, DateTimeOffset.UtcNow);
        Assert.NotNull(claims);
        Assert.Equal(("/srv/checkouts/repo-abc1234", Commit), (claims.RepositoryPath, claims.Commit));
        Assert.Equal(rig.Engine.Started.Single().RunId, claims.RunId);
    }

    [Fact]
    public async Task A_driven_request_over_http_is_refused_when_the_profile_has_driven_sessions_off()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), driven: (root, log) => new Rig(enabled: false, dir: root, log: log).Run);
        var res = await s.PostAsync(JsonContent.Create(Request("Add a retry"), options: ContractJson.Options), waitSeconds: 30);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("failed", body.GetProperty("status").GetString());
        Assert.Equal("invalid_request", body.GetProperty("error").GetProperty("code").GetString());
    }
}
