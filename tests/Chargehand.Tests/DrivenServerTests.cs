using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chargehand.Contracts;

namespace Chargehand.Tests;

/// <summary>Driven sessions (ADR 0039): what a client and a kill switch need from the server: a run list, cancel and halt.</summary>
public class DrivenServerTests
{
    private static async Task<JsonElement> Body(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition())
                return;
            await Task.Delay(50);
        }
        Assert.Fail("the condition did not hold within 10 seconds");
    }

    private static async Task<string> Finish(TestServer s)
    {
        var res = await s.PostAsync(Runs.DraftRequest(), waitSeconds: 20);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await Body(res)).GetProperty("task_id").GetString()!;
    }

    [Fact]
    public async Task The_run_list_is_newest_first_validates_and_filters_by_status_and_limit()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        var first = await Finish(s);
        await Task.Delay(1100);
        var second = await Finish(s);

        var list = await Body(await s.Http.GetAsync("/v1/runs"));
        var rows = list.GetProperty("runs").EnumerateArray().ToList();
        Assert.Equal([second, first], rows.Select(r => r.GetProperty("run_id").GetString()));
        Assert.All(rows, r => Assert.Empty(ContractSchemas.Validate(ContractSchemas.RunSummary, r)));
        Assert.Equal("draft", rows[0].GetProperty("preset").GetString());
        Assert.Equal("completed", rows[0].GetProperty("status").GetString());
        Assert.True(rows[0].TryGetProperty("finished_at", out _));

        Assert.Single((await Body(await s.Http.GetAsync("/v1/runs?limit=1"))).GetProperty("runs").EnumerateArray());
        Assert.Equal(2, (await Body(await s.Http.GetAsync("/v1/runs?status=completed"))).GetProperty("runs").GetArrayLength());
        Assert.Equal(0, (await Body(await s.Http.GetAsync("/v1/runs?status=failed"))).GetProperty("runs").GetArrayLength());
        var future = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0, (await Body(await s.Http.GetAsync($"/v1/runs?since={future}"))).GetProperty("runs").GetArrayLength());
    }

    [Fact]
    public async Task An_unfinished_run_is_listed_as_running_and_a_dead_owners_as_lost()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Hold = new() };
        await using var s = await TestServer.StartAsync(runtime);
        var id = (await Body(await s.PostAsync(Runs.DraftRequest(), waitSeconds: 0))).GetProperty("run_id").GetString();
        await s.Log.AppendAsync(new Chargehand.RunLog.StartRecord("run-dead", DateTimeOffset.UtcNow, new string('0', 32), Runs.DraftRequest(), int.MaxValue), CancellationToken.None);
        var statuses = (await Body(await s.Http.GetAsync("/v1/runs"))).GetProperty("runs").EnumerateArray().ToDictionary(r => r.GetProperty("run_id").GetString()!, r => r.GetProperty("status").GetString());
        Assert.Equal("running", statuses[id!]);
        Assert.Equal("lost", statuses["run-dead"]);
    }

    [Fact]
    public async Task A_bad_query_is_400_and_the_list_needs_the_key()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Http.GetAsync("/v1/runs?status=paused")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Http.GetAsync("/v1/runs?limit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Http.GetAsync("/v1/runs?since=yesterday")).StatusCode);
        using var anonymous = new HttpClient { BaseAddress = s.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/runs")).StatusCode);
    }

    [Fact]
    public async Task Cancel_ends_a_running_run_failed_with_cancelled_and_nothing_else_is_touched()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Hold = new() };
        await using var s = await TestServer.StartAsync(runtime);
        var id = (await Body(await s.PostAsync(Runs.DraftRequest(), waitSeconds: 0))).GetProperty("run_id").GetString();
        var res = await s.Http.PostAsync($"/v1/runs/{id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        JsonElement done = default;
        await WaitUntil(async () =>
        {
            var get = await s.Http.GetAsync($"/v1/runs/{id}");
            if (get.StatusCode != HttpStatusCode.OK)
                return false;
            done = await Body(get);
            return true;
        });
        Assert.Equal("failed", done.GetProperty("status").GetString());
        Assert.Equal("cancelled", done.GetProperty("error").GetProperty("code").GetString());
        Assert.False(done.GetProperty("error").GetProperty("retryable").GetBoolean());
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.Result, done));
        var listed = (await Body(await s.Http.GetAsync("/v1/runs"))).GetProperty("runs").EnumerateArray().Single(r => r.GetProperty("run_id").GetString() == id);
        Assert.Equal("failed", listed.GetProperty("status").GetString());
        Assert.Equal("cancelled", listed.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task Cancelling_an_unknown_or_finished_run_is_refused_and_needs_the_key()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Http.PostAsync("/v1/runs/run-none/cancel", null)).StatusCode);
        var done = await Finish(s);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Http.PostAsync($"/v1/runs/{done}/cancel", null)).StatusCode);
        using var anonymous = new HttpClient { BaseAddress = s.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/v1/runs/{done}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task Halt_cancels_every_run_refuses_new_ones_with_a_reason_and_resume_reopens()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Hold = new() };
        await using var s = await TestServer.StartAsync(runtime);
        var a = (await Body(await s.PostAsync(Runs.DraftRequest(), waitSeconds: 0))).GetProperty("run_id").GetString();
        var b = (await Body(await s.PostAsync(Runs.DraftRequest(), waitSeconds: 0))).GetProperty("run_id").GetString();
        var halt = await s.Http.PostAsync("/v1/halt", null);
        Assert.Equal(HttpStatusCode.OK, halt.StatusCode);
        Assert.Equal(2, (await Body(halt)).GetProperty("cancelled").GetInt32());
        foreach (var id in new[] { a, b })
            await WaitUntil(async () => (await s.Http.GetAsync($"/v1/runs/{id}")).StatusCode == HttpStatusCode.OK);

        var refused = await s.PostAsync(Runs.DraftRequest(), waitSeconds: 0);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Contains("halted", await refused.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await s.Http.PostAsync("/v1/resume", null)).StatusCode);
        runtime.Hold.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await s.PostAsync(Runs.DraftRequest(), waitSeconds: 20)).StatusCode);
    }

    [Fact]
    public async Task Halt_and_resume_need_the_key()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        using var anonymous = new HttpClient { BaseAddress = s.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/v1/halt", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/v1/resume", null)).StatusCode);
    }
}
