using System.Text.Json;
using Chargehand.Modules.Events.Api;

namespace Chargehand.Modules.Events.Tests;

public sealed class ThreadListTests
{
    private static async Task<List<string?>> ListAsync(Log log, string? state = null, string? scope = null, EventGrant? grant = null, int? limit = null)
    {
        var answer = await log.Service.ThreadsAsync(grant ?? EventGrant.Owner, state, scope, limit, CancellationToken.None);
        Assert.Equal(200, answer.Status);
        return [.. Log.Parse(answer).GetProperty("threads").EnumerateArray().Select(t => t.GetProperty("thread").GetString())];
    }

    private static async Task ThreeThreadsAsync(Log log)
    {
        await log.PostAsync(Log.Step("a1", "A", "x", "y", goal: "g", time: "2026-11-08T10:00:00Z"));
        await log.PostAsync(Log.Step("b1", "B", "x", "y", goal: "g", time: "2026-11-08T10:30:00Z", scope: "project:Egoushka/b"));
        await log.PostAsync(Log.Step("b2", "B", "done", state: "closed", time: "2026-11-08T10:40:00Z", scope: "project:Egoushka/b"));
        await log.PostAsync(Log.Step("c1", "C", "x", "y", goal: "g", time: "2026-11-08T11:00:00Z", scope: "project:Egoushka/c"));
    }

    [Fact]
    public async Task Open_threads_come_newest_activity_first()
    {
        using var log = new Log();
        await ThreeThreadsAsync(log);

        Assert.Equal(["C", "A"], await ListAsync(log));
        Assert.Equal(["C", "A"], await ListAsync(log, "open"));
    }

    [Fact]
    public async Task Closed_and_all_list_what_they_name()
    {
        using var log = new Log();
        await ThreeThreadsAsync(log);

        Assert.Equal(["B"], await ListAsync(log, "closed"));
        Assert.Equal(3, (await ListAsync(log, "all")).Count);
    }

    [Fact]
    public async Task A_step_with_state_open_on_a_closed_thread_reopens_it()
    {
        using var log = new Log();
        await ThreeThreadsAsync(log);

        await log.PostAsync(Log.Step("b3", "B", "again", "more", state: "open", time: "2026-11-08T12:00:00Z", scope: "project:Egoushka/b"));

        Assert.Equal(["B", "C", "A"], await ListAsync(log));
        Assert.Empty(await ListAsync(log, "closed"));
    }

    [Fact]
    public async Task A_scope_lists_only_threads_whose_latest_step_is_in_it()
    {
        using var log = new Log();
        await ThreeThreadsAsync(log);
        await log.PostAsync(Log.Step("c2", "C", "moved", "on", time: "2026-11-08T11:30:00Z", scope: "project:Egoushka/a"));
        await log.PostAsync(Log.Step("a2", "A", "moved", "away", time: "2026-11-08T11:40:00Z", scope: "project:Egoushka/c"));

        Assert.Equal(["C"], await ListAsync(log, scope: "project:Egoushka/a"));
        Assert.Equal(["A", "C"], await ListAsync(log, scope: "project:*"));
    }

    [Fact]
    public async Task A_limit_caps_the_list_and_an_item_has_no_text()
    {
        using var log = new Log();
        await ThreeThreadsAsync(log);

        Assert.Equal(["C"], await ListAsync(log, limit: 1));
        var item = Log.Parse(await log.Service.ThreadsAsync(EventGrant.Owner, null, null, null, CancellationToken.None)).GetProperty("threads")[0];
        Assert.False(item.TryGetProperty("text", out _));
        Assert.Equal(JsonValueKind.Object, item.GetProperty("last_step").ValueKind);
    }

    [Fact]
    public async Task An_unknown_state_gets_400_and_a_limit_out_of_range_is_clamped()
    {
        using var log = new Log();
        await ThreeThreadsAsync(log);

        Assert.Equal(400, (await log.Service.ThreadsAsync(EventGrant.Owner, "paused", null, null, CancellationToken.None)).Status);
        Assert.Equal(["C", "A"], await ListAsync(log, limit: 201));
        Assert.Equal(["C"], await ListAsync(log, limit: 0));
    }
}
