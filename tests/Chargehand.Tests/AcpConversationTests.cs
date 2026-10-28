using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chargehand.Acp;
using Chargehand.Modules.Conversations.Api;
using Chargehand.Runtimes.Acp.Api;
using Chargehand.Sandbox;

namespace Chargehand.Tests;

/// <summary>A conversation over <c>/v1/conversations</c> on the ACP runtime (ADR 0086, Cycle A task 5), with the scripted fake agent as the agent: a real child process speaking ACP on
/// stdio, started the way Codex or Gemini CLI are. The first word of a message picks what the agent does (tests/AgentClientProtocol.FakeAgent).</summary>
public sealed class AcpConversationTests : IAsyncLifetime
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private readonly List<TempDir> _checkouts = [];
    private readonly List<TestServer> _servers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var server in _servers)
            await server.DisposeAsync();
        foreach (var checkout in _checkouts)
            checkout.Dispose();
    }

    private static AcpConversationRuntime Runtime(string checkout, Action<string?>? preset = null)
    {
        var agent = AcpTestSupport.Fake();
        var catalog = AcpAgentCatalog.Parse(JsonSerializer.Serialize(new
        {
            schema = "acp-agents/v1",
            agents = new[]
            {
                new { id = agent.Id, name = agent.Name, command = agent.Command, args = agent.Args, version = agent.Version, api_key_env = agent.ApiKeyEnv, auth_method = agent.AuthMethod, reports_usage = true, provider = agent.Provider },
            },
        }), "test");
        var settings = new AcpSettings(Agents: new Dictionary<string, AcpAgentCredential> { [agent.Id] = new(ApiKeySecret: "fake-key") });
        return new AcpConversationRuntime(catalog, settings, _ => "k-test", (_, name, _) =>
        {
            preset?.Invoke(name);
            return Task.FromResult(checkout);
        }, new AcpSandboxLauncher(new NoSandbox()));
    }

    private async Task<TestServer> Server(Action<string?>? preset = null)
    {
        _checkouts.Add(new TempDir());
        var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), tweak: (root, s) =>
            s with { Conversations = new ConversationSettings("acp"), ConversationsDirectory = Path.Combine(root, "conversations"), ConversationRuntime = Runtime(_checkouts[^1].Path, preset) });
        _servers.Add(server);
        return server;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static Task<HttpResponseMessage> Start(HttpClient http, string runtime = "acp:fake", string? preset = null) =>
        http.PostAsync("/v1/conversations", Json(JsonSerializer.Serialize(new { repository = "acme/widgets", commit = Commit, runtime, preset })));

    private static async Task<string> Started(HttpClient http)
    {
        var res = await Start(http);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static Task<HttpResponseMessage> Say(HttpClient http, string id, string text) => http.PostAsync($"/v1/conversations/{id}/messages", Json(JsonSerializer.Serialize(new { text })));

    private static Task<HttpResponseMessage> Answer(HttpClient http, string id, string call, string decision) =>
        http.PostAsync($"/v1/conversations/{id}/approvals/{call}", Json(JsonSerializer.Serialize(new { decision })));

    private sealed record Seen(long Id, string Type, JsonElement Data)
    {
        public string? Text => Type == "message_chunk" && Data.GetProperty("role").GetString() == "assistant" ? Data.GetProperty("text").GetString() : null;

        public bool IdleAfterTurn => Type == "state" && Data.GetProperty("state").GetString() == "idle" && Data.TryGetProperty("stop_reason", out _);
    }

    /// <summary>Reads the stream after <paramref name="after"/> until <paramref name="until"/> holds for an event, or the conversation is over.</summary>
    private static async Task<List<Seen>> Stream(HttpClient http, string id, long after, Func<Seen, bool> until)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/conversations/{id}/events");
        request.Headers.Add("Last-Event-ID", after.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        List<Seen> seen = [];
        string? eventId = null, kind = null, data = null;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("id: ", StringComparison.Ordinal))
                eventId = line["id: ".Length..];
            else if (line.StartsWith("event: ", StringComparison.Ordinal))
                kind = line["event: ".Length..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
                data = line["data: ".Length..];
            else if (line.Length == 0 && kind is not null)
            {
                var one = new Seen(long.Parse(eventId!, System.Globalization.CultureInfo.InvariantCulture), kind, JsonDocument.Parse(data!).RootElement.GetProperty("data").Clone());
                seen.Add(one);
                (eventId, kind, data) = (null, null, null);
                if (until(one))
                    break;
            }
        }

        return seen;
    }

    private static async Task<List<Seen>> Turn(HttpClient http, string id, string text, long after)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await Say(http, id, text)).StatusCode);
        return await Stream(http, id, after, e => e.IdleAfterTurn);
    }

    private static string Said(IEnumerable<Seen> events) => string.Concat(events.Select(e => e.Text));

    [Fact]
    public async Task A_conversation_starts_streams_takes_a_second_message_asks_and_is_answered_both_ways_and_ends()
    {
        List<string?> presets = [];
        var s = await Server(presets.Add);
        var started = await Start(s.Http, preset: "cheap");
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var view = await started.Content.ReadFromJsonAsync<JsonElement>();
        var id = view.GetProperty("id").GetString()!;
        Assert.Equal(("idle", "acp:fake", "fast"), (view.GetProperty("state").GetString(), view.GetProperty("runtime").GetString(), view.GetProperty("model").GetString()));
        Assert.Equal(["cheap"], presets);

        var first = await Turn(s.Http, id, "say hello to the new world", after: 0);
        Assert.Equal("hello to the new world", Said(first));
        Assert.Equal(["state", "state", "message_chunk", "state", "message_chunk", "message_chunk", "message_chunk", "state"], first.Select(e => e.Type));

        var second = await Turn(s.Http, id, "tool", after: first[^1].Id);
        var call = second.Single(e => e.Type == "tool_call").Data;
        Assert.Equal(("read", "src/a.cs", 12), (call.GetProperty("kind").GetString(), call.GetProperty("locations")[0].GetProperty("path").GetString(), call.GetProperty("locations")[0].GetProperty("line").GetInt32()));
        Assert.Equal("completed", second.Single(e => e.Type == "tool_call_update").Data.GetProperty("status").GetString());

        var third = await Say(s.Http, id, "ask execute");
        Assert.Equal(HttpStatusCode.Accepted, third.StatusCode);
        var asked = await Stream(s.Http, id, second[^1].Id, e => e.Type == "approval_requested");
        var approval = asked[^1].Data;
        Assert.Equal(("t2", "execute"), (approval.GetProperty("call_id").GetString(), approval.GetProperty("kind").GetString()));
        Assert.Equal(["t2"], (await Read(s.Http, id)).GetProperty("pending_approvals").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(HttpStatusCode.OK, (await Answer(s.Http, id, "t2", "allow_once")).StatusCode);
        var allowed = await Stream(s.Http, id, asked[^1].Id, e => e.IdleAfterTurn);
        Assert.Equal("permission:selected:allow-once", Said(allowed));
        Assert.Contains(allowed, e => e.Type == "approval_answered");

        await Say(s.Http, id, "ask edit");
        var again = await Stream(s.Http, id, allowed[^1].Id, e => e.Type == "approval_requested");
        Assert.Equal(HttpStatusCode.OK, (await Answer(s.Http, id, "t3", "reject")).StatusCode);
        var denied = await Stream(s.Http, id, again[^1].Id, e => e.IdleAfterTurn);
        Assert.Equal("permission:selected:reject", Said(denied));

        var reading = await Turn(s.Http, id, "ask read", after: denied[^1].Id);
        Assert.Equal("permission:selected:allow-once", Said(reading));
        Assert.DoesNotContain(reading, e => e.Type == "approval_requested");

        var usage = await Turn(s.Http, id, "usage", after: reading[^1].Id);
        var used = usage.Single(e => e.Type == "usage").Data;
        Assert.Equal((10, 5, 0.01m), (used.GetProperty("input_tokens").GetInt32(), used.GetProperty("output_tokens").GetInt32(), used.GetProperty("usd").GetDecimal()));

        var read = await Read(s.Http, id);
        Assert.Equal(("idle", 6, 10, 5), (read.GetProperty("state").GetString(), read.GetProperty("messages").GetInt32(), read.GetProperty("usage").GetProperty("input_tokens").GetInt32(), read.GetProperty("usage").GetProperty("output_tokens").GetInt32()));
        var ended = await s.Http.DeleteAsync($"/v1/conversations/{id}");
        Assert.Equal(("ended", "user"), ((await Body(ended)).GetProperty("state").GetString(), (await Read(s.Http, id)).GetProperty("ended_reason").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, (await Say(s.Http, id, "say too late")).StatusCode);
    }

    private static async Task<JsonElement> Read(HttpClient http, string id) => await Body(await http.GetAsync($"/v1/conversations/{id}"));

    private static async Task<JsonElement> Body(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Ending_a_conversation_whose_turn_is_running_cancels_it_and_stops_the_agent()
    {
        var s = await Server();
        var id = await Started(s.Http);
        await Say(s.Http, id, "slow");
        var working = await Stream(s.Http, id, 0, e => e.Text == "working");

        var started = DateTimeOffset.UtcNow;
        var ended = await s.Http.DeleteAsync($"/v1/conversations/{id}");

        Assert.Equal("ended", (await Body(ended)).GetProperty("state").GetString());
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(8), "ending waited for the agent's cancel grace");
        var tail = await Stream(s.Http, id, working[^1].Id, _ => false);
        Assert.Equal("ended", tail[^1].Type);
        Assert.Equal("user", tail[^1].Data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_turn_the_agent_refuses_ends_with_its_stop_reason_and_the_conversation_goes_on()
    {
        var s = await Server();
        var id = await Started(s.Http);
        await Say(s.Http, id, "nonsense");

        var turn = await Stream(s.Http, id, 0, e => e.IdleAfterTurn);

        Assert.Equal("unknown command: nonsense", Said(turn));
        Assert.Equal("refusal", turn[^1].Data.GetProperty("stop_reason").GetString());
        Assert.Equal("idle", (await Read(s.Http, id)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task An_agent_that_dies_mid_turn_ends_the_conversation_with_its_own_last_words()
    {
        var s = await Server();
        var id = await Started(s.Http);
        await Say(s.Http, id, "die");

        var events = await Stream(s.Http, id, 0, e => e.Type == "ended");

        var error = events.Single(e => e.Type == "error").Data;
        Assert.True(error.GetProperty("fatal").GetBoolean());
        Assert.Contains("boom: the fake agent crashed", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("error", events[^1].Data.GetProperty("reason").GetString());
        Assert.Equal("failed", (await Read(s.Http, id)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_key_the_provider_rejects_fails_the_turn_and_pauses_new_conversations_on_that_agent()
    {
        var s = await Server();
        var id = await Started(s.Http);
        await Say(s.Http, id, "authfail");

        var turn = await Stream(s.Http, id, 0, e => e.IdleAfterTurn);

        Assert.Contains("authentication_error", turn.Single(e => e.Type == "error").Data.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("error", turn[^1].Data.GetProperty("stop_reason").GetString());
        var refused = await Start(s.Http);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal("credential_paused", (await Body(refused)).GetProperty("error").GetProperty("code").GetString());
        Assert.NotNull(s.Service.Credentials.Paused("acp:fake"));
    }

    [Fact]
    public async Task A_runtime_the_profile_gave_no_key_is_refused_before_anything_starts()
    {
        var s = await Server();

        Assert.Equal(HttpStatusCode.BadRequest, (await Start(s.Http, "acp:gemini")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Start(s.Http, "fake")).StatusCode);
    }
}
