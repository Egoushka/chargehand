using System.Net;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Watch;

namespace Chargehand.Tests;

/// <summary><c>chargehand watch</c> is what a Claude Code session runs under its Monitor tool: every line on stdout is one notification, so what a line says
/// and what the exit code means are pinned here.</summary>
public class WatchCliTests
{
    private const string Run = "batch-1";

    private sealed class Server(params Func<HttpRequestMessage, HttpResponseMessage>[] replies) : HttpMessageHandler
    {
        private int _next;
        public List<(HttpMethod Method, string Path, string? Auth)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString()));
            return Task.FromResult(replies[Math.Min(_next++, replies.Length - 1)](request));
        }
    }

    private sealed class Stepping(TimeSpan step) : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now += step;
    }

    private static HttpClient Client(Server server) => new(server) { BaseAddress = new Uri("http://chargehand.test"), DefaultRequestHeaders = { Authorization = new("Bearer", "k") } };

    private static string Frame(RunStatus e) =>
        $"event: {JsonNamingPolicy.SnakeCaseLower.ConvertName((e.Event ?? RunEventKind.Started).ToString())}\ndata: {JsonSerializer.Serialize(e, ContractJson.Options)}\n\n";

    private static Func<HttpRequestMessage, HttpResponseMessage> Stream(params RunStatus[] events) => _ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Concat(events.Select(Frame)), Encoding.UTF8, "text/event-stream"),
    };

    private static RunStatus Event(RunEventKind kind, string? detail = null, string? task = null) =>
        RunStatus.Of(Run, RunState.Running, kind) with { Detail = detail, TaskId = task };

    private static RunStatus Progress(string stage, long tokens = 100) =>
        Event(RunEventKind.SessionProgress, $"task t1: {tokens} tokens, stage {stage}", "t1") with { Tokens = tokens, Stage = Enum.Parse<SessionStage>(stage, true) };

    private static RunStatus Finished(ResultStatus status, string summary, ResultError? error = null)
    {
        var result = new ResultContract("result/v1", Run, "driven", "trace", new PromptChain([], new AsSent("claude-code/1", "driven", "batch", "2026-10-10")), status, summary,
            [], [], [], [], 0, new Usage(0, 0, 0, 0, 0), error);
        return RunStatus.Of(Run, RunStatus.StateOf(status), RunEventKind.RunFinished) with { Result = result };
    }

    private static async Task<(int Exit, string Out, string Err, Server Server)> Watch(Server server, TimeProvider? time = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await WatchCli.WatchAsync(Client(server), Run, output, error, default, time, TimeSpan.Zero);
        return (exit, output.ToString(), error.ToString(), server);
    }

    [Fact]
    public async Task A_completed_run_prints_one_line_per_event_and_exits_0()
    {
        var (exit, output, error, server) = await Watch(new Server(Stream(
            Event(RunEventKind.Started),
            Event(RunEventKind.ContainerStarted, "task t1: session container started", "t1"),
            Event(RunEventKind.VerifyFinished, "task t1: tests passed", "t1"),
            Event(RunEventKind.Pushed, "task t1: pushed chargehand/r1", "t1") with { Branch = "chargehand/r1" },
            Event(RunEventKind.PrOpened, "task t1: draft pull request opened", "t1") with { PrUrl = "https://example.test/o/r/pull/7" },
            Finished(ResultStatus.Completed, "1 of 1 tasks ended in a draft pull request"))));

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        Assert.Equal(
            ["started",
             "container_started: task t1: session container started",
             "verify_finished: task t1: tests passed",
             "pushed: task t1: pushed chargehand/r1",
             "pr_opened: task t1: draft pull request opened https://example.test/o/r/pull/7",
             "run_finished: completed: 1 of 1 tasks ended in a draft pull request"],
            output.TrimEnd().Split('\n'));
        Assert.Equal([(HttpMethod.Get, "/v1/runs/batch-1/events", "Bearer k")], server.Seen);
    }

    [Fact]
    public async Task A_failed_run_says_why_on_stdout_and_exits_1()
    {
        var error = new ResultError(ErrorCode.Cancelled, "the run was cancelled", false, "Start the batch again if it is still wanted; nothing was pushed.");
        var (exit, output, stderr, _) = await Watch(new Server(Stream(Finished(ResultStatus.Failed, "the run was cancelled", error))));

        Assert.Equal(1, exit);
        Assert.Equal("", stderr);                      // a Monitor sees stdout only
        Assert.Contains("run_finished: failed: the run was cancelled", output);
        Assert.Contains("Start the batch again", output);
    }

    [Theory]
    [InlineData(ResultStatus.NeedsInput, 3)]
    [InlineData(ResultStatus.Denied, 1)]
    public async Task The_exit_code_follows_the_result_status(ResultStatus status, int expected) =>
        Assert.Equal(expected, (await Watch(new Server(Stream(Finished(status, "x"))))).Exit);

    [Fact]
    public async Task A_run_whose_process_died_is_lost_and_exits_1()
    {
        var lost = RunStatus.Of(Run, RunState.Lost) with { Detail = "the server that ran it stopped" };
        var (exit, output, _, _) = await Watch(new Server(Stream(lost)));

        Assert.Equal(1, exit);
        Assert.Contains("lost: the server that ran it stopped", output);
    }

    [Fact]
    public async Task A_dropped_connection_resumes_without_repeating_lines()
    {
        var first = new[] { Event(RunEventKind.Started), Event(RunEventKind.ContainerStarted, "task t1: session container started", "t1") };
        var second = first.Append(Progress("write")).Append(Finished(ResultStatus.Completed, "done")).ToArray();
        var (exit, output, _, server) = await Watch(new Server(Stream(first), Stream(second)));

        Assert.Equal(0, exit);
        Assert.Equal(["started", "container_started: task t1: session container started", "session_progress: task t1: 100 tokens, stage write", "run_finished: completed: done"],
            output.TrimEnd().Split('\n'));
        Assert.Equal(2, server.Seen.Count);
    }

    [Fact]
    public async Task A_server_that_stays_away_ends_with_the_run_still_going()
    {
        var (exit, output, _, server) = await Watch(new Server(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)));

        Assert.Equal(1, exit);
        Assert.Contains("is not cancelled", output);
        Assert.Contains("chargehand watch batch-1", output);
        Assert.Equal(WatchCli.Attempts, server.Seen.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "API key")]
    [InlineData(HttpStatusCode.NotFound, "no run batch-1")]
    public async Task A_refusal_is_not_retried_and_exits_2(HttpStatusCode status, string message)
    {
        var (exit, _, error, server) = await Watch(new Server(_ => new HttpResponseMessage(status)));

        Assert.Equal(2, exit);
        Assert.Contains(message, error);
        Assert.Single(server.Seen);
    }

    [Fact]
    public async Task Progress_is_thinned_to_one_line_per_interval_unless_the_stage_changes()
    {
        // Each reading of the clock moves it 10 s: of three polls on the same stage only the first is printed, and a new stage always is.
        var (_, output, _, _) = await Watch(new Server(Stream(Progress("write", 1), Progress("write", 2), Progress("write", 3), Progress("test", 4), Finished(ResultStatus.Completed, "done"))),
            new Stepping(TimeSpan.FromSeconds(10)));

        Assert.Equal(["session_progress: task t1: 1 tokens, stage write", "session_progress: task t1: 4 tokens, stage test", "run_finished: completed: done"],
            output.TrimEnd().Split('\n'));
    }

    [Fact]
    public async Task Cancel_posts_and_says_the_run_is_cancelling()
    {
        var server = new Server(_ => new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("""{"run_id":"batch-1","status":"cancelling"}""") });
        var output = new StringWriter();

        Assert.Equal(0, await WatchCli.CancelAsync(Client(server), Run, output, TextWriter.Null, default));
        Assert.Equal([(HttpMethod.Post, "/v1/runs/batch-1/cancel", "Bearer k")], server.Seen);
        Assert.Contains("cancelling", output.ToString());
    }

    [Fact]
    public async Task Cancel_of_an_unknown_run_exits_2()
    {
        var error = new StringWriter();
        Assert.Equal(2, await WatchCli.CancelAsync(Client(new Server(_ => new HttpResponseMessage(HttpStatusCode.NotFound))), Run, TextWriter.Null, error, default));
        Assert.Contains("no run batch-1", error.ToString());
    }

    [Fact]
    public void The_server_and_key_come_from_the_flag_and_the_environment_before_the_profile()
    {
        var env = new Dictionary<string, string> { ["CHARGEHAND_API_KEY"] = "env-key" };
        var (client, problem) = WatchCli.Connect(["--url", "https://mcp.example/"], env.GetValueOrDefault, () => throw new InvalidOperationException("profile must not be read"));

        Assert.Null(problem);
        Assert.Equal("https://mcp.example/", client!.BaseAddress!.ToString());
        Assert.Equal("Bearer env-key", client.DefaultRequestHeaders.Authorization!.ToString());
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Fact]
    public void The_profile_fills_what_the_flag_and_environment_leave_out()
    {
        var (client, problem) = WatchCli.Connect([], _ => null, () => ("http://127.0.0.1:4300", "profile-key"));

        Assert.Null(problem);
        Assert.Equal("http://127.0.0.1:4300/", client!.BaseAddress!.ToString());
        Assert.Equal("Bearer profile-key", client.DefaultRequestHeaders.Authorization!.ToString());
    }

    [Theory]
    [InlineData("--url", "ftp://x")]
    [InlineData("--url", "not a url")]
    public void A_url_that_is_not_http_is_refused(string flag, string value)
    {
        var (client, problem) = WatchCli.Connect([flag, value], _ => "k", () => null);

        Assert.Null(client);
        Assert.Contains("http", problem);
    }

    [Fact]
    public void Without_any_source_the_problem_names_the_ways_to_give_them()
    {
        var (client, problem) = WatchCli.Connect([], _ => null, () => null);

        Assert.Null(client);
        Assert.Contains("--url", problem);
        Assert.Contains("CHARGEHAND_API_KEY", problem);
    }

    [Fact]
    public async Task Watch_follows_a_real_servers_run_from_accepted_to_its_result_and_reads_a_finished_one_again()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Hold = new() };
        await using var s = await TestServer.StartAsync(runtime);
        var id = (await s.PostAsync(Runs.DraftRequest(), waitSeconds: 0)).Headers.Location!.OriginalString.Split('/')[^1];
        runtime.Hold.SetResult();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var output = new StringWriter();
        Assert.Equal(0, await WatchCli.WatchAsync(s.Http, id, output, TextWriter.Null, timeout.Token));
        var lines = output.ToString().TrimEnd().Split('\n');
        Assert.Equal("accepted", lines[0]);
        Assert.StartsWith("run_finished: completed: ", lines[^1]);

        var again = new StringWriter();
        Assert.Equal(0, await WatchCli.WatchAsync(s.Http, id, again, TextWriter.Null, timeout.Token));
        Assert.StartsWith("run_finished: completed: ", again.ToString());
    }
}
