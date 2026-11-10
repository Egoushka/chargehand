using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Server;

namespace Chargehand.Tests;

// agent-checks: allow deleted-tests, deleted-assertions -- the pure unit tests moved to tests/Chargehand.Modules.Accounts.Tests/RunApprovalGateTests.cs with the code
/// <summary>ADR 0092: a run whose budget passes the per-run threshold waits for the owner's approval in the inbox before it starts.</summary>
public class RunApprovalTests
{
    private static RunRequest Asking(decimal usd) => Runs.DraftRequest() with { Context = Runs.DraftRequest().Context with { BudgetUsd = usd } };

    private static async Task<JsonElement> ApprovalsAsync(HttpClient http) =>
        (await http.GetFromJsonAsync<JsonElement>("/v1/inbox")).GetProperty("items").EnumerateArray().Where(i => i.GetProperty("kind").GetString() == "approval").ToArray() is var items
            ? JsonSerializer.SerializeToElement(items)
            : default;

    [Fact]
    public async Task The_console_reads_and_sets_the_threshold_and_refuses_one_that_is_not_above_zero()
    {
        await using var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [new AccountSettings("alice", AllowServerCredential: true)]);

        var read = await server.Http.GetFromJsonAsync<JsonElement>("/v1/limits/run-approval");
        Assert.Equal("run-approval/v1", read.GetProperty("contract_version").GetString());
        Assert.Equal((5m, 5m, 0), (read.GetProperty("limit_usd").GetDecimal(), read.GetProperty("default_usd").GetDecimal(), read.GetProperty("pending").GetInt32()));

        var put = await server.Http.PutAsJsonAsync("/v1/limits/run-approval", new { usd = 20 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(20m, (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("limit_usd").GetDecimal());

        var off = await server.Http.PutAsJsonAsync("/v1/limits/run-approval", new { usd = (decimal?)null });
        Assert.False((await off.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("limit_usd", out var limit) && limit.ValueKind != JsonValueKind.Null);

        foreach (var bad in new object[] { new { usd = 0 }, new { usd = -1 }, new { usd = "5" }, new { other = 1 } })
            Assert.Equal(HttpStatusCode.BadRequest, (await server.Http.PutAsJsonAsync("/v1/limits/run-approval", bad)).StatusCode);

        using var alice = server.As(server.Keys.Create("alice", null).Key);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/v1/limits/run-approval")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PutAsJsonAsync("/v1/limits/run-approval", new { usd = 1 })).StatusCode);
    }

    [Fact]
    public async Task A_run_over_the_threshold_is_held_for_the_owner_and_starts_once_approved()
    {
        await using var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));

        var held = await server.PostAsync(Asking(8m), 0);
        Assert.Equal(HttpStatusCode.Conflict, held.StatusCode);
        Assert.Contains("approval_required", await held.Content.ReadAsStringAsync());
        Assert.Empty(await server.Log.ListAsync(new RunListQuery(), CancellationToken.None));

        var cards = (await ApprovalsAsync(server.Http)).EnumerateArray().ToArray();
        var card = Assert.Single(cards);
        Assert.Equal("decide", card.GetProperty("lane").GetString());
        Assert.Equal(["approve", "decline"], card.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("id").GetString()).Take(2));

        // The same request again is the same question, not a second one.
        Assert.Equal(HttpStatusCode.Conflict, (await server.PostAsync(Asking(8m), 0)).StatusCode);
        Assert.Single((await ApprovalsAsync(server.Http)).EnumerateArray());

        var id = card.GetProperty("id").GetString()!["approval:".Length..];
        Assert.Equal(HttpStatusCode.OK, (await server.Http.PostAsync($"/v1/approvals/{id}/approve", null)).StatusCode);
        Assert.Empty((await ApprovalsAsync(server.Http)).EnumerateArray());

        var started = await server.PostAsync(Asking(8m), 30);
        Assert.True(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync());
        Assert.NotNull(started.Headers.Location);

        // One approval pays for one start.
        Assert.Equal(HttpStatusCode.Conflict, (await server.PostAsync(Asking(8m), 0)).StatusCode);
    }

