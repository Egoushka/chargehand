using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Server;
using Chargehand.Workspace;

namespace Chargehand.Tests;

/// <summary>CHARGEHAND-214: cancel one task, retry it, stop its session, re-run a pull request's failed required checks. Each is for the owner and the batch's account, writes an <c>operator</c> event, is safe
/// to send twice and answers 409 when the state forbids it.</summary>
public class ConsoleActionTests
{
    private static RunRequest Batch(int parallel, params string[] goals) =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", ConsoleRig.Commit)),
            Driven: new RequestDriven([.. goals.Select((g, i) => new DrivenTask($"t{i + 1}", Goal: g))], MaxParallel: parallel, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

    private static async Task<bool> Eventually(Func<Task<bool>> check)
    {
        for (var i = 0; i < 400; i++)
        {
            if (await check())
                return true;
            await Task.Delay(25);
        }
        return false;
    }

    private static async Task<string> StartAsync(TestServer s, RunRequest request, HttpClient? as_ = null)
    {
        var post = new HttpRequestMessage(HttpMethod.Post, "/v1/runs") { Content = JsonContent.Create(request, options: ContractJson.Options) };
        post.Headers.Add("Prefer", "wait=0");
        var res = await (as_ ?? s.Http).SendAsync(post);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return res.Headers.Location!.OriginalString.Split('/')[^1];
    }

    private static async Task<string?> TaskState(HttpClient http, string batch, string task)
    {
        var res = await http.GetAsync($"/v1/batches/{batch}");
        if (!res.IsSuccessStatusCode)
            return null;
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tasks").EnumerateArray().FirstOrDefault(t => t.GetProperty("id").GetString() == task).TryGetProperty("state", out var state) ? state.GetString() : null;
    }

    private static Task<HttpResponseMessage> Post(HttpClient http, string path) => http.PostAsync(path, null);

    private sealed class Held : IAsyncDisposable
    {
        public required TestServer Server { get; init; }
        public required DrivenRunTests.Rig Rig { get; init; }

        public async ValueTask DisposeAsync()
        {
            Rig.Engine.HoldRunning = false;
            await Server.DisposeAsync();
        }
    }

    private static async Task<Held> StartHeldAsync(Func<string, SessionStatus>? script = null, IReadOnlyList<AccountSettings>? accounts = null, bool hold = true)
    {
        DrivenRunTests.Rig? rig = null;
        var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: accounts,
            driven: (root, log) =>
            {
                rig = new DrivenRunTests.Rig(dir: root, log: log, durable: true, script: script, maxParallel: 4, records: true);
                rig.Engine.HoldRunning = hold;
                return rig.Run;
            });
        return new Held { Server = server, Rig = rig! };
    }

