using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0085: every item is in a lane (Decide, Fix, Handled), the badge counts Decide, and an item a rule or a person resolved is listed in <c>handled</c> with Undo.</summary>
public class ConsoleInboxLanesTests
{
    private static readonly DateTimeOffset Now = ConsoleRig.Now;

    private static async Task RunAsync(ConsoleRig rig, string id, string text, DateTimeOffset at, ResultStatus status = ResultStatus.Completed, ErrorCode? error = null)
    {
        var request = new RunRequest("request/v1", text, new RequestContext(false, "cheap", Repository: new RepositoryRef("/srv/repo", ConsoleRig.Commit)));
        var start = ConsoleSeed.Start(id, request, null, at);
        await rig.Log.AppendAsync(start, default);
        await rig.Log.AppendAsync(ConsoleSeed.Finish(start, ConsoleSeed.Result(id, status, "done", error: error is { } code ? new ResultError(code, "it failed", false, null) : null), at.AddMinutes(5)), default);
    }

    private static Task<HttpResponseMessage> Post(HttpClient http, string path, params string[] ids) => http.PostAsJsonAsync(path, new { ids });

    private static List<string> Ids(JsonElement body, string list) => [.. body.GetProperty(list).EnumerateArray().Select(i => i.GetProperty("id").GetString()!)];

    [Fact]
    public async Task Questions_are_decide_failures_are_fix_and_the_decide_count_leaves_failures_out()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await ConsoleSeed.BatchAsync(rig.Log, "b-q", null, Now.AddHours(-5), new SeedTask("t1", "needs_input", Questions: ["Which queue?"]));
        await ConsoleSeed.BatchAsync(rig.Log, "b-fail", null, Now.AddHours(-6), new SeedTask("t1", "failed", Goal: "Fix it", Error: ErrorCode.SessionFailed));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b-fail", "t1", LearningOutcome.Failed, at: Now.AddHours(-6)));

        var body = await ConsoleRig.Get(rig.Owner, "/v1/inbox");

        var lanes = body.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetString()!, i => i.GetProperty("lane").GetString());
        Assert.Equal("decide", lanes["question:b-q:t1"]);
        Assert.Equal("fix", lanes["failure:b-fail:t1"]);
        Assert.Equal((1, 2), (body.GetProperty("counts").GetProperty("decide").GetInt32(), body.GetProperty("counts").GetProperty("total").GetInt32()));
    }

    [Fact]
    public async Task What_a_rule_resolved_and_what_a_person_dismissed_is_listed_as_handled_with_its_rule_and_undo()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await RunAsync(rig, "run-checkout", "Probe the checkout", Now.AddHours(-7), ResultStatus.Failed, ErrorCode.CheckoutInvalid);
        await RunAsync(rig, "run-cap", "Explain the cache", Now.AddHours(-6), ResultStatus.Failed, ErrorCode.CostCapReached);
        await RunAsync(rig, "run-ok", "Something that worked", Now.AddHours(-2));
        Assert.Equal(HttpStatusCode.OK, (await Post(rig.Owner, "/v1/inbox/dismiss", "failure:run-cap")).StatusCode);

        var body = await ConsoleRig.Get(rig.Owner, "/v1/inbox");

        Assert.Empty(Ids(body, "items"));
        var handled = body.GetProperty("handled").EnumerateArray().ToList();
        Assert.Equal(["failure:run-cap", "failure:run-checkout"], handled.Select(i => i.GetProperty("id").GetString()));
        Assert.All(handled, i => Assert.Equal("handled", i.GetProperty("lane").GetString()));
        Assert.Equal(["dismissed", "infrastructure"], handled.Select(i => i.GetProperty("resolved_by").GetString()));
        var undo = handled[1].GetProperty("options").EnumerateArray().Single();
        Assert.Equal(("undo", "/v1/inbox/restore"), (undo.GetProperty("id").GetString(), undo.GetProperty("call").GetProperty("path").GetString()));
        Assert.Equal(0, body.GetProperty("counts").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Undo_on_an_item_a_rule_resolved_keeps_it_open_and_a_later_dismiss_still_takes_it_out()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await RunAsync(rig, "run-checkout", "Probe the checkout", Now.AddHours(-7), ResultStatus.Failed, ErrorCode.CheckoutInvalid);
        await RunAsync(rig, "run-ok", "Something that worked", Now.AddHours(-2));

        var restored = await Post(rig.Owner, "/v1/inbox/restore", "failure:run-checkout");

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.True((await restored.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("changed").GetBoolean());
        var body = await ConsoleRig.Get(rig.Owner, "/v1/inbox");
        Assert.Equal(["failure:run-checkout"], Ids(body, "items"));
        Assert.Empty(Ids(body, "handled"));
        Assert.Equal(0, body.GetProperty("resolved").GetProperty("infrastructure").GetInt32());
        // Restoring an item that is open already changes nothing, and does not hand it back to the rule.
        Assert.False((await (await Post(rig.Owner, "/v1/inbox/restore", "failure:run-checkout")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("changed").GetBoolean());
        Assert.Equal(["failure:run-checkout"], Ids(await ConsoleRig.Get(rig.Owner, "/v1/inbox"), "items"));

        Assert.Equal(HttpStatusCode.OK, (await Post(rig.Owner, "/v1/inbox/dismiss", "failure:run-checkout")).StatusCode);
        Assert.Empty(Ids(await ConsoleRig.Get(rig.Owner, "/v1/inbox"), "items"));
    }
}
