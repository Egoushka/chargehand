using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>ADR 0064: what leaves the inbox without anyone dealing with it there (superseded, infrastructure, continued), identical failures folded into one, and the dismissals a person stores.</summary>
public class ConsoleInboxRulesTests
{
    private static readonly DateTimeOffset Now = ConsoleRig.Now;

    private static AccountSettings Alice() => new("alice", Driven: true, AllowServerCredential: true);

    private static AccountSettings Bob() => new("bob", Driven: true, AllowServerCredential: true);

    private static async Task<string[]> Ids(HttpClient http) =>
        [.. (await ConsoleRig.Get(http, "/v1/inbox")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!)];

    private static async Task<(int Superseded, int Infrastructure, int Continued, int Dismissed, int Duplicates)> Resolved(HttpClient http)
    {
        var r = (await ConsoleRig.Get(http, "/v1/inbox")).GetProperty("resolved");
        return (r.GetProperty("superseded").GetInt32(), r.GetProperty("infrastructure").GetInt32(), r.GetProperty("continued").GetInt32(), r.GetProperty("dismissed").GetInt32(),
            r.GetProperty("duplicates").GetInt32());
    }

    /// <summary>A run of its own on <c>/srv/repo</c>, finished with <paramref name="status"/>.</summary>
    private static async Task RunAsync(ConsoleRig rig, string id, string text, DateTimeOffset at, ResultStatus status = ResultStatus.Completed, ErrorCode? error = null, string? actor = null)
    {
        var request = new RunRequest("request/v1", text, new RequestContext(false, "cheap", Repository: new RepositoryRef("/srv/repo", ConsoleRig.Commit)));
        var start = ConsoleSeed.Start(id, request, actor, at);
        await rig.Log.AppendAsync(start, default);
        await rig.Log.AppendAsync(ConsoleSeed.Finish(start, ConsoleSeed.Result(id, status, "done", error: error is { } code ? new ResultError(code, "it failed", false, null) : null, actor: actor), at.AddMinutes(5)), default);
    }

