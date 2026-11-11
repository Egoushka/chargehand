using Chargehand.Modules.Events.Api;

namespace Chargehand.Modules.Events.Tests;

public sealed class ThreadScopeTests
{
    private static async Task TwoProjectsAsync(Log log)
    {
        await log.PostAsync(Log.Step("a1", "TA", "x", "y", goal: "g", scope: "project:Egoushka/a", time: "2026-11-08T10:00:00Z"));
        await log.PostAsync(Log.Step("b1", "TB", "x", "y", goal: "g", scope: "project:Egoushka/b", time: "2026-11-08T11:00:00Z"));
    }

    [Fact]
    public async Task A_token_granted_one_project_lists_only_that_projects_threads()
    {
        using var log = new Log();
        await TwoProjectsAsync(log);

        var answer = await log.Service.ThreadsAsync(Log.ProjectA, "all", null, null, CancellationToken.None);

        var threads = Log.Parse(answer).GetProperty("threads").EnumerateArray().Select(t => t.GetProperty("thread").GetString());
        Assert.Equal(["TA"], threads);
    }

    [Fact]
    public async Task A_card_outside_the_grants_gets_404_not_403()
    {
        using var log = new Log();
        await TwoProjectsAsync(log);

        Assert.Equal(404, (await log.Service.CardAsync(Log.ProjectA, "TB", CancellationToken.None)).Status);
        Assert.Equal(200, (await log.Service.CardAsync(Log.ProjectA, "TA", CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_thread_that_moved_to_an_unreadable_scope_disappears()
    {
        using var log = new Log();
        await TwoProjectsAsync(log);

        await log.PostAsync(Log.Step("a2", "TA", "moved", "on", scope: "project:Egoushka/b", time: "2026-11-08T12:00:00Z"));

        Assert.Equal(404, (await log.Service.CardAsync(Log.ProjectA, "TA", CancellationToken.None)).Status);
    }

    [Fact]
    public async Task An_event_in_an_unreadable_scope_never_moves_last_activity()
    {
        using var log = new Log();
        await TwoProjectsAsync(log);

        await log.PostAsync(Log.Envelope("hidden", "chargehand.tool.called", scope: "project:Egoushka/b", correlation: "TA", time: "2026-11-08T15:00:00Z"));
        var card = Log.Parse(await log.Service.CardAsync(Log.ProjectA, "TA", CancellationToken.None));
        var owners = Log.Parse(await log.Service.CardAsync(EventGrant.Owner, "TA", CancellationToken.None));

        Assert.Equal("2026-11-08T10:00:00.000Z", card.GetProperty("last_activity").GetString());
        Assert.Equal("2026-11-08T15:00:00.000Z", owners.GetProperty("last_activity").GetString());
    }

    [Fact]
    public async Task Steps_in_an_unreadable_scope_are_not_counted_or_shown()
    {
        using var log = new Log();
        await log.PostAsync(Log.Step("b1", "T", "secret work", "y", goal: "hidden goal", scope: "project:Egoushka/b", time: "2026-11-08T10:00:00Z"));
        await log.PostAsync(Log.Step("a1", "T", "visible", "next", goal: "shown goal", scope: "project:Egoushka/a", time: "2026-11-08T11:00:00Z"));

        var card = Log.Parse(await log.Service.CardAsync(Log.ProjectA, "T", CancellationToken.None));

        Assert.Equal(1, card.GetProperty("steps").GetInt32());
        Assert.DoesNotContain("secret work", card.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("hidden goal", card.GetRawText(), StringComparison.Ordinal);
    }
}
