using Chargehand.Modules.Events.Api;

namespace Chargehand.Modules.Events.Tests;

public sealed class EventScopeTests
{
    [Fact]
    public async Task A_token_granted_one_project_querying_any_scope_gets_only_that_projects_events()
    {
        using var log = new Log();
        await log.PostAsync($"[{Log.Envelope("a1", scope: "project:Egoushka/a")},{Log.Envelope("b1", scope: "project:Egoushka/b")},{Log.Envelope("g1", scope: "global")}]");

        var answer = await log.Service.QueryAsync(Log.ProjectA, new EventQuery(Scope: "*"), CancellationToken.None);

        var ids = Log.Parse(answer).GetProperty("events").EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
        Assert.Equal(["a1"], ids);
    }

    [Fact]
    public async Task The_stream_is_filtered_by_the_grants_too()
    {
        using var log = new Log();
        await log.PostAsync($"[{Log.Envelope("a1", scope: "project:Egoushka/a")},{Log.Envelope("b1", scope: "project:Egoushka/b")},{Log.Envelope("a2", scope: "project:Egoushka/a")}]");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var seen = new List<string>();
        await foreach (var e in log.Service.StreamAsync(Log.ProjectA, new EventQuery(Scope: "*"), cts.Token))
        {
            seen.Add(e.Id);
            if (seen.Count == 2)
                break;
        }

        Assert.Equal(["a1", "a2"], seen);
    }

    [Fact]
    public async Task An_exact_scope_outside_the_grants_gets_403()
    {
        using var log = new Log();

        var answer = await log.Service.QueryAsync(Log.ProjectA, new EventQuery(Scope: "project:Egoushka/b"), CancellationToken.None);

        Assert.Equal(403, answer.Status);
        Assert.NotNull(new EventQuery(Scope: "project:Egoushka/b").Problem(Log.ProjectA));
    }

    [Fact]
    public async Task A_write_outside_the_write_grants_gets_403()
    {
        using var log = new Log();

        Assert.Equal(403, (await log.PostAsync(Log.Envelope("e1", scope: "project:Egoushka/b"), Log.ProjectA)).Status);
        Assert.Equal(0, await log.CountAsync());
    }

    [Fact]
    public async Task Any_token_may_post_a_message_to_an_agent_inbox()
    {
        using var log = new Log();

        var answer = await log.PostAsync(Log.Envelope("m1", type: "agent.message.sent", scope: "agent:cc/other"), Log.ProjectA);

        Assert.Equal(200, answer.Status);
    }

    [Fact]
    public async Task An_event_without_scope_gets_the_tokens_default_else_its_run_else_global()
    {
        using var log = new Log();
        await log.PostAsync(Log.Envelope("p1", scope: null), Log.ProjectA);
        await log.PostAsync(Log.Envelope("r1", scope: null, actor: "{\"kind\":\"agent\",\"id\":\"x\",\"run\":\"run-7\"}"));
        await log.PostAsync(Log.Envelope("g1", scope: null));

        var stored = await log.Store.ReadAsync(new EventFilter(), CancellationToken.None);

        Assert.Equal(["project:Egoushka/a", "run:run-7", "global"], stored.Select(e => e.Scope));
        Assert.Contains("\"scope\":\"global\"", stored[2].Json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("project:*", "project:Egoushka/a", true)]
    [InlineData("project:*", "projects:x", false)]
    [InlineData("agent:cc/*", "agent:cc/abc", true)]
    [InlineData("agent:cc/*", "agent:oc/abc", false)]
    [InlineData("global", "global", true)]
    [InlineData("*", "anything", true)]
    [InlineData("a*b*c", "aXbYc", true)]
    [InlineData("a*b*c", "aXbY", false)]
    public void Globs_match_with_star_only(string glob, string value, bool expected) =>
        Assert.Equal(expected, Glob.Match(glob, value));

    [Fact]
    public async Task Sqlite_glob_characters_in_a_grant_match_literally()
    {
        using var log = new Log();
        await log.PostAsync($"[{Log.Envelope("q1", scope: "channel:a?b")},{Log.Envelope("q2", scope: "channel:axb")}]");
        var grant = new EventGrant("q", "claude", ["channel:a?b"], []);

        var answer = await log.Service.QueryAsync(grant, new EventQuery(Scope: "*"), CancellationToken.None);

        var ids = Log.Parse(answer).GetProperty("events").EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
        Assert.Equal(["q1"], ids);
    }
}
