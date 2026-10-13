using System.Net;
using System.Text;
using System.Text.Json;
using Chargehand.Bench;
using Chargehand.Contracts;

namespace Chargehand.Tests;

/// <summary>The benchmark talks to a server the way any caller does; these pin the requests it makes and how it follows a run that drops its connection.</summary>
public class BenchServerTests
{
    private sealed class Handler(params Func<HttpRequestMessage, HttpResponseMessage>[] replies) : HttpMessageHandler
    {
        private int _next;
        public List<(HttpMethod Method, string Path, string? Prefer, string? Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.TryGetValues("Prefer", out var p) ? string.Join(',', p) : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return replies[Math.Min(_next++, replies.Length - 1)](request);
        }
    }

    private static BenchServer Server(Handler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://chargehand.test") }, TimeSpan.Zero, TimeSpan.Zero);

    private static RunRequest Request() =>
        new("request/v1", "bench", new RequestContext(false, "driven", Repository: new RepositoryRef("/r", "abc")), Driven: new RequestDriven([new DrivenTask("t", Goal: "g")]));

    private static string Frame(RunStatus e) => $"event: x\ndata: {JsonSerializer.Serialize(e, ContractJson.Options)}\n\n";

    private static HttpResponseMessage Stream(params RunStatus[] events) =>
        new(HttpStatusCode.OK) { Content = new StringContent(string.Concat(events.Select(Frame)), Encoding.UTF8, "text/event-stream") };

    private static RunStatus Progress(long tokens) => RunStatus.Of("b1", RunState.Running, RunEventKind.SessionProgress) with { TaskId = "t", Tokens = tokens };

    private static ResultContract Done() =>
        new("result/v1", "b1", "driven", "trace", new PromptChain([], new AsSent("c", "driven", "batch", "2026-10-13")), ResultStatus.Completed, "done", [], [], [], [], 1, new Usage(0, 0, 0, 0, null));

    private static RunStatus Finished() => RunStatus.Of("b1", RunState.Completed, RunEventKind.RunFinished) with { Result = Done() };

    [Fact]
    public async Task A_batch_is_posted_as_request_v1_and_its_id_comes_from_the_location_header()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Accepted) { Headers = { Location = new Uri("/v1/runs/b-42", UriKind.Relative) } });
        Assert.Equal("b-42", await Server(handler).SubmitAsync(Request(), default));
        var (method, path, prefer, body) = Assert.Single(handler.Seen);
        Assert.Equal((HttpMethod.Post, "/v1/runs", "wait=1"), (method, path, prefer));
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("request/v1", doc.RootElement.GetProperty("contract_version").GetString());
        Assert.Equal("g", doc.RootElement.GetProperty("driven").GetProperty("tasks")[0].GetProperty("goal").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "refused the API key")]
    [InlineData(HttpStatusCode.Forbidden, "refused the API key")]
    [InlineData(HttpStatusCode.BadRequest, "refused the request: {\"errors\":[\"nope\"]}")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "cannot take a batch now")]
    [InlineData(HttpStatusCode.TooManyRequests, "cannot take a batch now")]
    [InlineData(HttpStatusCode.InternalServerError, "answered HTTP 500")]
    public async Task A_refusal_says_what_the_server_said(HttpStatusCode status, string expected)
    {
        var handler = new Handler(_ => new HttpResponseMessage(status) { Content = new StringContent("{\"errors\":[\"nope\"]}") });
        var e = await Assert.ThrowsAsync<BenchException>(() => Server(handler).SubmitAsync(Request(), default));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public async Task A_reply_with_no_run_id_is_an_error()
    {
        var e = await Assert.ThrowsAsync<BenchException>(() => Server(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Accepted))).SubmitAsync(Request(), default));
        Assert.Contains("named no run id", e.Message);
    }

    [Fact]
    public async Task Following_a_run_hands_every_event_over_and_returns_the_result()
    {
        var seen = new List<RunStatus>();
        var (events, result) = await Server(new Handler(_ => Stream(Progress(10), Progress(20), Finished()))).FollowAsync("b1", seen.Add, default);
        Assert.Equal(3, events.Count);
        Assert.Equal(3, seen.Count);
        Assert.Equal("done", result.Summary);
    }

    [Fact]
    public async Task A_dropped_connection_resumes_without_repeating_events()
    {
        var handler = new Handler(_ => Stream(Progress(10), Progress(20)), _ => Stream(Progress(10), Progress(20), Progress(30), Finished()));
        var (events, _) = await Server(handler).FollowAsync("b1", null, default);
        Assert.Equal([10L, 20L, 30L], events.Where(e => e.Tokens is not null).Select(e => e.Tokens!.Value));
        Assert.Equal(2, handler.Seen.Count);
    }

    [Fact]
    public async Task When_the_events_cannot_be_had_the_result_is_polled()
    {
        var handler = new Handler(
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway), _ => new HttpResponseMessage(HttpStatusCode.BadGateway), _ => new HttpResponseMessage(HttpStatusCode.BadGateway),
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway), _ => new HttpResponseMessage(HttpStatusCode.BadGateway),
            _ => new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("{}") },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(Done(), ContractJson.Options), Encoding.UTF8, "application/json") });
        var (events, result) = await Server(handler).FollowAsync("b1", null, default);
        Assert.Empty(events);
        Assert.Equal("done", result.Summary);
    }

    [Fact]
    public async Task A_lost_run_or_a_wrong_key_stops_the_follow()
    {
        var lost = new Handler(_ => Stream(RunStatus.Of("b1", RunState.Lost, RunEventKind.RunFinished) with { Detail = "the process ended" }));
        Assert.Contains("was lost: the process ended", (await Assert.ThrowsAsync<BenchException>(() => Server(lost).FollowAsync("b1", null, default))).Message);
        var denied = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        Assert.Contains("refused the API key", (await Assert.ThrowsAsync<BenchException>(() => Server(denied).FollowAsync("b1", null, default))).Message);
        var unknown = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Contains("no run b1", (await Assert.ThrowsAsync<BenchException>(() => Server(unknown).FollowAsync("b1", null, default))).Message);
    }

    [Fact]
    public async Task A_finished_task_result_is_read_by_its_run_id_and_a_missing_one_is_null()
    {
        var found = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(Done(), ContractJson.Options), Encoding.UTF8, "application/json") });
        Assert.Equal("done", (await Server(found).GetResultAsync("run/1", default))!.Summary);
        Assert.StartsWith("/v1/runs/run", found.Seen[0].Path);
        Assert.Null(await Server(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))).GetResultAsync("x", default));
        await Assert.ThrowsAsync<BenchException>(() => Server(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Gone))).GetResultAsync("x", default));
    }
}
