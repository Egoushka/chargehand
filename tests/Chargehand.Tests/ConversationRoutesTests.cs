using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Conversations.Api;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>The conversation routes (ADR 0086) over the scripted agent: start, read, stream with resume, message, approve and end, who may call them, and what the caps answer.</summary>
public class ConversationRoutesTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private static AccountSettings Account(string name) => new(name, AllowServerCredential: true);

    private static Task<TestServer> Server(IReadOnlyList<AccountSettings>? accounts = null, ConversationSettings? settings = null, IConversationRuntime? runtime = null, Action<string>? before = null) =>
        TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), accounts: accounts, tweak: (root, s) =>
        {
            before?.Invoke(Path.Combine(root, "conversations"));
            return s with { Conversations = settings ?? new ConversationSettings(), ConversationsDirectory = Path.Combine(root, "conversations"), ConversationRuntime = runtime };
        });

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static StringContent StartBody(string repository = "acme/widgets", string runtime = "fake") =>
        Json($$"""{"repository":"{{repository}}","commit":"{{Commit}}","runtime":"{{runtime}}"}""");

    private static async Task<JsonElement> Body(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string> StartedId(HttpClient http)
    {
        var res = await http.PostAsync("/v1/conversations", StartBody());
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await Body(res)).GetProperty("id").GetString()!;
    }

    private static Task<HttpResponseMessage> Say(HttpClient http, string id, string text) =>
        http.PostAsync($"/v1/conversations/{id}/messages", Json(JsonSerializer.Serialize(new { text })));

    /// <summary>Reads <paramref name="take"/> events of the stream, from after <paramref name="lastEventId"/> when given; fewer when the stream ends first.</summary>
    private static async Task<List<(string Id, string Event, JsonElement Data)>> Stream(HttpClient http, string id, int take, long? lastEventId = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/conversations/{id}/events");
        if (lastEventId is { } last)
            request.Headers.Add("Last-Event-ID", last.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        List<(string, string, JsonElement)> seen = [];
        string? eventId = null, kind = null, data = null;
        while (seen.Count < take && await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("id: ", StringComparison.Ordinal))
                eventId = line["id: ".Length..];
            else if (line.StartsWith("event: ", StringComparison.Ordinal))
                kind = line["event: ".Length..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
                data = line["data: ".Length..];
            else if (line.Length == 0 && kind is not null)
            {
                seen.Add((eventId!, kind, JsonDocument.Parse(data!).RootElement.Clone()));
                (eventId, kind, data) = (null, null, null);
            }
        }

        return seen;
    }

    [Fact]
    public async Task A_conversation_starts_with_201_and_a_location_and_reads_back()
    {
        await using var s = await Server();

        var res = await s.Http.PostAsync("/v1/conversations", StartBody());

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var started = await Body(res);
        Assert.Equal($"/v1/conversations/{started.GetProperty("id").GetString()}", res.Headers.Location!.ToString());
        Assert.Equal(("conversation/v1", "idle", "fake-1", "fake"), (started.GetProperty("contract_version").GetString(), started.GetProperty("state").GetString(), started.GetProperty("model").GetString(), started.GetProperty("runtime").GetString()));
        var read = await Body(await s.Http.GetAsync(res.Headers.Location));
        Assert.Equal(("acme/widgets", Commit, 200), (read.GetProperty("repository").GetProperty("path").GetString(), read.GetProperty("repository").GetProperty("commit").GetString(), read.GetProperty("caps").GetProperty("max_messages").GetInt32()));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Http.GetAsync("/v1/conversations/conv-none")).StatusCode);
    }

    [Fact]
    public async Task The_stream_carries_each_event_with_its_id_and_resumes_after_last_event_id_then_ends_with_the_conversation()
    {
        await using var s = await Server();
        var id = await StartedId(s.Http);
        var accepted = await Say(s.Http, id, "hello");

        var all = await Stream(s.Http, id, take: 8);

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(["1", "2", "3", "4", "5", "6", "7", "8"], all.Select(e => e.Id));
        Assert.Equal(["state", "state", "message_chunk", "state", "message_chunk", "message_chunk", "usage", "state"], all.Select(e => e.Event));
        Assert.Equal("starting", all[0].Data.GetProperty("data").GetProperty("state").GetString());
        Assert.Equal("user", all[2].Data.GetProperty("data").GetProperty("role").GetString());
        var resumed = await Stream(s.Http, id, take: 3, lastEventId: 5);
        Assert.Equal(["6", "7", "8"], resumed.Select(e => e.Id));

        var ended = await s.Http.DeleteAsync($"/v1/conversations/{id}");

        var endedBody = await Body(ended);
        Assert.Equal(("ended", "user"), (endedBody.GetProperty("state").GetString(), endedBody.GetProperty("ended_reason").GetString()));
        var tail = await Stream(s.Http, id, take: int.MaxValue, lastEventId: 8);
        Assert.Equal([("9", "ended")], tail.Select(e => (e.Id, e.Event)));
    }

    [Fact]
    public async Task A_message_is_accepted_with_202_and_a_bad_body_or_text_is_400()
    {
        await using var s = await Server();
        var id = await StartedId(s.Http);

        var accepted = await Body(await Say(s.Http, id, "hello"));

        Assert.Equal((id, false, 1), (accepted.GetProperty("conversation_id").GetString(), accepted.GetProperty("queued").GetBoolean(), accepted.GetProperty("messages").GetInt32()));
        Assert.Equal(HttpStatusCode.BadRequest, (await Say(s.Http, id, "  ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Http.PostAsync($"/v1/conversations/{id}/messages", Json("[1"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Http.PostAsync("/v1/conversations", Json("""{"repository":"","commit":"HEAD","runtime":"nope"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Say(s.Http, "conv-none", "hi")).StatusCode);
    }

    [Fact]
    public async Task An_approval_is_answered_once_unknown_is_404_and_a_second_answer_is_409()
    {
        await using var s = await Server();
        var id = await StartedId(s.Http);
        await Say(s.Http, id, "approve:edit");
        var asked = await Stream(s.Http, id, take: 6);
        var call = asked.Single(e => e.Event == "approval_requested").Data.GetProperty("data").GetProperty("call_id").GetString();

        Assert.Equal(HttpStatusCode.NotFound, (await s.Http.PostAsync($"/v1/conversations/{id}/approvals/call-9", Json("""{"decision":"allow_once"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Http.PostAsync($"/v1/conversations/{id}/approvals/{call}", Json("""{"decision":"always"}"""))).StatusCode);
        var answered = await s.Http.PostAsync($"/v1/conversations/{id}/approvals/{call}", Json("""{"decision":"allow_once"}"""));
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        var answeredBody = await Body(answered);
        Assert.Equal((call, "allow_once"), (answeredBody.GetProperty("call_id").GetString(), answeredBody.GetProperty("decision").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, (await s.Http.PostAsync($"/v1/conversations/{id}/approvals/{call}", Json("""{"decision":"reject"}"""))).StatusCode);
    }

    [Fact]
    public async Task A_rejected_approval_fails_the_tool_call_and_the_turn_goes_on()
    {
        await using var s = await Server();
        var id = await StartedId(s.Http);
        await Say(s.Http, id, "approve:edit");
        await Stream(s.Http, id, take: 6);

        var denied = await s.Http.PostAsync($"/v1/conversations/{id}/approvals/call-1", Json("""{"decision":"reject"}"""));

        Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
        var rest = await Stream(s.Http, id, take: 5, lastEventId: 6);
        Assert.Equal(["approval_answered", "tool_call_update", "message_chunk", "usage", "state"], rest.Select(e => e.Event));
        Assert.Equal("failed", rest[1].Data.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_account_reaches_its_own_conversations_only_and_a_call_without_a_key_is_401()
    {
        await using var s = await Server(accounts: [Account("alice"), Account("bob")]);
        using var alice = s.As(s.Keys.Create("alice", null).Key);
        using var bob = s.As(s.Keys.Create("bob", null).Key);
        var id = await StartedId(alice);

        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/v1/conversations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Http.GetAsync($"/v1/conversations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/v1/conversations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/v1/conversations/{id}/events")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Say(bob, id, "hi")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"/v1/conversations/{id}/approvals/call-1", Json("""{"decision":"reject"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/v1/conversations/{id}")).StatusCode);
        using var nobody = new HttpClient { BaseAddress = s.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await nobody.PostAsync("/v1/conversations", StartBody())).StatusCode);
    }

    [Fact]
    public async Task A_run_token_opens_no_conversation_route()
    {
        await using var s = await Server();
        var token = s.Tokens.Issue(new RunTokenClaims("run-parent", "acme/widgets", Commit, null, null, DateTimeOffset.UtcNow.AddHours(1)), DateTimeOffset.UtcNow);
        using var session = s.As(token);

        Assert.Equal(HttpStatusCode.Forbidden, (await session.PostAsync("/v1/conversations", StartBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await session.GetAsync("/v1/conversations/conv-1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await session.GetAsync("/v1/conversations/conv-1/events")).StatusCode);
    }

    [Fact]
    public async Task A_message_over_the_conversation_s_cap_is_429_with_the_body_of_a_refused_run()
    {
        await using var s = await Server(settings: new ConversationSettings(MaxMessages: 1));
        var id = await StartedId(s.Http);
        await Say(s.Http, id, "first");

        var refused = await Say(s.Http, id, "second");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        var body = await Body(refused);
        Assert.Equal("conversation_cap_exceeded", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("1 messages", body.GetProperty("errors")[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_runtime_that_does_not_start_is_502_and_names_the_failed_conversation()
    {
        await using var s = await Server(runtime: new FakeConversationRuntime { StartFailure = new InvalidOperationException("no agent on this host") });

        var res = await s.Http.PostAsync("/v1/conversations", StartBody());

        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        var id = (await Body(res)).GetProperty("conversation_id").GetString();
        var failed = await Body(await s.Http.GetAsync($"/v1/conversations/{id}"));
        Assert.Equal(("failed", "error"), (failed.GetProperty("state").GetString(), failed.GetProperty("ended_reason").GetString()));
    }

    [Fact]
    public async Task A_conversation_open_when_the_server_stopped_is_lost_when_the_next_one_starts()
    {
        var opened = new[]
        {
            ConversationJson.ToEvent(1, DateTimeOffset.UtcNow, new StateData(ConversationState.Starting, Repository: new("acme/widgets", Commit), Runtime: "fake")),
            ConversationJson.ToEvent(2, DateTimeOffset.UtcNow, new StateData(ConversationState.Idle)),
        };
        await using var s = await Server(before: dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "conv-earlier-1.jsonl"), opened.Select(e => JsonSerializer.Serialize(e, ConversationJson.Event)));
        });

        var read = await Body(await s.Http.GetAsync("/v1/conversations/conv-earlier-1"));

        Assert.Equal(("lost", "lost", 3), (read.GetProperty("state").GetString(), read.GetProperty("ended_reason").GetString(), read.GetProperty("last_event_id").GetInt32()));
    }

    [Fact]
    public async Task The_routes_are_absent_unless_the_profile_turns_conversations_on()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));

        Assert.Equal(HttpStatusCode.NotFound, (await s.Http.PostAsync("/v1/conversations", StartBody())).StatusCode);
        Assert.DoesNotContain(s.Routes, r => r.Pattern.StartsWith("/v1/conversations", StringComparison.Ordinal));
        await using var on = await Server();
        Assert.Contains(("POST", "/v1/conversations/{id}/approvals/{call}"), on.Routes);
    }
}
