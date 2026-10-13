using System.Net;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Ledger.Api;
using Chargehand.RunLog;

namespace Chargehand.Tests;

/// <summary><c>GET /v1/inbox</c> and <c>GET /v1/pulls</c> (CHARGEHAND-209): what needs a person, derived from the run log and the learning records on every call.</summary>
public class ConsoleInboxTests
{
    private static AccountSettings Alice() => new("alice", Driven: true, AllowServerCredential: true);

    private static AccountSettings Bob() => new("bob", Driven: true, AllowServerCredential: true);

    private static Profile AskProfile(string root) => Runs.Profile(root) with
    {
        Driven = new DrivenSettings(Integrator: new DrivenIntegrator(Repositories: new Dictionary<string, string> { ["o/r"] = "ask", ["o/merge"] = "merge" })),
    };

    private static async Task<string[]> Ids(HttpClient http, string path) =>
        [.. (await ConsoleRig.Get(http, path)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!)];

    /// <summary>Four pull requests, a question, two failed tasks of batches and three runs of their own.</summary>
    private static async Task SeedAsync(ConsoleRig rig)
    {
        var at = ConsoleRig.Now;
        await ConsoleSeed.BatchAsync(rig.Log, "b-q", null, at.AddHours(-5), new SeedTask("t1", "needs_input", Goal: "Pick a queue", Questions: ["Which queue?"]));
        // Pull requests: policy ask, test integrity on a repository that would merge, a partial draft, and one that was merged.
        await ConsoleSeed.BatchAsync(rig.Log, "b-ask", null, at.AddHours(-4), new SeedTask("t1", PrUrl: "https://example.test/o/r/pull/1"));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b-ask", "t1", pr: ConsoleSeed.Pull(1), at: at.AddHours(-4)));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b-int", "t1", repository: "o/merge", pr: ConsoleSeed.Pull(2, "merge"), integrityFlag: true, at: at.AddHours(-3)));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b-part", "t1", LearningOutcome.Partial, repository: "o/other", pr: ConsoleSeed.Pull(3, "other"), at: at.AddHours(-2)));
        var merged = ConsoleSeed.Learned("b-done", "t1", repository: "o/r", pr: ConsoleSeed.Pull(4), at: at.AddHours(-2));
        await ConsoleSeed.LearnAsync(rig, merged);
        await ConsoleSeed.OutcomeAsync(rig, merged, PrOutcomes.MergedUnchanged);
        // A merge-policy repository with nothing odd waits for no one.
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b-ok", "t1", repository: "o/merge", pr: ConsoleSeed.Pull(5, "merge"), at: at.AddHours(-2)));
        // A failed task, and a batch that failed only because of it.
        await ConsoleSeed.BatchAsync(rig.Log, "b-fail", null, at.AddHours(-6), new SeedTask("t1", "failed", Goal: "Fix the thing", Error: ErrorCode.SessionFailed, Detail: "the session did not finish"));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b-fail", "t1", LearningOutcome.Failed, at: at.AddHours(-6)));
        // Runs of their own: one failed, one cancelled, one that lost its process.
        var failed = ConsoleSeed.Start("run-failed", Runs.DraftRequest(), null, at.AddHours(-7));
        await rig.Log.AppendAsync(failed, default);
        await rig.Log.AppendAsync(ConsoleSeed.Finish(failed, ConsoleSeed.Result("run-failed", ResultStatus.Failed, "over budget", error: new ResultError(ErrorCode.CostCapReached, "over budget", false, "raise it")), at.AddHours(-7)), default);
        var cancelled = ConsoleSeed.Start("run-cancelled", Runs.DraftRequest(), null, at.AddHours(-8));
        await rig.Log.AppendAsync(cancelled, default);
        await rig.Log.AppendAsync(ConsoleSeed.Finish(cancelled, ConsoleSeed.Result("run-cancelled", ResultStatus.Failed, "cancelled", error: new ResultError(ErrorCode.Cancelled, "cancelled", false, null)), at.AddHours(-8)), default);
        await rig.Log.AppendAsync(new StartRecord("run-lost", at.AddHours(-9), new string('0', 32), Runs.DraftRequest(), int.MaxValue), default);
    }

    [Fact]
    public async Task The_inbox_lists_questions_pull_requests_for_a_person_and_failures_newest_first_with_counts()
    {
        await using var rig = await ConsoleRig.StartAsync(profile: AskProfile);
        await SeedAsync(rig);

        var body = await ConsoleRig.Get(rig.Owner, "/v1/inbox");

        Assert.Equal("inbox/v1", body.GetProperty("contract_version").GetString());
        var counts = body.GetProperty("counts");
        Assert.Equal((1, 3, 3, 7), (counts.GetProperty("questions").GetInt32(), counts.GetProperty("pull_requests").GetInt32(), counts.GetProperty("failures").GetInt32(), counts.GetProperty("total").GetInt32()));
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(
            ["pull_request:https://example.test/o/other/pull/3", "pull_request:https://example.test/o/merge/pull/2", "pull_request:https://example.test/o/r/pull/1", "question:b-q:t1", "failure:b-fail:t1", "failure:run-failed", "failure:run-lost"],
            items.Select(i => i.GetProperty("id").GetString()));
        Assert.Equal(items.Select(i => i.GetProperty("at").GetDateTimeOffset()).OrderByDescending(a => a), items.Select(i => i.GetProperty("at").GetDateTimeOffset()));

        var byId = items.ToDictionary(i => i.GetProperty("id").GetString()!);
        Assert.Equal(("partial_draft", "test_integrity", "policy_ask"),
            (byId["pull_request:https://example.test/o/other/pull/3"].GetProperty("reason").GetString(), byId["pull_request:https://example.test/o/merge/pull/2"].GetProperty("reason").GetString(),
                byId["pull_request:https://example.test/o/r/pull/1"].GetProperty("reason").GetString()));
        Assert.Equal("o/r", byId["pull_request:https://example.test/o/r/pull/1"].GetProperty("pull_request").GetProperty("repository").GetString());

        var question = byId["question:b-q:t1"];
        Assert.Equal(["Which queue?"], question.GetProperty("questions").EnumerateArray().Select(q => q.GetString()));
        Assert.Equal("Pick a queue", question.GetProperty("title").GetString());
        Assert.Equal(("b-q", "t1"), (question.GetProperty("answer").GetProperty("continue_from").GetProperty("run_id").GetString(), question.GetProperty("answer").GetProperty("continue_from").GetProperty("task_id").GetString()));
        Assert.Equal("b-q-t1", question.GetProperty("run_id").GetString());

        Assert.Equal(("task", "session_failed"), (byId["failure:b-fail:t1"].GetProperty("subject").GetString(), byId["failure:b-fail:t1"].GetProperty("error").GetProperty("code").GetString()));
        Assert.Equal(("run", "cost_cap_reached", "raise it"), (byId["failure:run-failed"].GetProperty("subject").GetString(), byId["failure:run-failed"].GetProperty("reason").GetString(),
            byId["failure:run-failed"].GetProperty("error").GetProperty("action").GetString()));
        Assert.Equal("lost", byId["failure:run-lost"].GetProperty("reason").GetString());
        Assert.DoesNotContain("failure:run-cancelled", byId.Keys);
        Assert.DoesNotContain("failure:b-fail", byId.Keys);
    }

    [Fact]
    public async Task A_question_leaves_the_inbox_when_a_batch_continues_it()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await ConsoleSeed.BatchAsync(rig.Log, "b-q", null, ConsoleRig.Now.AddHours(-5), new SeedTask("t1", "needs_input", Questions: ["Which queue?"]));
        Assert.Equal(["question:b-q:t1"], await Ids(rig.Owner, "/v1/inbox"));

        await ConsoleSeed.BatchAsync(rig.Log, "b-a", null, ConsoleRig.Now.AddHours(-1), new SeedTask("t1", Asked: new DrivenTask("t1", ContinueFrom: new ContinueFrom("b-q", "t1"), Answers: ["A"])));

        Assert.Empty(await Ids(rig.Owner, "/v1/inbox"));
    }

    [Fact]
    public async Task The_inbox_window_limit_and_bad_values()
    {
        await using var rig = await ConsoleRig.StartAsync(profile: AskProfile);
        await SeedAsync(rig);

        Assert.Equal(2, (await Ids(rig.Owner, "/v1/inbox?limit=2")).Length);
        Assert.Equal(7, (await ConsoleRig.Get(rig.Owner, "/v1/inbox?limit=2")).GetProperty("counts").GetProperty("total").GetInt32());
        // Only what happened in the last 150 minutes: the pull request of two hours ago and the one three hours back are out.
        Assert.Equal(["pull_request:https://example.test/o/other/pull/3"], await Ids(rig.Owner, $"/v1/inbox?since={Uri.EscapeDataString(ConsoleRig.Now.AddMinutes(-150).ToString("o", System.Globalization.CultureInfo.InvariantCulture))}"));
        Assert.Empty(await Ids(rig.Owner, "/v1/inbox?since=0d"));
        Assert.Equal(HttpStatusCode.BadRequest, await ConsoleRig.Status(rig.Owner, "/v1/inbox?since=yesterday"));
        Assert.Equal(HttpStatusCode.BadRequest, await ConsoleRig.Status(rig.Owner, "/v1/inbox?limit=0"));
        Assert.Equal(HttpStatusCode.BadRequest, await ConsoleRig.Status(rig.Owner, "/v1/inbox?limit=501"));
    }

    [Fact]
    public async Task An_open_pull_request_older_than_the_read_back_window_no_longer_waits_for_a_person()
    {
        await using var rig = await ConsoleRig.StartAsync(profile: AskProfile);
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b-old", "t1", pr: ConsoleSeed.Pull(9), at: ConsoleRig.Now.AddDays(-20)));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b-new", "t1", pr: ConsoleSeed.Pull(10), at: ConsoleRig.Now.AddDays(-10)));

        Assert.Equal(["pull_request:https://example.test/o/r/pull/10"], await Ids(rig.Owner, "/v1/inbox?since=60d"));
        var pulls = (await ConsoleRig.Get(rig.Owner, "/v1/pulls?awaiting=true")).GetProperty("pulls").EnumerateArray().Select(p => p.GetProperty("number").GetInt32());
        Assert.Equal([10], pulls);
    }

    [Fact]
    public async Task An_account_gets_its_own_inbox_and_pull_requests_and_nothing_of_the_owners_or_another_accounts()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice(), Bob()], profile: AskProfile);
        var at = ConsoleRig.Now;
        await ConsoleSeed.BatchAsync(rig.Log, "a-q", "alice", at.AddHours(-3), new SeedTask("t1", "needs_input", Questions: ["Alice?"]));
        await ConsoleSeed.BatchAsync(rig.Log, "b-q", "bob", at.AddHours(-3), new SeedTask("t1", "needs_input", Questions: ["Bob?"]));
        await ConsoleSeed.BatchAsync(rig.Log, "o-q", null, at.AddHours(-3), new SeedTask("t1", "needs_input", Questions: ["Owner?"]));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("a-pr", "t1", actor: "alice", pr: ConsoleSeed.Pull(21)));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b-pr", "t1", actor: "bob", pr: ConsoleSeed.Pull(22)));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("o-pr", "t1", pr: ConsoleSeed.Pull(23)));
        var lost = new StartRecord("bob-lost", at.AddHours(-2), new string('0', 32), Runs.DraftRequest(), int.MaxValue, Actor: "bob");
        await rig.Log.AppendAsync(lost, default);
        using var alice = rig.As("alice");

        Assert.Equal(["pull_request:https://example.test/o/r/pull/21", "question:a-q:t1"], await Ids(alice, "/v1/inbox"));
        string[] everything = ["pull_request:https://example.test/o/r/pull/21", "question:b-q:t1", "pull_request:https://example.test/o/r/pull/22", "question:o-q:t1", "question:a-q:t1",
            "pull_request:https://example.test/o/r/pull/23", "failure:bob-lost"];
        Assert.Equal(everything.Order(), (await Ids(rig.Owner, "/v1/inbox")).Order());
        Assert.Equal([21], (await ConsoleRig.Get(alice, "/v1/pulls")).GetProperty("pulls").EnumerateArray().Select(p => p.GetProperty("number").GetInt32()));
        Assert.Equal(3, (await ConsoleRig.Get(rig.Owner, "/v1/pulls")).GetProperty("pulls").GetArrayLength());
        var text = await ConsoleRig.Text(alice, "/v1/inbox");
        Assert.DoesNotContain("Bob?", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Owner?", text, StringComparison.Ordinal);
        Assert.DoesNotContain("bob-lost", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pulls_filter_by_repository_state_and_awaiting_and_reject_bad_values()
    {
        await using var rig = await ConsoleRig.StartAsync(profile: AskProfile);
        await SeedAsync(rig);

        List<int> Numbers(JsonElement body) => [.. body.GetProperty("pulls").EnumerateArray().Select(p => p.GetProperty("number").GetInt32())];
        Assert.Equal("pulls/v1", (await ConsoleRig.Get(rig.Owner, "/v1/pulls")).GetProperty("contract_version").GetString());
        Assert.Equal([5, 4, 3, 2, 1], Numbers(await ConsoleRig.Get(rig.Owner, "/v1/pulls")).Order().Reverse().ToList());
        Assert.Equal([3, 2, 1], Numbers(await ConsoleRig.Get(rig.Owner, "/v1/pulls?awaiting=true")));
        Assert.Equal([4], Numbers(await ConsoleRig.Get(rig.Owner, "/v1/pulls?state=merged_unchanged")));
        Assert.Equal([4, 1], Numbers(await ConsoleRig.Get(rig.Owner, "/v1/pulls?repository=o/r")).Order().Reverse().ToList());
        Assert.Equal([2], Numbers(await ConsoleRig.Get(rig.Owner, "/v1/pulls?repository=o/merge&awaiting=true")));
        Assert.Single((await ConsoleRig.Get(rig.Owner, "/v1/pulls?limit=1")).GetProperty("pulls").EnumerateArray());
        var merged = (await ConsoleRig.Get(rig.Owner, "/v1/pulls?state=merged_unchanged")).GetProperty("pulls")[0];
        Assert.Equal(("merged_unchanged", false), (merged.GetProperty("state").GetString(), merged.GetProperty("awaiting_person").GetBoolean()));
        Assert.Equal(HttpStatusCode.BadRequest, await ConsoleRig.Status(rig.Owner, "/v1/pulls?state=lost"));
        Assert.Equal(HttpStatusCode.BadRequest, await ConsoleRig.Status(rig.Owner, "/v1/pulls?awaiting=maybe"));
        Assert.Equal(HttpStatusCode.BadRequest, await ConsoleRig.Status(rig.Owner, "/v1/pulls?limit=0"));
        Assert.Equal(HttpStatusCode.BadRequest, await ConsoleRig.Status(rig.Owner, "/v1/pulls?since=never"));
    }
}