    private static async Task FailedTaskAsync(ConsoleRig rig, string batch, DateTimeOffset at, string goal = "Fix the thing", ErrorCode error = ErrorCode.SessionFailed, string? actor = null)
    {
        await ConsoleSeed.BatchAsync(rig.Log, batch, actor, at, new SeedTask("t1", "failed", Goal: goal, Error: error));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned(batch, "t1", LearningOutcome.Failed, actor, at: at.AddMinutes(30)));
    }

    private static Task<HttpResponseMessage> Post(HttpClient http, string path, params string[] ids) => http.PostAsJsonAsync(path, new { ids });

    [Fact]
    public async Task A_question_is_superseded_by_a_later_batch_that_asks_its_task_the_same_and_not_by_one_that_asks_something_else()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await ConsoleSeed.BatchAsync(rig.Log, "b-old", null, Now.AddHours(-6), new SeedTask("t1", "needs_input", Goal: "Pick a queue", Questions: ["Which queue?"]));
        await ConsoleSeed.BatchAsync(rig.Log, "b-other", null, Now.AddHours(-4), new SeedTask("t1", Goal: "Something else"));
        Assert.Equal(["question:b-old:t1"], await Ids(rig.Owner));

        await ConsoleSeed.BatchAsync(rig.Log, "b-new", null, Now.AddHours(-2), new SeedTask("t1", "needs_input", Goal: "Pick a queue", Questions: ["Which queue now?"]));

        Assert.Equal(["question:b-new:t1"], await Ids(rig.Owner));
        Assert.Equal((1, 0, 0, 0, 0), await Resolved(rig.Owner));
    }

    [Fact]
    public async Task A_question_that_a_batch_answered_or_retried_counts_as_continued()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await ConsoleSeed.BatchAsync(rig.Log, "b-q", null, Now.AddHours(-6), new SeedTask("t1", "needs_input", Questions: ["Which queue?"]));
        await ConsoleSeed.BatchAsync(rig.Log, "b-r", null, Now.AddHours(-6), new SeedTask("t2", "needs_input", Questions: ["Which port?"]));
        await ConsoleSeed.BatchAsync(rig.Log, "b-a", null, Now.AddHours(-1), new SeedTask("t1", Asked: new DrivenTask("t1", ContinueFrom: new ContinueFrom("b-q", "t1"), Answers: ["A"])));
        var retry = ConsoleSeed.Start("b-retry", ConsoleSeed.BatchRequest(new DrivenTask("t2", Goal: "goal of t2")) with { Text = "Retry of b-r/t2." }, null, Now.AddHours(-1));
        await rig.Log.AppendAsync(retry, default);

        Assert.Empty(await Ids(rig.Owner));
        Assert.Equal((0, 0, 2, 0, 0), await Resolved(rig.Owner));
    }

    [Fact]
    public async Task A_failed_task_is_superseded_only_by_a_later_task_with_its_id_and_goal_that_completed_and_another_accounts_batch_does_not_count()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice()]);
        await FailedTaskAsync(rig, "b-fail", Now.AddHours(-8));
        await ConsoleSeed.BatchAsync(rig.Log, "b-alice", "alice", Now.AddHours(-6), new SeedTask("t1", Goal: "Fix the thing"));
        await ConsoleSeed.BatchAsync(rig.Log, "b-elsewhere", null, Now.AddHours(-5), new SeedTask("t1", Goal: "Fix another thing"));
        Assert.Equal(["failure:b-fail:t1"], await Ids(rig.Owner));

        await ConsoleSeed.BatchAsync(rig.Log, "b-fixed", null, Now.AddHours(-2), new SeedTask("t1", Goal: "Fix the thing", PrUrl: "https://example.test/o/r/pull/3"));

        Assert.Empty(await Ids(rig.Owner));
        Assert.Equal((1, 0, 0, 0, 0), await Resolved(rig.Owner));
    }

    [Fact]
    public async Task A_failed_run_is_superseded_by_a_later_run_with_the_same_text_on_the_same_repository_that_completed()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await RunAsync(rig, "run-1", "Explain the cache", Now.AddHours(-6), ResultStatus.Failed, ErrorCode.CostCapReached);
        await RunAsync(rig, "run-2", "Explain the queue", Now.AddHours(-5));
        Assert.Equal(["failure:run-1"], await Ids(rig.Owner));

        await RunAsync(rig, "run-3", "Explain the cache", Now.AddHours(-4));

        Assert.Empty(await Ids(rig.Owner));
        Assert.Equal((1, 0, 0, 0, 0), await Resolved(rig.Owner));
    }

    [Fact]
    public async Task A_lost_batch_is_superseded_by_a_later_batch_in_which_each_of_its_tasks_did_its_work()
    {
        await using var rig = await ConsoleRig.StartAsync();
        var request = ConsoleSeed.BatchRequest(new DrivenTask("t1", Goal: "goal of t1"), new DrivenTask("t2", Goal: "goal of t2")) with { Text = "Two things" };
        await rig.Log.AppendAsync(new StartRecord("b-lost", Now.AddHours(-9), new string('0', 32), request, int.MaxValue), default);
        // The same tasks, neither of which did its work: no proof (and lost is an infrastructure code, so nothing on /srv/repo may finish ok before the batch that does).
        await ConsoleSeed.BatchAsync(rig.Log, "b-half", null, Now.AddHours(-8), new SeedTask("t1", "failed", Error: ErrorCode.SessionFailed), new SeedTask("t2", "failed", Error: ErrorCode.SessionFailed));
        Assert.Contains("failure:b-lost", await Ids(rig.Owner));

        await ConsoleSeed.BatchAsync(rig.Log, "b-again", null, Now.AddHours(-3), new SeedTask("t1"), new SeedTask("t2", PrUrl: "https://example.test/o/r/pull/4"));

        Assert.DoesNotContain("failure:b-lost", await Ids(rig.Owner));
        Assert.Equal(1, (await Resolved(rig.Owner)).Superseded);
    }

    [Fact]
    public async Task An_infrastructure_failure_is_resolved_by_any_later_run_on_the_repository_that_finished_ok_and_a_task_failure_is_not()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await RunAsync(rig, "run-checkout", "Probe the checkout", Now.AddHours(-7), ResultStatus.Failed, ErrorCode.CheckoutInvalid);
        await RunAsync(rig, "run-internal", "Probe again", Now.AddHours(-7), ResultStatus.Failed, ErrorCode.Internal);
        await RunAsync(rig, "run-secrets", "Read the repo", Now.AddHours(-7), ResultStatus.Failed, ErrorCode.CheckoutHasSecrets);
        await FailedTaskAsync(rig, "b-verify", Now.AddHours(-7), "Make the tests pass", ErrorCode.VerificationFailed);
        // Before the failures: proves nothing.
        await RunAsync(rig, "run-earlier", "Something that worked", Now.AddHours(-8));
        Assert.Equal(["failure:b-verify:t1", "failure:run-checkout", "failure:run-internal", "failure:run-secrets"], (await Ids(rig.Owner)).Order());

        await ConsoleSeed.BatchAsync(rig.Log, "b-ok", null, Now.AddHours(-2), new SeedTask("t9", Goal: "Unrelated work"));

        Assert.Equal(["failure:b-verify:t1", "failure:run-secrets"], (await Ids(rig.Owner)).Order());
        Assert.Equal((0, 2, 0, 0, 0), await Resolved(rig.Owner));
    }

    [Fact]
    public async Task Identical_failures_fold_into_the_newest_with_a_count_and_the_counts_are_of_what_is_left()
    {
        await using var rig = await ConsoleRig.StartAsync();
        for (var n = 1; n <= 3; n++)
            await RunAsync(rig, $"run-{n}", "Explain the cache", Now.AddHours(-10 + n), ResultStatus.Failed, ErrorCode.CostCapReached);
        await RunAsync(rig, "run-other", "Explain the queue", Now.AddHours(-9), ResultStatus.Failed, ErrorCode.CostCapReached);

        var body = await ConsoleRig.Get(rig.Owner, "/v1/inbox");

        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["failure:run-3", "failure:run-other"], items.Select(i => i.GetProperty("id").GetString()));
        Assert.Equal((3, 1), (items[0].GetProperty("count").GetInt32(), items[1].GetProperty("count").GetInt32()));
        Assert.Equal(Now.AddHours(-7).AddMinutes(5), items[0].GetProperty("at").GetDateTimeOffset());
        Assert.Equal((2, 2), (body.GetProperty("counts").GetProperty("failures").GetInt32(), body.GetProperty("counts").GetProperty("total").GetInt32()));
        Assert.Equal(2, body.GetProperty("resolved").GetProperty("duplicates").GetInt32());
    }

    [Fact]
    public async Task Dismiss_hides_an_item_and_every_failure_it_stands_for_survives_a_restart_of_the_store_and_restore_brings_them_back()
    {
        await using var rig = await ConsoleRig.StartAsync();
        for (var n = 1; n <= 2; n++)
            await RunAsync(rig, $"run-{n}", "Explain the cache", Now.AddHours(-10 + n), ResultStatus.Failed, ErrorCode.CostCapReached);
        await ConsoleSeed.BatchAsync(rig.Log, "b-q", null, Now.AddHours(-5), new SeedTask("t1", "needs_input", Questions: ["Which queue?"]));

        var res = await Post(rig.Owner, "/v1/inbox/dismiss", "failure:run-2", "question:nope");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("dismiss", true), (body.GetProperty("action").GetString(), body.GetProperty("changed").GetBoolean()));
        Assert.Equal(["question:nope"], body.GetProperty("unknown").EnumerateArray().Select(i => i.GetString()));
        Assert.Equal(["question:b-q:t1"], await Ids(rig.Owner));
        Assert.Equal((0, 0, 0, 2, 0), await Resolved(rig.Owner));
        Assert.Equal(["failure:run-1", "failure:run-2"], new InboxDismissals(Path.Combine(rig.Root, "inbox", "dismissed.json")).Read().Keys.Order());

        var again = await (await Post(rig.Owner, "/v1/inbox/dismiss", "failure:run-2")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(again.GetProperty("changed").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await Post(rig.Owner, "/v1/inbox/dismiss", "failure:missing")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Post(rig.Owner, "/v1/inbox/restore", "failure:run-2")).StatusCode);
        Assert.Equal(["question:b-q:t1", "failure:run-2"], await Ids(rig.Owner));
        Assert.Equal(2, (await ConsoleRig.Get(rig.Owner, "/v1/inbox")).GetProperty("items")[1].GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task An_account_dismisses_its_own_items_only_and_a_run_token_and_no_key_reach_neither_route()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice(), Bob()]);
        await RunAsync(rig, "run-alice", "Alice's", Now.AddHours(-3), ResultStatus.Failed, ErrorCode.CostCapReached, "alice");
        await RunAsync(rig, "run-bob", "Bob's", Now.AddHours(-3), ResultStatus.Failed, ErrorCode.CostCapReached, "bob");
        using var alice = rig.As("alice");
        var repo = Runs.GitRepo(rig.Server.WorkerRoot);
        using var session = rig.Server.As(rig.Server.Tokens.Issue(new RunTokenClaims("run-task", repo.Path, repo.Commit, null, null, DateTimeOffset.UtcNow.AddHours(1), "alice"), DateTimeOffset.UtcNow));
        using var anonymous = new HttpClient { BaseAddress = rig.Server.BaseAddress };

        Assert.Equal(HttpStatusCode.NotFound, (await Post(alice, "/v1/inbox/dismiss", "failure:run-bob")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(session, "/v1/inbox/dismiss", "failure:run-alice")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(anonymous, "/v1/inbox/dismiss", "failure:run-alice")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(alice, "/v1/inbox/dismiss", "failure:run-alice")).StatusCode);

        Assert.Empty(await Ids(alice));
        Assert.Equal(["failure:run-bob"], await Ids(rig.Owner));
        Assert.Equal("alice", new InboxDismissals(Path.Combine(rig.Root, "inbox", "dismissed.json")).Read()["failure:run-alice"].By);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(rig.As("bob"), "/v1/inbox/restore", "failure:run-alice")).StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ids\": []}")]
    [InlineData("{\"ids\": [\"\"]}")]
    [InlineData("{\"ids\": [1]}")]
    [InlineData("not json")]
    public async Task A_bad_body_is_a_bad_request(string body)
    {
        await using var rig = await ConsoleRig.StartAsync();

        var res = await rig.Owner.PostAsync("/v1/inbox/dismiss", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task A_server_without_a_file_for_dismissals_refuses_them()
    {
        await using var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));

        var res = await Post(server.Http, "/v1/inbox/dismiss", "failure:x");

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal(0, (await ConsoleRig.Get(server.Http, "/v1/inbox")).GetProperty("resolved").GetProperty("dismissed").GetInt32());
    }
}
