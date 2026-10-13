using System.Text.Json;
using Chargehand.Bench;
using Chargehand.Contracts;

namespace Chargehand.Tests;

/// <summary>The benchmark's flow against a server, a workspace and a container runner that are all made up: what is sent, what is measured from the answers, and what is written. Nothing here
/// starts a container or calls a model.</summary>
public sealed class BenchRunnerTests : IDisposable
{
    private const string Pin = "0123456789abcdef0123456789abcdef01234567";
    private const string Image = "registry.example/session@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // ---- fakes ----

    private sealed class FakeWorkspace : IBenchWorkspace
    {
        public List<(string Repo, string Remote)> Prepared { get; } = [];
        public HashSet<string> RemoteBranches { get; } = [];
        public string? PinProblem { get; set; }
        public string? Existing { get; set; }

        public Task<string> PrepareAsync(BenchRepository repository, string remoteUrl, CancellationToken ct)
        {
            Prepared.Add((repository.Name, remoteUrl));
            return Task.FromResult($"/bench/repos/{repository.Name}");
        }

        public Task<string?> FetchBranchAsync(BenchRepository repository, string branch, CancellationToken ct) =>
            Task.FromResult<string?>(RemoteBranches.Contains(branch) ? $"/bench/repos/{repository.Name}" : null);

        public Task<string> BaselineBranchAsync(BenchRepository repository, string branch, CancellationToken ct) => Task.FromResult($"/bench/repos/{repository.Name}");

        public Task<string?> CheckPinAsync(BenchRepository repository, CancellationToken ct) => Task.FromResult(PinProblem);

        public Task<string?> ExistingCommitAsync(BenchRepository repository, CancellationToken ct) => Task.FromResult(Existing);
    }

    private sealed class FakeAcceptance(Func<AcceptanceRequest, AcceptanceResult> answer) : IAcceptanceRunner
    {
        public List<AcceptanceRequest> Seen { get; } = [];

