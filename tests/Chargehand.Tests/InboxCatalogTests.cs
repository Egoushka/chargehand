using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>The inbox catalog (ADR 0085): every code an inbox item can carry has a plain <c>what</c> and <c>why</c>, and every option it offers is a route the server maps.</summary>
public partial class InboxCatalogTests
{
    /// <summary>Every code a failure item can carry: the error codes, and what <see cref="ConsoleInbox"/> adds of its own.</summary>
    public static TheoryData<string> EmittedCodes()
    {
        var codes = new TheoryData<string>();
        foreach (var code in Enum.GetValues<ErrorCode>())
            codes.Add(JsonNamingPolicy.SnakeCaseLower.ConvertName(code.ToString()));
        foreach (var extra in new[] { "partial", "lost", "failed" })
            codes.Add(extra);
        return codes;
    }

    [Theory]
    [MemberData(nameof(EmittedCodes))]
    public void Every_code_the_server_can_emit_has_an_entry(string code)
    {
        var entry = InboxCatalog.Entry(code);

        Assert.NotNull(entry);
        Assert.False(string.IsNullOrWhiteSpace(entry.Verb));
        Assert.False(string.IsNullOrWhiteSpace(entry.Why));
    }

    [Fact]
    public void Every_code_in_the_reference_table_has_an_entry()
    {
        var table = File.ReadAllText(Repo.Path("docs", "guide", "reference.md")).Split("## Error codes", 2)[1].Split("\n## ", 2)[0];
        var codes = TableCode().Matches(table).Select(m => m.Groups[1].Value).ToList();

        Assert.True(codes.Count >= 25, $"read {codes.Count} codes from the reference table");
        Assert.All(codes, code => Assert.NotNull(InboxCatalog.Entry(code)));
    }

    [Theory]
    [InlineData("policy_ask")]
    [InlineData("test_integrity")]
    [InlineData("partial_draft")]
    public void Every_reason_a_pull_request_waits_has_words(string reason) =>
        Assert.False(string.IsNullOrWhiteSpace(InboxCatalog.PullRequestWhy(reason)));

    [GeneratedRegex(@"(?m)^\| `([a-z_]+)` \|")]
    private static partial Regex TableCode();