    [Fact]
    public async Task A_running_task_is_cancelled_alone_the_other_goes_on_an_event_is_written_and_a_second_cancel_changes_nothing()
    {
        await using var h = await StartHeldAsync();
        var batch = await StartAsync(h.Server, Batch(2, "one", "two"));
        Assert.True(await Eventually(() => Task.FromResult(h.Rig.Engine.Started.Count == 2)));
        Assert.True(await Eventually(async () => await TaskState(h.Server.Http, batch, "t1") == "running"));

        var first = await Post(h.Server.Http, $"/v1/batches/{batch}/tasks/t1/cancel");
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.True((await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("changed").GetBoolean());
        Assert.True(await Eventually(async () => await TaskState(h.Server.Http, batch, "t1") == "cancelled"));

        Assert.Equal("running", await TaskState(h.Server.Http, batch, "t2"));
        var again = await Post(h.Server.Http, $"/v1/batches/{batch}/tasks/t1/cancel");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.False((await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("changed").GetBoolean());
        var events = h.Rig.Records!.ReadEvents(batch)!;
        var operatorEvent = Assert.Single(events, e => e.Event == RunEventKind.Operator);
        Assert.Equal(("t1", "task t1: cancelled by an operator"), (operatorEvent.TaskId, operatorEvent.Detail));
        // The task was cancelled as a batch cancel does it: interrupted, then killed, and nothing handed over.
        Assert.Empty(h.Rig.Handover.Inputs);
        Assert.Contains($"signal cid-{h.Rig.Engine.Started[0].RunId} SIGINT", h.Rig.Engine.Calls);
    }

    [Fact]
    public async Task A_task_that_has_not_started_is_cancelled_and_never_starts_and_what_waits_on_it_is_not_started()
    {
        await using var h = await StartHeldAsync();
        var request = Batch(1, "one", "two", "three");
        request = request with { Driven = request.Driven! with { Tasks = [request.Driven.Tasks[0], request.Driven.Tasks[1], request.Driven.Tasks[2] with { DependsOn = ["t2"] }] } };
        var batch = await StartAsync(h.Server, request);
        Assert.True(await Eventually(() => Task.FromResult(h.Rig.Engine.Started.Count == 1)));

        var res = await Post(h.Server.Http, $"/v1/batches/{batch}/tasks/t2/cancel");

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.True(await Eventually(async () => await TaskState(h.Server.Http, batch, "t2") == "cancelled"));
        Assert.True(await Eventually(async () => await TaskState(h.Server.Http, batch, "t3") == "not_started"));
        Assert.Single(h.Rig.Engine.Started);
    }

    [Fact]
    public async Task A_task_that_has_ended_cannot_be_cancelled_and_an_unknown_one_is_not_found()
    {
        await using var h = await StartHeldAsync(hold: false);
        var batch = await StartAsync(h.Server, Batch(1, "one"));
        Assert.True(await Eventually(async () => (await h.Server.Http.GetAsync($"/v1/batches/{batch}")).IsSuccessStatusCode && await TaskState(h.Server.Http, batch, "t1") == "completed"));

        Assert.Equal(HttpStatusCode.Conflict, (await Post(h.Server.Http, $"/v1/batches/{batch}/tasks/t1/cancel")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(h.Server.Http, $"/v1/batches/{batch}/tasks/t9/cancel")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(h.Server.Http, "/v1/batches/run-none/tasks/t1/cancel")).StatusCode);
    }

    [Fact]
    public async Task A_running_sessions_container_is_stopped_and_a_second_stop_finds_no_session()
    {
        await using var h = await StartHeldAsync();
        var batch = await StartAsync(h.Server, Batch(1, "one"));
        Assert.True(await Eventually(async () => await TaskState(h.Server.Http, batch, "t1") == "running" && h.Rig.Engine.Started.Count == 1));
        var run = h.Rig.Engine.Started[0].RunId;

        var res = Eventually(async () => (await Post(h.Server.Http, $"/v1/batches/{batch}/tasks/t1/session/stop")).StatusCode == HttpStatusCode.Accepted);

        Assert.True(await res);
        Assert.Contains($"signal cid-{run} SIGKILL", h.Rig.Engine.Calls);
        Assert.True(await Eventually(async () => await TaskState(h.Server.Http, batch, "t1") is "completed" or "failed"));
        Assert.Equal(HttpStatusCode.Conflict, (await Post(h.Server.Http, $"/v1/batches/{batch}/tasks/t1/session/stop")).StatusCode);
        Assert.Contains(h.Rig.Records!.ReadEvents(batch)!, e => e.Event == RunEventKind.Operator && e.Detail == "task t1: session stopped by an operator");
    }

    [Fact]
    public async Task A_failed_task_is_retried_as_a_new_batch_of_that_task_and_a_second_retry_returns_the_first()
    {
        await using var h = await StartHeldAsync(goal => goal.Contains("boom", StringComparison.Ordinal) ? SessionStatus.Failed : SessionStatus.Completed, hold: false);
        var batch = await StartAsync(h.Server, Batch(2, "boom once", "fine"));
        Assert.True(await Eventually(async () => await TaskState(h.Server.Http, batch, "t1") == "failed" && await TaskState(h.Server.Http, batch, "t2") == "completed"));
        Assert.True(await Eventually(async () => (await h.Server.Http.GetAsync($"/v1/runs/{batch}")).StatusCode == HttpStatusCode.OK));

        var first = await Post(h.Server.Http, $"/v1/batches/{batch}/tasks/t1/retry");
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();
        var second = await Post(h.Server.Http, $"/v1/batches/{batch}/tasks/t1/retry");
        var again = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.True(body.GetProperty("changed").GetBoolean());
        Assert.False(body.GetProperty("continue_from").GetBoolean());
        var run = body.GetProperty("run_id").GetString()!;
        Assert.Equal($"/v1/runs/{run}", first.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal((run, false), (again.GetProperty("run_id").GetString(), again.GetProperty("changed").GetBoolean()));
        // The new batch is the one task, as the old batch asked for it.
        Assert.True(await Eventually(async () => (await h.Server.Http.GetAsync($"/v1/batches/{run}")).IsSuccessStatusCode));
        var retried = await h.Server.Http.GetFromJsonAsync<JsonElement>($"/v1/batches/{run}");
        var task = Assert.Single(retried.GetProperty("tasks").EnumerateArray());
        Assert.Equal(("t1", "boom once"), (task.GetProperty("id").GetString(), task.GetProperty("goal").GetString()));
        Assert.Contains(h.Rig.Records!.ReadEvents(batch)!, e => e.Event == RunEventKind.Operator && e.Detail!.StartsWith($"task t1: retry started as run {run}", StringComparison.Ordinal));
        Assert.True(await Eventually(async () => (await h.Server.Http.GetAsync($"/v1/runs/{run}")).StatusCode == HttpStatusCode.OK));   // the retried batch has finished writing before the directory goes
    }

    [Fact]
    public async Task A_task_that_completed_or_waits_for_an_answer_or_belongs_to_a_batch_still_running_is_not_retried()
    {
        await using var h = await StartHeldAsync();
        var batch = await StartAsync(h.Server, Batch(1, "one"));
        Assert.True(await Eventually(async () => await TaskState(h.Server.Http, batch, "t1") == "running"));
        Assert.True(await Eventually(() => Task.FromResult(h.Rig.Engine.Started.Count == 1)));   // "running" is the admitted task: its container may not have started yet

        Assert.Equal(HttpStatusCode.Conflict, (await Post(h.Server.Http, $"/v1/batches/{batch}/tasks/t1/retry")).StatusCode);
        Assert.Single(h.Rig.Engine.Started);
        h.Rig.Engine.HoldRunning = false;
        Assert.True(await Eventually(async () => await TaskState(h.Server.Http, batch, "t1") == "completed"));
        Assert.True(await Eventually(async () => (await h.Server.Http.GetAsync($"/v1/runs/{batch}")).StatusCode == HttpStatusCode.OK));
        Assert.Equal(HttpStatusCode.Conflict, (await Post(h.Server.Http, $"/v1/batches/{batch}/tasks/t1/retry")).StatusCode);
    }

    // Authorisation and unchanged routes, on batches seeded in the run log

    private static AccountSettings Alice() => new("alice", Driven: true, AllowServerCredential: true);

    private static AccountSettings Bob() => new("bob", Driven: true, AllowServerCredential: true);

    [Fact]
    public async Task Another_account_gets_a_not_found_for_every_action_on_a_batch_and_a_run_token_and_no_key_reach_none()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice(), Bob()]);
        await ConsoleSeed.BatchAsync(rig, "run-b1", "alice", new SeedTask("t1", "failed", Error: ErrorCode.SessionStalled, Detail: "stalled", PrUrl: "https://example.test/o/r/pull/7"));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("run-b1", "t1", LearningOutcome.Failed, "alice", pr: ConsoleSeed.Pull(7)));
        using var bob = rig.As("bob");
        var repo = Runs.GitRepo(rig.Server.WorkerRoot);
        var token = rig.Server.Tokens.Issue(new RunTokenClaims("run-task", repo.Path, repo.Commit, null, null, DateTimeOffset.UtcNow.AddHours(1), "alice"), DateTimeOffset.UtcNow);
        using var session = rig.Server.As(token);
        using var anonymous = new HttpClient { BaseAddress = rig.Server.BaseAddress };
        string[] paths = ["/v1/batches/run-b1/tasks/t1/cancel", "/v1/batches/run-b1/tasks/t1/retry", "/v1/batches/run-b1/tasks/t1/session/stop", "/v1/pulls/o/r/7/checks/rerun"];