    [Fact]
    public async Task A_declined_run_is_refused_once_and_asked_about_again_when_sent_again()
    {
        await using var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        Assert.Equal(HttpStatusCode.Conflict, (await server.PostAsync(Asking(8m), 0)).StatusCode);
        var id = (await ApprovalsAsync(server.Http)).EnumerateArray().Single().GetProperty("id").GetString()!["approval:".Length..];

        Assert.Equal(HttpStatusCode.OK, (await server.Http.PostAsync($"/v1/approvals/{id}/decline", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Http.PostAsync($"/v1/approvals/{id}/decline", null)).StatusCode);

        var declined = await server.PostAsync(Asking(8m), 0);
        Assert.Equal(HttpStatusCode.Forbidden, declined.StatusCode);
        Assert.Contains("approval_declined", await declined.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await server.PostAsync(Asking(8m), 0)).StatusCode);
    }

    [Fact]
    public async Task A_run_at_or_under_the_threshold_with_no_budget_or_with_the_threshold_off_starts_without_asking()
    {
        await using var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        foreach (var request in new[] { Asking(5m), Asking(0.5m), Runs.DraftRequest() })
            Assert.NotEqual(HttpStatusCode.Conflict, (await server.PostAsync(request, 30)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await server.Http.PutAsJsonAsync("/v1/limits/run-approval", new { usd = (decimal?)null })).StatusCode);
        Assert.NotEqual(HttpStatusCode.Conflict, (await server.PostAsync(Asking(500m), 30)).StatusCode);
        Assert.Empty((await ApprovalsAsync(server.Http)).EnumerateArray());
    }

    [Fact]
    public async Task A_subscription_run_has_no_priced_spend_and_never_asks()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Credential = "subscription" };
        await using var server = await TestServer.StartAsync(runtime);
        Assert.NotEqual(HttpStatusCode.Conflict, (await server.PostAsync(Asking(50m), 30)).StatusCode);
        Assert.Empty((await ApprovalsAsync(server.Http)).EnumerateArray());
    }

    [Fact]
    public async Task An_account_waits_for_the_owner_and_cannot_approve_its_own_run()
    {
        await using var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: [new AccountSettings("alice", AllowServerCredential: true)]);
        using var alice = server.As(server.Keys.Create("alice", null).Key);

        var post = new HttpRequestMessage(HttpMethod.Post, "/v1/runs") { Content = JsonContent.Create(Asking(8m), options: ContractJson.Options) };
        post.Headers.Add("Prefer", "wait=0");
        Assert.Equal(HttpStatusCode.Conflict, (await alice.SendAsync(post)).StatusCode);

        var id = (await ApprovalsAsync(server.Http)).EnumerateArray().Single().GetProperty("id").GetString()!["approval:".Length..];
        Assert.Empty((await ApprovalsAsync(alice)).EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsync($"/v1/approvals/{id}/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.Http.PostAsync($"/v1/approvals/{id}/approve", null)).StatusCode);

        var again = new HttpRequestMessage(HttpMethod.Post, "/v1/runs") { Content = JsonContent.Create(Asking(8m), options: ContractJson.Options) };
        again.Headers.Add("Prefer", "wait=30");
        Assert.True((await alice.SendAsync(again)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task An_automated_start_over_the_threshold_is_held_for_approval_and_starts_after_it()
    {
        await using var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        var starter = new RunServiceStarter(server.Service);

        var held = await starter.StartAsync(Asking(8m), CancellationToken.None);
        Assert.Null(held.RunId);
        Assert.Contains("approval", held.Refusal, StringComparison.OrdinalIgnoreCase);
        Assert.Single((await ApprovalsAsync(server.Http)).EnumerateArray());

        // Polled again, it is the same question.
        await starter.StartAsync(Asking(8m), CancellationToken.None);
        Assert.Single((await ApprovalsAsync(server.Http)).EnumerateArray());

        var id = (await ApprovalsAsync(server.Http)).EnumerateArray().Single().GetProperty("id").GetString()!["approval:".Length..];
        await server.Http.PostAsync($"/v1/approvals/{id}/approve", null);
        Assert.NotNull((await starter.StartAsync(Asking(8m), CancellationToken.None)).RunId);
    }
}
