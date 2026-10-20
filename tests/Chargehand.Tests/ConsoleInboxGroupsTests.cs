using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Tests;

/// <summary>CHARGEHAND-237: failures with the same repository and code shown as one group, a group dismissed in one action, and the resolve rules ADR 0064 gained (a closed pull request, the same
/// pull request head, the same task run again).</summary>
public class ConsoleInboxGroupsTests
{
    private static readonly DateTimeOffset Now = ConsoleRig.Now;
    private static readonly string HeadA = new('a', 40);
    private static readonly string HeadB = new('b', 40);

    private static AccountSettings Alice() => new("alice", Driven: true, AllowServerCredential: true);

    private static async Task<string[]> Ids(HttpClient http) =>
        [.. (await ConsoleRig.Get(http, "/v1/inbox")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!)];

    private static Task<HttpResponseMessage> Post(HttpClient http, string path, params string[] ids) => http.PostAsJsonAsync(path, new { ids });

    private static async Task RunAsync(ConsoleRig rig, string id, string text, DateTimeOffset at, ResultStatus status = ResultStatus.Completed, ErrorCode? error = null, string? actor = null,
        string? commit = null)
    {
        var request = new RunRequest("request/v1", text, new RequestContext(false, "review", Repository: new RepositoryRef("/srv/repo", commit ?? ConsoleRig.Commit)));
        var start = ConsoleSeed.Start(id, request, actor, at);
        await rig.Log.AppendAsync(start, default);
        await rig.Log.AppendAsync(ConsoleSeed.Finish(start, ConsoleSeed.Result(id, status, "done", error: error is { } code ? new ResultError(code, "it failed", false, null) : null, actor: actor), at.AddMinutes(5)), default);
    }

    [Fact]
    public async Task Failures_with_the_same_repository_and_code_form_one_group_newest_first_and_every_item_stays_listed()
    {
        await using var rig = await ConsoleRig.StartAsync();
        for (var n = 1; n <= 3; n++)
            await RunAsync(rig, $"run-{n}", $"Review pull request o/r#{n}.", Now.AddHours(-10 + n), ResultStatus.Failed, ErrorCode.CheckoutHasSecrets);
        // Identical to run-3: folded into it, so the group counts it but lists one item.
        await RunAsync(rig, "run-3b", "Review pull request o/r#3.", Now.AddHours(-6.5), ResultStatus.Failed, ErrorCode.CheckoutHasSecrets);
        await RunAsync(rig, "run-cap", "Explain the cache", Now.AddHours(-9), ResultStatus.Failed, ErrorCode.CostCapReached);

        var body = await ConsoleRig.Get(rig.Owner, "/v1/inbox");

        Assert.Equal(4, body.GetProperty("items").GetArrayLength());
        var group = Assert.Single(body.GetProperty("groups").EnumerateArray());
        Assert.Equal("group:checkout_has_secrets:failure:run-3b", group.GetProperty("id").GetString());
        Assert.Equal(("failure", "/srv/repo", "checkout_has_secrets"), (group.GetProperty("kind").GetString(), group.GetProperty("repository").GetString(), group.GetProperty("reason").GetString()));
        Assert.Equal(4, group.GetProperty("count").GetInt32());
        Assert.Equal(["failure:run-3b", "failure:run-2", "failure:run-1"], group.GetProperty("items").EnumerateArray().Select(i => i.GetString()));
        Assert.Equal(Now.AddHours(-6.5).AddMinutes(5), group.GetProperty("at").GetDateTimeOffset());
    }