        foreach (var path in paths)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await Post(bob, path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Post(session, path)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Post(anonymous, path)).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await rig.Owner.GetAsync(path)).StatusCode);
        }
        Assert.Empty(rig.Server.Service.Held);
        Assert.Empty(rig.Records.ReadEvents("run-b1") ?? []);
    }

    [Fact]
    public async Task The_batchs_account_and_the_owner_may_act_on_a_finished_batch_and_a_retry_belongs_to_the_caller()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice()]);
        await ConsoleSeed.BatchAsync(rig, "run-b1", "alice", new SeedTask("t1", "failed", Error: ErrorCode.SessionStalled, Detail: "stalled"));
        using var alice = rig.As("alice");

        Assert.Equal(HttpStatusCode.Conflict, (await Post(alice, "/v1/batches/run-b1/tasks/t1/cancel")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(rig.Owner, "/v1/batches/run-b1/tasks/t1/session/stop")).StatusCode);
        var retried = await Post(alice, "/v1/batches/run-b1/tasks/t1/retry");

        Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
        var run = (await retried.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;
        Assert.True(await Eventually(async () => (await rig.Log.ReadAsync(run, default)).Start?.Actor == "alice"));   // the run's start record is written after the 202
    }

    [Fact]
    public async Task A_halted_server_refuses_a_retry()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await ConsoleSeed.BatchAsync(rig, "run-b1", null, new SeedTask("t1", "failed", Error: ErrorCode.SessionStalled, Detail: "stalled"));
        await rig.Owner.PostAsync("/v1/halt", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Post(rig.Owner, "/v1/batches/run-b1/tasks/t1/retry")).StatusCode);
    }

    // Re-running a pull request's checks

    private static DrivenServices Services(FakeIntegrationHost host) =>
        new(new DrivenRunTests.FakeEngine(), new DrivenRunTests.FakeEngine(), new DrivenRunTests.FakeVolumes(_ => SessionStatus.Completed), _ => throw new NotSupportedException(), null,
            (_, _, _) => Task.FromResult(new DrivenCheckout("/srv/checkouts/repo", ConsoleRig.Commit, "https://github.com/o/r.git", "main")), new DrivenRunTests.NoEvidenceProblems(), name => $"value-of-{name}", Integration: host);

    private static async Task<(ConsoleRig Rig, FakeIntegrationHost Host)> PullRigAsync(string? actor = null, bool withHost = true)
    {
        var host = new FakeIntegrationHost { Current = FakeIntegrationHost.Snapshot(draft: false, status: "BLOCKED", checks: [new CheckStatus("build", true, CheckOutcome.Failed, 900), new CheckStatus("lint", false, CheckOutcome.Failed, 901), new CheckStatus("test", true, CheckOutcome.Failed, 902)]) };
        var rig = await ConsoleRig.StartAsync(accounts: [Alice(), Bob()], services: withHost ? _ => Services(host) : null);
        await ConsoleSeed.BatchAsync(rig, "run-b1", actor, new SeedTask("t1", PrUrl: "https://example.test/o/r/pull/7"));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("run-b1", "t1", actor: actor, pr: ConsoleSeed.Pull(7)));
        return (rig, host);
    }

    [Fact]
    public async Task The_failed_required_checks_are_run_again_once_and_the_batch_gets_an_event()
    {
        var (rig, host) = await PullRigAsync("alice");
        await using var _ = rig;
        using var alice = rig.As("alice");

        var first = await Post(alice, "/v1/pulls/o/r/7/checks/rerun");
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();
        var second = await Post(alice, "/v1/pulls/o/r/7/checks/rerun");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(["build", "test"], body.GetProperty("checks").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal([900L, 902L], Assert.Single(host.Reruns));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Single(host.Reruns);
        var tell = Assert.Single(rig.Records.ReadEvents("run-b1")!, e => e.Event == RunEventKind.Operator);
        Assert.Equal(("t1", "task t1: failed checks re-run for pull request #7: build, test"), (tell.TaskId, tell.Detail));
    }

    [Fact]
    public async Task A_merged_or_closed_pull_request_and_one_with_nothing_failed_are_a_conflict_and_a_forge_error_is_a_bad_gateway()
    {
        var (rig, host) = await PullRigAsync();
        await using var _ = rig;

        host.Current = host.Current with { Merged = true };
        Assert.Equal(HttpStatusCode.Conflict, (await Post(rig.Owner, "/v1/pulls/o/r/7/checks/rerun")).StatusCode);
        host.Current = host.Current with { Merged = false, Closed = true };
        Assert.Equal(HttpStatusCode.Conflict, (await Post(rig.Owner, "/v1/pulls/o/r/7/checks/rerun")).StatusCode);
        host.Current = host.Current with { Closed = false, Checks = [new CheckStatus("build", true, CheckOutcome.Passed, 900), new CheckStatus("lint", false, CheckOutcome.Failed, 901)] };
        Assert.Equal(HttpStatusCode.Conflict, (await Post(rig.Owner, "/v1/pulls/o/r/7/checks/rerun")).StatusCode);
        host.Current = host.Current with { Checks = [new CheckStatus("build", true, CheckOutcome.Failed, 900)] };
        host.RerunFails = new IntegrationException("GitHub answered 403 to re-running the failed jobs: Resource not accessible by integration");
        var bad = await Post(rig.Owner, "/v1/pulls/o/r/7/checks/rerun");
        Assert.Equal(HttpStatusCode.BadGateway, bad.StatusCode);
        Assert.Contains("403", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(host.Reruns);
    }

    [Fact]
    public async Task A_pull_request_chargehand_did_not_open_or_one_of_another_account_is_not_found_and_no_forge_is_a_conflict()
    {
        var (rig, host) = await PullRigAsync("alice");
        await using var _ = rig;
        using var bob = rig.As("bob");

        Assert.Equal(HttpStatusCode.NotFound, (await Post(bob, "/v1/pulls/o/r/7/checks/rerun")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(rig.Owner, "/v1/pulls/o/r/8/checks/rerun")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(rig.Owner, "/v1/pulls/o/other/7/checks/rerun")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(rig.Owner, "/v1/pulls/o/r/x/checks/rerun")).StatusCode);
        Assert.Empty(host.Calls);
        var (bare, _) = await PullRigAsync(withHost: false);
        await using var __ = bare;
        Assert.Equal(HttpStatusCode.Conflict, (await Post(bare.Owner, "/v1/pulls/o/r/7/checks/rerun")).StatusCode);
    }
}
