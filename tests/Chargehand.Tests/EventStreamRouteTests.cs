using System.Net;
using System.Text;
using Chargehand.Modules.Events.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0090, threads addendum of 2026-11-08: the event and thread routes over HTTP, the SSE stream's resume and the event tokens' fence.</summary>
public sealed class EventStreamRouteTests
{
    private const string EventToken = "event-token-for-tests";

    private static readonly EventGrant ProjectA = new("a", "claude", ["project:Egoushka/a"], ["project:Egoushka/a"], "project:Egoushka/a");

    private static async Task<(TestServer Server, EventService Events)> StartAsync()
    {
        EventService? events = null;
        var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), tweak: (root, s) =>
        {
            events = new EventService(SqliteEventLog.Open(Path.Combine(root, "events", "events.db")), [(ProjectA, EventToken)]);
            return s with { Events = events };
        });
        return (server, events!);
    }

    private static string Envelope(int i, string type = "claude.tool.called") =>
        $"{{\"specversion\":\"1.0\",\"id\":\"e{i}\",\"source\":\"claude\",\"type\":\"{type}\",\"time\":\"2026-11-08T12:00:00Z\",\"scope\":\"project:Egoushka/a\"}}";

    private static async Task PostAsync(HttpClient http, IEnumerable<string> envelopes)
    {
        using var body = new StringContent("[" + string.Join(",", envelopes) + "]", Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(new Uri("/v1/events", UriKind.Relative), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<List<long>> ReadIdsAsync(HttpClient http, string path, int count, string? lastEventId = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (lastEventId is not null)
            request.Headers.Add("Last-Event-ID", lastEventId);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        var ids = new List<long>();
        while (ids.Count < count && await reader.ReadLineAsync(cts.Token) is { } line)
        {
            if (line.StartsWith("id: ", StringComparison.Ordinal))
                ids.Add(long.Parse(line[4..], System.Globalization.CultureInfo.InvariantCulture));
        }
        return ids;
    }

    [Fact]
    public async Task A_client_that_disconnects_after_10_resumes_with_Last_Event_ID_from_11()
    {
        var (server, events) = await StartAsync();
        await using var _ = server;
        using var __ = events;
        await PostAsync(server.Http, Enumerable.Range(1, 15).Select(i => Envelope(i)));

        var first = await ReadIdsAsync(server.Http, "/v1/events/stream", 10);
        var resumeTask = ReadIdsAsync(server.Http, "/v1/events/stream", 7, lastEventId: "10");
        await PostAsync(server.Http, Enumerable.Range(16, 2).Select(i => Envelope(i)));
        var resumed = await resumeTask;

        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), first);
        Assert.Equal(Enumerable.Range(11, 7).Select(i => (long)i), resumed);
    }

    [Fact]
    public async Task A_type_glob_filters_both_the_query_and_the_stream()
    {
        var (server, events) = await StartAsync();
        await using var _ = server;
        using var __ = events;
        await PostAsync(server.Http, [Envelope(1), Envelope(2, "signal.rule.fired"), Envelope(3), Envelope(4, "signal.rule.fired")]);

        var query = await server.Http.GetStringAsync(new Uri("/v1/events?type=signal.*", UriKind.Relative));
        var streamed = await ReadIdsAsync(server.Http, "/v1/events/stream?type=signal.*", 2);

        Assert.Contains("\"id\":\"e2\"", query, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\":\"e1\"", query, StringComparison.Ordinal);
        Assert.Equal([2L, 4L], streamed);
    }

    [Fact]
    public async Task An_event_token_reaches_the_event_routes_and_nothing_else()
    {
        var (server, events) = await StartAsync();
        await using var _ = server;
        using var __ = events;
        using var token = server.As(EventToken);

        await PostAsync(token, [Envelope(1)]);
        using var runs = await token.GetAsync(new Uri("/v1/runs", UriKind.Relative));
        using var threads = await token.GetAsync(new Uri("/v1/threads", UriKind.Relative));
        using var stranger = await server.As("not-a-token").GetAsync(new Uri("/v1/events", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, runs.StatusCode);
        Assert.Equal(HttpStatusCode.OK, threads.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, stranger.StatusCode);
    }

    [Fact]
    public async Task A_card_is_served_by_its_url_encoded_id_and_a_bad_number_gets_400()
    {
        var (server, events) = await StartAsync();
        await using var _ = server;
        using var __ = events;
        const string step = """{"specversion":"1.0","id":"s1","source":"claude","type":"thread.step.recorded","subject":"Egoushka/a#12","time":"2026-11-08T12:00:00Z","scope":"project:Egoushka/a","data":{"goal":"g","done":"d","next":"n"}}""";
        await PostAsync(server.Http, [step]);

        var card = await server.Http.GetStringAsync(new Uri("/v1/threads/" + Uri.EscapeDataString("Egoushka/a#12") + "/card", UriKind.Relative));
        using var bad = await server.Http.GetAsync(new Uri("/v1/events?after=x", UriKind.Relative));

        Assert.Contains("\"thread\":\"Egoushka/a#12\"", card, StringComparison.Ordinal);
        Assert.Contains("\"text\":", card, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