    /// <summary>A question, a pull request, a failed task, a failed review run, a lost run and a refused task: every option on them names a route the server maps.</summary>
    [Fact]
    public async Task Every_option_names_a_route_the_server_maps()
    {
        await using var rig = await ConsoleRig.StartAsync(profile: root => Runs.Profile(root) with
        {
            Driven = new DrivenSettings(Integrator: new DrivenIntegrator(Repositories: new Dictionary<string, string> { ["o/r"] = "ask" })),
        });
        await SeedAsync(rig);

        var items = (await ConsoleRig.Get(rig.Owner, "/v1/inbox")).GetProperty("items").EnumerateArray().ToList();
        var routes = rig.Server.Routes.Select(r => (r.Method, Pattern: new Regex("^" + Regex.Replace(Regex.Escape(r.Pattern), @"\\\{[^}]+}", "[^/]+") + "$"))).ToList();

        Assert.Equal(5, items.Count);
        foreach (var item in items)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("what").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("why").GetString()));
            var options = item.GetProperty("options").EnumerateArray().ToList();
            Assert.True(options.Count >= 2, $"{item.GetProperty("id")} offers {options.Count} options");
            foreach (var option in options.Where(o => o.TryGetProperty("call", out var call) && call.ValueKind == JsonValueKind.Object))
            {
                var call = option.GetProperty("call");
                var (method, path) = (call.GetProperty("method").GetString()!, call.GetProperty("path").GetString()!);
                Assert.Contains(routes, r => r.Method == method && r.Pattern.IsMatch(path));
            }
        }
    }

    [Fact]
    public async Task A_failed_session_says_what_and_why_in_plain_words_and_offers_retry_take_over_and_dismiss()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await SeedAsync(rig);

        var item = await ItemAsync(rig, "failure:b-fail:t1");

        Assert.Equal("A task on o/r ended without a usable branch", item.GetProperty("what").GetString());
        Assert.Equal("the session ended with an error; its log has the details", item.GetProperty("why").GetString());
        var options = item.GetProperty("options").EnumerateArray().ToList();
        Assert.Equal(["retry", "take_over", "dismiss"], options.Select(o => o.GetProperty("id").GetString()));
        var retry = options[0];
        Assert.Equal(("POST", "/v1/batches/b-fail/tasks/t1/retry"), (retry.GetProperty("call").GetProperty("method").GetString(), retry.GetProperty("call").GetProperty("path").GetString()));
        Assert.Equal(1.5m, retry.GetProperty("estimate_usd").GetDecimal());
        Assert.Equal("chargehand takeover b-fail/t1", options[1].GetProperty("command").GetString());
        Assert.Equal(["failure:b-fail:t1"], options[2].GetProperty("call").GetProperty("body").GetProperty("ids").EnumerateArray().Select(i => i.GetString()));
        Assert.DoesNotContain("session_failed", item.GetProperty("what").GetString());
    }

    [Fact]
    public async Task A_refused_checkout_offers_no_retry()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await SeedAsync(rig);

        var item = await ItemAsync(rig, "failure:b-secret:t1");

        Assert.Contains("look like secrets", item.GetProperty("why").GetString());
        Assert.DoesNotContain("retry", item.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task A_review_that_stopped_at_its_budget_names_the_pull_request_and_its_spend()
    {
        await using var rig = await ConsoleRig.StartAsync();
        await SeedAsync(rig);

        var item = await ItemAsync(rig, "failure:run-review");

        Assert.Equal("A review of pull request o/r#7 stopped at its budget", item.GetProperty("what").GetString());
        Assert.Equal(["dismiss"], item.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("id").GetString()).Where(id => id != "open"));
    }

    private static async Task<JsonElement> ItemAsync(ConsoleRig rig, string id) =>
        (await ConsoleRig.Get(rig.Owner, "/v1/inbox")).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == id);

    private static async Task SeedAsync(ConsoleRig rig)
    {
        var at = ConsoleRig.Now;
        await ConsoleSeed.BatchAsync(rig.Log, "b-q", null, at.AddHours(-5), new SeedTask("t1", "needs_input", Goal: "Pick a queue", Questions: ["Which queue?"]));
        await ConsoleSeed.BatchAsync(rig.Log, "b-ask", null, at.AddHours(-4), new SeedTask("t1", PrUrl: "https://example.test/o/r/pull/1"));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b-ask", "t1", pr: ConsoleSeed.Pull(1), at: at.AddHours(-4)));
        await ConsoleSeed.BatchAsync(rig.Log, "b-fail", null, at.AddHours(-6), new SeedTask("t1", "failed", Goal: "Fix the thing", Error: ErrorCode.SessionFailed, Detail: "the session did not finish: session error"));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b-fail", "t1", LearningOutcome.Failed, at: at.AddHours(-6)));
        await ConsoleSeed.BatchAsync(rig.Log, "b-secret", null, at.AddHours(-7), new SeedTask("t1", "failed", Goal: "Fix more", Error: ErrorCode.CheckoutHasSecrets, Detail: "secrets"));
        await ConsoleSeed.LearnAsync(rig, ConsoleSeed.Learned("b-secret", "t1", LearningOutcome.Failed, at: at.AddHours(-7)));
        var review = ConsoleSeed.Start("run-review", Runs.DraftRequest() with { Text = "Review pull request o/r#7." }, null, at.AddHours(-8));
        await rig.Log.AppendAsync(review, default);
        await rig.Log.AppendAsync(ConsoleSeed.Finish(review, ConsoleSeed.Result("run-review", ResultStatus.Failed, "over budget", error: new ResultError(ErrorCode.CostCapReached, "over budget", false, "raise it")), at.AddHours(-8)),
            default);
    }
}