    [Fact]
    public async Task A_group_is_dismissed_in_one_action_safely_twice_and_restored_in_one()
    {
        await using var rig = await ConsoleRig.StartAsync();
        for (var n = 1; n <= 3; n++)
            await RunAsync(rig, $"run-{n}", $"Review pull request o/r#{n}.", Now.AddHours(-10 + n), ResultStatus.Failed, ErrorCode.CheckoutHasSecrets);
        await RunAsync(rig, "run-cap", "Explain the cache", Now.AddHours(-9), ResultStatus.Failed, ErrorCode.CostCapReached);
        const string Group = "group:checkout_has_secrets:failure:run-3";

        var res = await Post(rig.Owner, "/v1/inbox/dismiss", Group);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal([Group], (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ids").EnumerateArray().Select(i => i.GetString()));
        Assert.Equal(["failure:run-cap"], await Ids(rig.Owner));
        Assert.Equal(3, (await ConsoleRig.Get(rig.Owner, "/v1/inbox")).GetProperty("resolved").GetProperty("dismissed").GetInt32());
        Assert.Empty((await ConsoleRig.Get(rig.Owner, "/v1/inbox")).GetProperty("groups").EnumerateArray());
        var again = await (await Post(rig.Owner, "/v1/inbox/dismiss", Group)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal([Group], again.GetProperty("unchanged").EnumerateArray().Select(i => i.GetString()));

        Assert.Equal(HttpStatusCode.OK, (await Post(rig.Owner, "/v1/inbox/restore", Group)).StatusCode);

        Assert.Equal(["failure:run-3", "failure:run-2", "failure:run-1", "failure:run-cap"], await Ids(rig.Owner));
        Assert.Equal(HttpStatusCode.NotFound, (await Post(rig.Owner, "/v1/inbox/restore", "group:checkout_has_secrets:failure:nope")).StatusCode);
    }

    [Fact]
    public async Task A_failed_task_resolves_when_its_pull_request_is_closed_and_a_review_when_the_pull_request_it_names_is_merged()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await ConsoleSeed.BatchAsync(rig.Log, "b-fail", null, Now.AddHours(-8), new SeedTask("t1", "failed", Goal: "Fix it", Error: ErrorCode.VerificationFailed));
        var failed = ConsoleSeed.Learned("b-fail", "t1", LearningOutcome.Failed, at: Now.AddHours(-7), pr: ConsoleSeed.Pull(5));
        await ConsoleSeed.LearnAsync(rig, failed);
        await RunAsync(rig, "run-review", "Review pull request o/r#7.", Now.AddHours(-6), ResultStatus.Failed, ErrorCode.CheckoutHasSecrets);
        Assert.Equal(["failure:run-review", "failure:b-fail:t1"], await Ids(rig.Owner));

        await ConsoleSeed.OutcomeAsync(rig, failed, PrOutcomes.Closed);
        var opened = ConsoleSeed.Learned("b-pr", "t1", at: Now.AddHours(-9), pr: ConsoleSeed.Pull(7));
        await ConsoleSeed.LearnAsync(rig, opened);
        await ConsoleSeed.OutcomeAsync(rig, opened, PrOutcomes.Merged);

        Assert.Empty(await Ids(rig.Owner));
        Assert.Equal(2, (await ConsoleRig.Get(rig.Owner, "/v1/inbox")).GetProperty("resolved").GetProperty("pr_closed").GetInt32());
    }

    [Fact]
    public async Task A_failed_review_is_superseded_by_a_later_review_of_the_same_head_by_any_account_and_not_by_one_of_another_head()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice()]);
        await RunAsync(rig, "run-fail", "Review pull request o/r#7.", Now.AddHours(-6), ResultStatus.Failed, ErrorCode.CheckoutHasSecrets, commit: HeadA);
        await RunAsync(rig, "run-other-head", "Review pull request o/r#7.", Now.AddHours(-5), actor: "alice", commit: HeadB);
        await RunAsync(rig, "run-other-pull", "Review pull request o/r#8.", Now.AddHours(-5), actor: "alice", commit: HeadA);
        Assert.Equal(["failure:run-fail"], await Ids(rig.Owner));

        await RunAsync(rig, "run-ok", "Review pull request o/r#7.", Now.AddHours(-4), actor: "alice", commit: HeadA);

        Assert.Empty(await Ids(rig.Owner));
        Assert.Equal(1, (await ConsoleRig.Get(rig.Owner, "/v1/inbox")).GetProperty("resolved").GetProperty("superseded").GetInt32());
    }

    [Fact]
    public async Task A_failed_task_is_superseded_when_another_account_retried_or_continued_it_and_it_did_its_work()
    {
        await using var rig = await ConsoleRig.StartAsync(accounts: [Alice()]);
        await ConsoleSeed.BatchAsync(rig.Log, "b-one", null, Now.AddHours(-8), new SeedTask("t1", "failed", Goal: "Fix it", Error: ErrorCode.SessionFailed));
        await ConsoleSeed.BatchAsync(rig.Log, "b-two", null, Now.AddHours(-8), new SeedTask("t1", "failed", Goal: "Fix that", Error: ErrorCode.SessionFailed));
        foreach (var batch in new[] { "b-one", "b-two" })
            await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned(batch, "t1", LearningOutcome.Failed, at: Now.AddHours(-7)));
        // A retry that failed again proves nothing.
        await rig.Log.AppendAsync(ConsoleSeed.Start("b-retry-failed", ConsoleSeed.BatchRequest(new DrivenTask("t1", Goal: "Fix that")) with { Text = "Retry of b-two/t1." }, "alice", Now.AddHours(-6)), default);
        Assert.Equal(["failure:b-one:t1", "failure:b-two:t1"], (await Ids(rig.Owner)).Order());

        await rig.Log.AppendAsync(ConsoleSeed.Start("b-retry", ConsoleSeed.BatchRequest(new DrivenTask("t1", Goal: "Fix it")) with { Text = "Retry of b-one/t1." }, "alice", Now.AddHours(-4)), default);
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b-retry", "t1", actor: "alice", at: Now.AddHours(-3)));
        await ConsoleSeed.BatchAsync(rig.Log, "b-cont", "alice", Now.AddHours(-4), new SeedTask("t9", Asked: new DrivenTask("t9", ContinueFrom: new ContinueFrom("b-two", "t1"))));

        Assert.Empty(await Ids(rig.Owner));
        Assert.Equal(2, (await ConsoleRig.Get(rig.Owner, "/v1/inbox")).GetProperty("resolved").GetProperty("superseded").GetInt32());
    }
}
