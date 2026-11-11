using System.Net;
using System.Text;
using System.Text.Json;
using Chargehand.Modules.Events.Api;

namespace Chargehand.Tests;

/// <summary>Events spec, Task 1 acceptance: one session's tool calls are queryable right after they are posted, by time, kind, source, scope and session.</summary>
public sealed class EventAcceptanceTests
{
    private const string Token = "producer-token-for-tests";

    private static readonly EventGrant Producer = new("mac", "claude", ["project:Egoushka/a"], ["project:Egoushka/a"], "project:Egoushka/a");

    private static string Call(int i, string time, string session, string type = "claude.tool.called", string source = "claude") =>
        $"{{\"specversion\":\"1.0\",\"id\":\"t{i}\",\"source\":\"{source}\",\"type\":\"{type}\",\"time\":\"{time}\",\"correlationid\":\"{session}\",\"data\":{{\"tool\":\"Bash\"}}}}";

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient http, HttpMethod method, string path, string? json = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static List<string> Ids(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToList();

    [Fact]
    public async Task A_sessions_tool_calls_posted_as_one_batch_are_queryable_by_time_kind_source_scope_and_session_in_seq_order()
    {
        EventService? events = null;
        await using var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), tweak: (root, s) =>
        {
            events = new EventService(SqliteEventLog.Open(Path.Combine(root, "events", "events.db")), [(Producer, Token)]);
            return s with { Events = events };
        });
        using var __ = events;
        using var http = server.As(Token);

        var posted = await SendAsync(http, HttpMethod.Post, "/v1/events", "[" + string.Join(",",
            Call(1, "2026-11-08T12:00:00Z", "s1"), Call(2, "2026-11-08T12:00:30Z", "s1"), Call(3, "2026-11-08T12:01:00Z", "s1", "claude.tool.failed"),
            Call(4, "2026-11-08T12:02:00Z", "s2"), Call(5, "2026-11-08T12:03:00Z", "s1")) + "]");
        Assert.Equal(HttpStatusCode.OK, posted.Status);

        Assert.Equal(["t1", "t2", "t3", "t4", "t5"], Ids((await SendAsync(http, HttpMethod.Get, "/v1/events")).Body));
        Assert.Equal(["t2", "t3", "t4"], Ids((await SendAsync(http, HttpMethod.Get, "/v1/events?since=2026-11-08T12:00:30Z&until=2026-11-08T12:02:00Z")).Body));
        Assert.Equal(["t1", "t2", "t3", "t5"], Ids((await SendAsync(http, HttpMethod.Get, "/v1/events?correlationid=s1")).Body));
        Assert.Equal(["t3"], Ids((await SendAsync(http, HttpMethod.Get, "/v1/events?type=claude.tool.failed&source=claude&scope=project:Egoushka/a")).Body));

        var bad = await SendAsync(http, HttpMethod.Get, "/v1/events?since=yesterday");
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);

        var work = await SendAsync(http, HttpMethod.Post, "/v1/events",
            "{\"specversion\":\"1.0\",\"id\":\"w1\",\"source\":\"claude\",\"type\":\"claude.tool.called\",\"time\":\"2026-11-08T12:04:00Z\",\"data\":{\"cwd\":\"/home/u/work/bison\"}}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, work.Status);
    }
}