        public Task<AcceptanceResult> RunAsync(AcceptanceRequest request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private sealed class FakeServer(params Func<RunRequest, string, (IReadOnlyList<RunStatus>, ResultContract)>[] batches) : IBenchServer
    {
        private int _next;
        public List<RunRequest> Requests { get; } = [];
        public Dictionary<string, ResultContract> Results { get; } = [];
        public Func<RunRequest, string>? OnSubmit { get; set; }
        public Exception? SubmitFails { get; set; }

        public Task<string> SubmitAsync(RunRequest request, CancellationToken ct)
        {
            if (SubmitFails is { } fails)
                throw fails;
            Requests.Add(request);
            return Task.FromResult($"batch-{Requests.Count}");
        }

        public Task<(IReadOnlyList<RunStatus> Events, ResultContract Result)> FollowAsync(string runId, Action<RunStatus>? onEvent, CancellationToken ct)
        {
            var (events, result) = batches[Math.Min(_next++, batches.Length - 1)](Requests[^1], runId);
            foreach (var e in events)
                onEvent?.Invoke(e);
            return Task.FromResult((events, result));
        }

        public Task<ResultContract?> GetResultAsync(string runId, CancellationToken ct) => Task.FromResult(Results.GetValueOrDefault(runId));
    }

    // ---- builders ----

    private static BenchRepository Repo(string name = "lib", string ecosystem = "python") => new(name, ecosystem, $"https://example.com/org/{name}.git", Pin, "MIT", ["python3", "-m", "unittest"]);

    private static BenchTask Job(string id, string kind = "change", string size = "S", string repository = "lib", IReadOnlyList<string>? answers = null) =>
        new(id, repository, kind, size, $"Goal of {id}.", new BenchAcceptance(Argv: ["check", id, "{base}"]), answers);

    private static TaskSet Set(IReadOnlyList<BenchRepository> repositories, params BenchTask[] tasks) =>
        new(TaskSet.SchemaId, "set", null, repositories, tasks) { Directory = null, Sha256 = "abcdef0123456789abcdef" };

    private static BenchEnvironment Environment(string? image = Image) => new(_ => image, ["registry.npmjs.org", "pypi.org"], "observe", TimeSpan.FromMinutes(7));

    private BenchOptions Options(Func<BenchOptions, BenchOptions>? change = null)
    {
        var options = new BenchOptions(Path.Combine(_dir.Path, "out"), Environment(), RemoteTemplate: "file:///bench/remotes/{name}.git", MaxTokens: 500_000, MaxUsd: 5m);
        return change?.Invoke(options) ?? options;
    }

    private static Artifact Inline(string kind, object value)
    {
        var content = JsonSerializer.Serialize(value);
        return new Artifact(kind, "application/json", "00", Content: content);
    }

    private static ResultContract Result(string runId, ResultStatus status, Artifact[]? artifacts = null, Usage? usage = null, ResultError? error = null) =>
        new("result/v1", runId, "driven", "trace", new PromptChain([], new AsSent("claude-code/1", "driven", "batch", "2026-10-13")), status, "summary", [], [], artifacts ?? [], [], 0.5,
            usage ?? new Usage(0, 0, 0, 0, null), error);

    private static Dictionary<string, object?> Row(string id, string status, string? branch = null, string? prUrl = null, string? error = null, string? detail = null, string? runId = null) => new()
    {
        ["id"] = id, ["run_id"] = runId ?? $"run-{id}", ["status"] = status, ["branch"] = branch, ["pr_url"] = prUrl, ["error_code"] = error, ["detail"] = detail, ["tokens"] = 0,
    };

    private static ResultContract Batch(string runId, params Dictionary<string, object?>[] rows) =>
        Result(runId, rows.All(r => (string?)r["status"] == "completed") ? ResultStatus.Completed : ResultStatus.Failed, [Inline("driven-batch", new { credential_delivery = "token_exchange", tasks = rows })]);

    private static RunStatus Event(RunEventKind kind, string? task, int seconds, string? detail = null) =>
        RunStatus.Of("batch", RunState.Running, kind) with { TaskId = task, At = T0.AddSeconds(seconds), Detail = detail };

    private static RunStatus Progress(string task, int seconds, long tokens, int turns, decimal? usd = 0.25m) =>
        Event(RunEventKind.SessionProgress, task, seconds) with { Tokens = tokens, Turns = turns, CacheRead = tokens * 40, CacheWrite = tokens / 5, UsdEstimate = usd };

    private static AcceptanceResult Accept(bool passed) => new(passed, passed ? 0 : 1, false, 4.5, passed ? "" : "assertion failed");

    private static ResultContract TaskResult(string runId, bool integrityFlag = false, Usage? usage = null) =>
        Result(runId, ResultStatus.Completed,
            [Inline("verification", new { exit_code = 0, passed = true, test_integrity = integrityFlag ? new { flagged = true, reason = "every changed file is a test file" } : null })], usage);

    private StringWriter Out { get; } = new();

    private BenchRunner Runner(FakeWorkspace? workspace, IBenchServer? server, IAcceptanceRunner? acceptance) =>
        new(workspace ?? new FakeWorkspace(), server, acceptance, Out, new FixedTime(T0));

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ---- selection, dry run, refusals ----

    [Fact]
    public async Task Only_picks_tasks_and_an_unknown_id_is_a_usage_error()
    {
        var set = Set([Repo()], Job("a"), Job("b"));
        Assert.Equal(["b"], BenchRunner.Select(set, new HashSet<string> { "b" }, out var none).Select(t => t.Id));
        Assert.Null(none);
        BenchRunner.Select(set, new HashSet<string> { "zzz" }, out var problem);
        Assert.Contains("zzz", problem);

        var exit = await Runner(null, null, null).RunAsync(set, Options(o => o with { Only = new HashSet<string> { "zzz" }, DryRun = true }), default);
        Assert.Equal(2, exit);
        Assert.Contains("--only names tasks the set does not have: zzz", Out.ToString());
    }

    [Fact]
    public async Task A_dry_run_checks_everything_and_starts_nothing()
    {
        var workspace = new FakeWorkspace();
        var set = Set([Repo(ecosystem: "python"), Repo("web", "node")], Job("a"), Job("b", "impossible"), Job("c", repository: "web"));
        var exit = await Runner(workspace, null, null).RunAsync(set, new BenchOptions(Path.Combine(_dir.Path, "out"), Environment(null), DryRun: true), default);
        var text = Out.ToString();
        Assert.Equal(0, exit);
        Assert.Contains("task set set: 3 tasks over 2 repositories", text);
        Assert.Contains("lib (python) https://example.com/org/lib.git @ 0123456789ab: a[S,change] b[S,impossible]", text);
        Assert.Contains("kinds: 2 change, 1 impossible, 0 question", text);
        Assert.Contains("ok   task set is valid", text);
        Assert.Contains("ok   a: acceptance command present", text);
        Assert.Contains("note lib: the profile names no session image for python", text);
        Assert.Contains("note no --remote", text);
        Assert.Contains("note no --max-tokens or --max-usd", text);
        Assert.Contains("governor mode is observe", text);
        Assert.Contains("dry run: nothing was started.", text);
        Assert.Empty(workspace.Prepared);
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, "out")));
    }

    [Fact]
    public async Task A_dry_run_names_package_hosts_the_allowlist_lacks_and_warns_about_a_governor_that_enforces()
    {
        var set = Set([Repo(ecosystem: "maven")], Job("a"));
        var environment = new BenchEnvironment(_ => Image, ["pypi.org"], "enforce", TimeSpan.FromMinutes(5));
        var exit = await Runner(new FakeWorkspace(), null, null).RunAsync(set, new BenchOptions(_dir.Path, environment, DryRun: true), default);
        Assert.Equal(0, exit);
        Assert.Contains("note lib: add repo.maven.apache.org, repo1.maven.org to driven.network.allow", Out.ToString());
        Assert.Contains("governor mode is enforce: the false-stop rate needs observe", Out.ToString());
    }

    [Fact]
    public async Task A_dry_run_fails_on_a_pin_the_upstream_does_not_serve_and_on_a_clone_at_another_commit()
    {
        var workspace = new FakeWorkspace { PinProblem = "fatal: couldn't find remote ref", Existing = "fedcba9876543210fedcba9876543210fedcba98" };
        var exit = await Runner(workspace, null, null).RunAsync(Set([Repo()], Job("a")), new BenchOptions(_dir.Path, Environment(), DryRun: true, CheckPins: true), default);
        Assert.Equal(1, exit);
        Assert.Contains("FAIL lib: upstream serves 0123456789ab: fatal: couldn't find remote ref", Out.ToString());
        Assert.Contains("FAIL lib: existing clone is at the pin: it is at fedcba987654", Out.ToString());
        Assert.Contains("2 checks failed", Out.ToString());
    }

    [Fact]
    public async Task A_token_cap_under_the_per_task_cap_is_flagged_because_no_task_would_start()
    {
        await Runner(new FakeWorkspace(), null, null).RunAsync(Set([Repo()], Job("a")), new BenchOptions(_dir.Path, Environment(), DryRun: true, MaxTokens: 500_000), default);
        Assert.Contains("below the driven preset's per-task cap", Out.ToString());
        var ok = new StringWriter();
        await new BenchRunner(new FakeWorkspace(), null, null, ok).RunAsync(Set([Repo()], Job("a")), new BenchOptions(_dir.Path, Environment(), DryRun: true, MaxTokens: 4_000_000), default);
        Assert.DoesNotContain("below the driven preset's per-task cap", ok.ToString());
    }

    [Fact]
    public async Task A_real_run_without_a_remote_or_a_cap_is_refused_before_anything_starts()
    {
        var server = new FakeServer();
        var set = Set([Repo()], Job("a"));
        Assert.Equal(2, await Runner(null, server, new FakeAcceptance(_ => Accept(true))).RunAsync(set, new BenchOptions(_dir.Path, Environment(), MaxTokens: 1000), default));
        Assert.Contains("needs --remote", Out.ToString());
        Assert.Equal(2, await Runner(null, server, new FakeAcceptance(_ => Accept(true))).RunAsync(set, new BenchOptions(_dir.Path, Environment(), RemoteTemplate: "file:///x/{name}.git"), default));
        Assert.Contains("needs a cap per batch", Out.ToString());
        Assert.Empty(server.Requests);
    }

    // ---- a real run ----

    [Fact]
    public async Task A_batch_per_repository_is_measured_checked_and_written()
    {
        var workspace = new FakeWorkspace();
        workspace.RemoteBranches.UnionWith(["chargehand/run-a", "chargehand/run-b", "chargehand/run-q", "chargehand/run-imp"]);
        var set = Set([Repo()], Job("a", size: "M"), Job("b"), Job("q", "question", answers: ["Use Postgres."]), Job("imp", "impossible"), Job("c"));
        var server = new FakeServer(
            (_, id) => ([
                Event(RunEventKind.ContainerStarted, "a", 1),
                Progress("a", 30, 4000, 11),
                Event(RunEventKind.Governor, "a", 90, "governor observe: checkpoint on repeated_call (repeated call) at turn 20"),
                Event(RunEventKind.TaskFinished, "a", 100) with { Tokens = 4500, Branch = "chargehand/run-a", PrUrl = "https://example.test/pull/1" },
                Event(RunEventKind.ContainerStarted, "b", 2),
                Event(RunEventKind.ContainerStarted, "b", 400, "session container started (continuation 1 of 2, previous stopped by turn_limit)"),
                Progress("b", 500, 9000, 30),
                Event(RunEventKind.TaskFinished, "b", 620) with { Tokens = 9200 },
                Event(RunEventKind.ContainerStarted, "q", 3),
                Event(RunEventKind.Question, "q", 60, "task q: the session asked:\n- which database?"),
                Progress("q", 59, 1200, 5),
                Event(RunEventKind.TaskFinished, "q", 61),
                Event(RunEventKind.ContainerStarted, "imp", 4),
                Progress("imp", 50, 2500, 9),
                Event(RunEventKind.TaskFinished, "imp", 70),
            ], Batch(id,
                Row("a", "completed", "chargehand/run-a", "https://example.test/pull/1"),
                Row("b", "failed", "chargehand/run-b", error: "session_stalled", detail: "partial: stopped by turn_limit"),
                Row("q", "needs_input", "chargehand/run-q"),
                Row("imp", "failed", error: "verification_failed", detail: "the tests failed"),
                Row("c", "not_started", error: "cancelled", detail: "cancelled"))),
            (_, id) => ([
                Event(RunEventKind.ContainerStarted, "q", 1),
                Progress("q", 40, 3000, 14),
                Event(RunEventKind.TaskFinished, "q", 50) with { Tokens = 3100, Branch = "chargehand/run-q" },
            ], Batch(id, Row("q", "completed", "chargehand/run-q", "https://example.test/pull/2"))));
        server.Results["run-a"] = TaskResult("run-a", integrityFlag: true, usage: new Usage(4000, 600, 200_000, 0, null));
        var acceptance = new FakeAcceptance(r => Accept(r.Label is "a" or "q"));

        var exit = await Runner(workspace, server, acceptance).RunAsync(set, Options(), default);

        Assert.Equal(0, exit);
        // One batch for the repository, one more for the question.
        Assert.Equal(2, server.Requests.Count);
        var first = server.Requests[0];
        Assert.Equal("driven", first.Context.Preset);
        Assert.False(first.Context.Interactive);
        Assert.Equal(new RepositoryRef("/bench/repos/lib", Pin), first.Context.Repository);
        Assert.Equal(["python3", "-m", "unittest"], first.Context.Verify);
        Assert.Equal(false, first.Driven!.Plan);
        Assert.Equal((500_000L, 5m), (first.Driven.MaxTokensTotal, first.Driven.MaxUsdTotal));
        Assert.Equal(["a", "b", "q", "imp", "c"], first.Driven.Tasks.Select(t => t.Id));
        Assert.Equal(["M", "S", "S", "S", "S"], first.Driven.Tasks.Select(t => t.Size));
        Assert.Equal("Goal of a.", first.Driven.Tasks[0].Goal);
        Assert.Equal(("lib", "file:///bench/remotes/lib.git"), workspace.Prepared.Single());
        var second = server.Requests[1];
        var answered = Assert.Single(second.Driven!.Tasks);
        Assert.Equal(("q", null, new ContinueFrom("batch-1", "q")), (answered.Id, answered.Goal, answered.ContinueFrom));
        Assert.Equal(["Use Postgres."], answered.Answers);

        // The acceptance checks ran on the branches that exist, with the pin where the check names it.
        Assert.Equal(["a", "b", "q"], acceptance.Seen.Select(r => r.Label));
        var check = acceptance.Seen[0];
        Assert.Equal(("chargehand/run-a", Pin, Image, TimeSpan.FromMinutes(7)), (check.Branch, check.BaseCommit, check.Image, check.Timeout));
        Assert.Equal(["check", "a", Pin], check.Argv);
        Assert.Equal(["registry.npmjs.org", "pypi.org"], check.Allow);

        var results = BenchResults.Parse(File.ReadAllText(Path.Combine(_dir.Path, "out", BenchRunner.ResultsFile)))!;
        Assert.True(results.Complete);
        Assert.Equal(("set", "abcdef0123456789abcdef"), (results.Taskset, results.TasksetSha256));
        var byId = results.Tasks.ToDictionary(t => t.Id);
        Assert.Equal(["a", "b", "q", "imp", "c"], results.Tasks.Select(t => t.Id));

        var a = byId["a"];
        Assert.Equal((Outcomes.Passed, "completed", "chargehand/run-a", "https://example.test/pull/1"), (a.Outcome, a.State, a.Branch, a.PrUrl));
        Assert.Equal((4600L, 11, 0, 99.0), (a.Tokens, a.Turns, a.Continuations, a.WallSeconds));
        Assert.Equal(200_000, a.CacheRead);
        Assert.Equal(0.25m, a.UsdEstimate);
        Assert.True(a.TestIntegrityFlagged);
        Assert.Equal("every changed file is a test file", a.TestIntegrityReason);
        Assert.Equal("checkpoint", Assert.Single(a.Governor).Action);

        var b = byId["b"];
        Assert.Equal((Outcomes.Failed, "session_stalled", 1), (b.Outcome, b.ErrorCode, b.Continuations));
        Assert.False(b.Acceptance!.Passed);

        var q = byId["q"];
        Assert.Equal((Outcomes.Passed, true, true), (q.Outcome, q.AskedQuestion, q.Answered));
        Assert.Equal(4300, q.Tokens);
        Assert.Equal(14 + 5, q.Turns);
        Assert.Equal("https://example.test/pull/2", q.PrUrl);

        var imp = byId["imp"];
        Assert.Equal((Outcomes.HonestStop, null as AcceptanceResult), (imp.Outcome, imp.Acceptance));
        Assert.Equal(("not_started", Outcomes.Failed), (byId["c"].State, byId["c"].Outcome));

        var report = File.ReadAllText(Path.Combine(_dir.Path, "out", BenchRunner.ReportFile));
        Assert.Contains("# Benchmark: set", report);
        Assert.Contains("pass rate 50% (2/4", Out.ToString());
    }

    [Fact]
    public async Task A_repository_whose_batch_cannot_start_is_not_run_and_the_others_still_are()
    {
        var set = Set([Repo(), Repo("web", "node")], Job("a"), Job("w", repository: "web"));
        var server = new FakeServer((_, id) => ([], Batch(id, Row("w", "completed", "chargehand/run-w", "https://example.test/pull/9"))));
        var workspace = new FakeWorkspace();
        workspace.RemoteBranches.Add("chargehand/run-w");
        server.OnSubmit = null;
        var failing = new FailFirstServer(server);
        var exit = await Runner(workspace, failing, new FakeAcceptance(_ => Accept(true))).RunAsync(set, Options(), default);
        Assert.Equal(1, exit);
        var results = BenchResults.Parse(File.ReadAllText(Path.Combine(_dir.Path, "out", BenchRunner.ResultsFile)))!;
        Assert.False(results.Complete);
        Assert.Equal((Outcomes.NotRun, "not_run"), (BenchStats.OutcomeOf(results.Tasks[0]), results.Tasks[0].State));
        Assert.Contains("the server refused the API key", results.Tasks[0].Detail);
        Assert.Equal(Outcomes.Passed, BenchStats.OutcomeOf(results.Tasks[1]));
        Assert.Equal(new Rate(1, 1), BenchStats.Compute(results).Pass);
    }

    private sealed class FailFirstServer(FakeServer inner) : IBenchServer
    {
        private bool _failed;

        public Task<string> SubmitAsync(RunRequest request, CancellationToken ct)
        {
            if (_failed)
                return inner.SubmitAsync(request, ct);
            _failed = true;
            throw new BenchException("the server refused the API key (HTTP 401).");
        }

        public Task<(IReadOnlyList<RunStatus> Events, ResultContract Result)> FollowAsync(string runId, Action<RunStatus>? onEvent, CancellationToken ct) => inner.FollowAsync(runId, onEvent, ct);

        public Task<ResultContract?> GetResultAsync(string runId, CancellationToken ct) => inner.GetResultAsync(runId, ct);
    }

    [Fact]
    public async Task A_batch_that_failed_before_it_started_marks_its_tasks_not_run_with_the_reason()
    {
        var failed = Result("batch-1", ResultStatus.Failed, error: new ResultError(ErrorCode.InvalidRequest, "the repository is not allowed", false));
        var server = new FakeServer((_, _) => ([], failed));
        var exit = await Runner(null, server, new FakeAcceptance(_ => Accept(true))).RunAsync(Set([Repo()], Job("a")), Options(), default);
        Assert.Equal(0, exit);
        var task = BenchResults.Parse(File.ReadAllText(Path.Combine(_dir.Path, "out", BenchRunner.ResultsFile)))!.Tasks.Single();
        Assert.Equal("not_run", task.State);
        Assert.Contains("invalid_request: the repository is not allowed", task.Detail);
    }

    [Fact]
    public async Task A_branch_the_remote_does_not_have_gets_no_acceptance_check()
    {
        var server = new FakeServer((_, id) => ([], Batch(id, Row("a", "completed", "chargehand/run-a", "https://example.test/pull/1"))));
        var acceptance = new FakeAcceptance(_ => Accept(true));
        await Runner(new FakeWorkspace(), server, acceptance).RunAsync(Set([Repo()], Job("a")), Options(), default);
        Assert.Empty(acceptance.Seen);
        var task = BenchResults.Parse(File.ReadAllText(Path.Combine(_dir.Path, "out", BenchRunner.ResultsFile)))!.Tasks.Single();
        Assert.Equal((Outcomes.Failed, null as AcceptanceResult), (task.Outcome, task.Acceptance));
        Assert.Contains("is not on the remote", Out.ToString());
    }

    [Fact]
    public async Task A_cancelled_run_keeps_what_it_measured()
    {
        using var cts = new CancellationTokenSource();
        var server = new FakeServer((_, id) =>
        {
            cts.Cancel();
            return ([], Batch(id, Row("a", "completed", "chargehand/run-a")));
        });
        var workspace = new FakeWorkspace();
        workspace.RemoteBranches.Add("chargehand/run-a");
        var exit = await Runner(workspace, server, new FakeAcceptance(_ => Accept(true))).RunAsync(Set([Repo()], Job("a")), Options(), cts.Token);
        Assert.Equal(1, exit);
        Assert.False(BenchResults.Parse(File.ReadAllText(Path.Combine(_dir.Path, "out", BenchRunner.ResultsFile)))!.Complete);
        Assert.Contains("stopped; the partial results", Out.ToString());
    }

    // ---- parsing what the server returns ----

    [Fact]
    public void The_rows_of_a_batch_come_from_its_artifact()
    {
        var rows = BenchRunner.BatchRows(Batch("b", Row("a", "completed", "chargehand/x", "https://e.test/1"), Row("z", "failed", error: "verification_failed", detail: "d")), out var problem);
        Assert.Null(problem);
        Assert.Equal(["a", "z"], rows.Select(r => r.Id));
        Assert.Equal(("completed", "chargehand/x", "https://e.test/1", null), (rows[0].Status, rows[0].Branch, rows[0].PrUrl, rows[0].ErrorCode));
        Assert.Equal(("verification_failed", "d", "run-z"), (rows[1].ErrorCode, rows[1].Detail, rows[1].RunId));
        Assert.Empty(BenchRunner.BatchRows(Result("b", ResultStatus.Failed, error: new ResultError(ErrorCode.Internal, "boom", false)), out var why));
        Assert.Equal("internal: boom", why);
    }

    [Fact]
    public void The_integrity_flag_is_read_from_the_verification_artifact()
    {
        Assert.Equal((true, "every changed file is a test file"), BenchRunner.TestIntegrity(TaskResult("r", integrityFlag: true)));
        Assert.Equal((false, null), BenchRunner.TestIntegrity(TaskResult("r")));
        Assert.Equal((false, null), BenchRunner.TestIntegrity(Result("r", ResultStatus.Completed)));
    }

    // ---- baseline ----

    [Fact]
    public async Task The_baseline_expects_the_tests_to_pass_a_change_to_fail_and_a_guard_to_hold()
    {
        var set = Set([Repo()], Job("a"), Job("imp", "impossible"));
        var acceptance = new FakeAcceptance(r => r.Label switch
        {
            "lib-test" => Accept(true),
            "a" => Accept(false),
            _ => Accept(true),
        });
        var exit = await Runner(new FakeWorkspace(), null, acceptance).RunAsync(set, Options(o => o with { Baseline = true }), default);
        Assert.Equal(0, exit);
        Assert.Equal(["lib-test", "a", "imp"], acceptance.Seen.Select(r => r.Label));
        Assert.Equal(["python3", "-m", "unittest"], acceptance.Seen[0].Argv);
        Assert.All(acceptance.Seen, r => Assert.Equal("chargehand/bench-baseline-lib", r.Branch));
        Assert.Contains("ok   a: the acceptance fails on the pin", Out.ToString());
        Assert.Contains("ok   imp: the guard passes on the pin", Out.ToString());
        Assert.Contains("no model was called", Out.ToString());
    }

    [Fact]
    public async Task The_baseline_fails_a_check_that_already_passes_and_a_test_command_that_does_not()
    {
        var set = Set([Repo()], Job("a"), Job("imp", "impossible"));
        var acceptance = new FakeAcceptance(r => r.Label switch
        {
            "lib-test" => new AcceptanceResult(false, 2, false, 1, "ModuleNotFoundError: no module named x"),
            "a" => Accept(true),
            _ => Accept(false),
        });
        var exit = await Runner(new FakeWorkspace(), null, acceptance).RunAsync(set, Options(o => o with { Baseline = true }), default);
        var text = Out.ToString();
        Assert.Equal(1, exit);
        Assert.Contains("FAIL lib: the test command passes on the pin (exit 2", text);
        Assert.Contains("ModuleNotFoundError", text);
        Assert.Contains("FAIL a: the acceptance fails on the pin", text);
        Assert.Contains("FAIL imp: the guard passes on the pin", text);
        Assert.Contains("3 checks did not behave", text);
    }

    [Fact]
    public async Task The_baseline_needs_a_session_image_for_the_repository()
    {
        var exit = await Runner(new FakeWorkspace(), null, new FakeAcceptance(_ => Accept(true))).RunAsync(Set([Repo()], Job("a")), Options(o => o with { Baseline = true, Environment = Environment(null) }), default);
        Assert.Equal(1, exit);
        Assert.Contains("the profile names no session image for python", Out.ToString());
    }
}
