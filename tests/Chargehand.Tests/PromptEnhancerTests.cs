using System.Text.Json;
using Chargehand.Config;
using Chargehand.Enhancement;
using Chargehand.Mcp;
using ModelContextProtocol.Protocol;

namespace Chargehand.Tests;

/// <summary>ADR 0040: a prompt enhancer behind the guard. The user's prompt is never worse off for asking.</summary>
public class PromptEnhancerTests
{
    private const string Prompt = "review my diff";
    private const string Servers = """{"gw":{"url":"https://mcp.example.internal/mcp"}}""";
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(200);

    private static readonly EnhanceContext Context = new("example/app", "abc1234", "review");

    private static Profile ProfileWith(string enhancer, string servers = Servers)
    {
        using var dir = new TempDir();
        return Profile.Load(dir.Write("p.json", $$"""{"schema":"profile/v1","mcp_servers":{{servers}},"prompt_enhancer":{{enhancer}}}"""));
    }

    private static McpConnectionPool PoolFor(Profile profile, FakeMcpServer server) =>
        new(profile.McpServers!, _ => "", async (_, _, _) => await server.TransportAsync());

    private static CallToolResult Answer(string json) =>
        new() { StructuredContent = JsonDocument.Parse(json).RootElement.Clone(), Content = [new TextContentBlock { Text = json }] };

    private const string PassThrough = """{"prompt":"review my diff","changed":false,"template_id":null,"template_version":null,"reason":"no similar past prompt","task_kind":null,"request_id":"req-1","held_out":false}""";

    private const string Rewrite = """{"prompt":"Review the diff against main; bugs first, each with file:line.","changed":true,"template_id":"tpl-review","template_version":"1.2.0","reason":"accepted 11 of 14 times","task_kind":"review","request_id":"req-2","held_out":false}""";

    private static FakeMcpServer Whetstone(string enhanceAnswer) => new(
        new FakeTool("enhance", _ => Answer(enhanceAnswer)),
        new FakeTool("feedback", _ => Answer("{}")));

    /// <summary>An enhancer scripted per test; the real one is whetstone, and the seam is the same.</summary>
    private sealed class Scripted(Func<string, CancellationToken, Task<Enhanced>> enhance, Func<CancellationToken, Task>? feedback = null) : IPromptEnhancer
    {
        public Task<Enhanced> EnhanceAsync(string prompt, EnhanceContext context, CancellationToken ct) => enhance(prompt, ct);

        public Task FeedbackAsync(string requestId, EnhanceOutcome outcome, CancellationToken ct) => (feedback ?? (_ => Task.CompletedTask))(ct);
    }

    private static Scripted Fake(string? rewrite) =>
        new((p, _) => Task.FromResult(new Enhanced(p, rewrite, "fake", "req-fake", rewrite is null ? null : "tpl", rewrite is null ? null : "1.0.0")));

    [Fact]
    public async Task A_pass_through_has_no_rewrite_so_a_client_shows_no_diff()
    {
        var enhanced = await new GuardedPromptEnhancer(Fake(null), Deadline).EnhanceAsync(Prompt, Context, CancellationToken.None);

        Assert.False(enhanced.Changed);
        Assert.Null(enhanced.Rewrite);
        Assert.Equal(Prompt, enhanced.Original);
    }

    [Fact]
    public async Task A_rewrite_is_offered_beside_the_original()
    {
        var enhanced = await new GuardedPromptEnhancer(Fake("Review the diff against main."), Deadline).EnhanceAsync(Prompt, Context, CancellationToken.None);

        Assert.True(enhanced.Changed);
        Assert.Equal("Review the diff against main.", enhanced.Rewrite);
        Assert.Equal(Prompt, enhanced.Original);
        Assert.Equal("tpl", enhanced.TemplateId);
    }

    [Fact]
    public async Task A_late_enhancer_yields_the_original_at_the_deadline()
    {
        var never = new TaskCompletionSource<Enhanced>();
        var time = new ManualTimeProvider();
        var call = new GuardedPromptEnhancer(new Scripted((_, _) => never.Task), Deadline, time).EnhanceAsync(Prompt, Context, CancellationToken.None);

        Assert.False(call.IsCompleted);
        time.Advance(Deadline);
        var enhanced = await call;

        Assert.False(enhanced.Changed);
        Assert.Equal(Prompt, enhanced.Original);
        Assert.Contains("in time", enhanced.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blocking_enhancer_yields_the_original_at_the_deadline()
    {
        using var release = new ManualResetEventSlim();
        var blocking = new Scripted((p, _) =>
        {
            release.Wait(CancellationToken.None);
            return Task.FromResult(new Enhanced(p, "too late", "late"));
        });
        var time = new ManualTimeProvider();
        var call = new GuardedPromptEnhancer(blocking, Deadline, time).EnhanceAsync(Prompt, Context, CancellationToken.None);

        time.Advance(Deadline);
        var enhanced = await call;
        release.Set();

        Assert.False(enhanced.Changed);
        Assert.Contains("in time", enhanced.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_answer_before_the_deadline_is_kept()
    {
        var time = new ManualTimeProvider();
        var call = new GuardedPromptEnhancer(Fake("Review the diff against main."), Deadline, time).EnhanceAsync(Prompt, Context, CancellationToken.None);

        time.Advance(Deadline - TimeSpan.FromMilliseconds(1));

        Assert.True((await call).Changed);
    }

    [Fact]
    public async Task A_failing_enhancer_yields_the_original_and_its_message_stays_out_of_the_reason()
    {
        var failing = new Scripted((p, _) => throw new InvalidOperationException($"could not process '{p}'"));

        var enhanced = await new GuardedPromptEnhancer(failing, Deadline).EnhanceAsync(Prompt, Context, CancellationToken.None);

        Assert.False(enhanced.Changed);
        Assert.Equal(Prompt, enhanced.Original);
        Assert.Contains(nameof(InvalidOperationException), enhanced.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(Prompt, enhanced.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(Prompt)]
    public async Task An_empty_or_identical_rewrite_is_no_rewrite(string rewrite)
    {
        var enhanced = await new GuardedPromptEnhancer(Fake(rewrite), Deadline).EnhanceAsync(Prompt, Context, CancellationToken.None);

        Assert.False(enhanced.Changed);
        Assert.Equal(Prompt, enhanced.Original);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_swallowed()
    {
        using var cancel = new CancellationTokenSource();
        var waiting = new Scripted(async (p, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Enhanced.Unchanged(p, "never");
        });
        var call = new GuardedPromptEnhancer(waiting, TimeSpan.FromSeconds(30)).EnhanceAsync(Prompt, Context, cancel.Token);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Fact]
    public async Task A_failing_or_late_feedback_never_reaches_the_run()
    {
        var failing = new Scripted((p, _) => Task.FromResult(Enhanced.Unchanged(p, "x")), _ => throw new InvalidOperationException("boom"));
        var never = new TaskCompletionSource();
        var late = new Scripted((p, _) => Task.FromResult(Enhanced.Unchanged(p, "x")), _ => never.Task);
        var time = new ManualTimeProvider();

        await new GuardedPromptEnhancer(failing, Deadline, time).FeedbackAsync("req-1", new EnhanceOutcome(RewriteAccepted: true), CancellationToken.None);
        var call = new GuardedPromptEnhancer(late, Deadline, time).FeedbackAsync("req-1", new EnhanceOutcome(RewriteAccepted: true), CancellationToken.None);
        time.Advance(Deadline);
        await call;
    }

    [Fact]
    public async Task Enhance_sends_the_prompt_and_only_the_context_that_is_set()
    {
        var profile = ProfileWith("""{"server":"gw"}""");
        await using var server = Whetstone(Rewrite);
        await using var pool = PoolFor(profile, server);

        var enhanced = await new McpPromptEnhancer(profile.PromptEnhancer!, pool)
            .EnhanceAsync(Prompt, new EnhanceContext(Repository: "example/app", TaskKind: "review"), CancellationToken.None);

        var (tool, args) = Assert.Single(server.Calls);
        Assert.Equal("enhance", tool);
        Assert.Equal(Prompt, args["prompt"].GetString());
        Assert.Equal(1500, args["deadline_ms"].GetInt32());
        Assert.Equal(["client", "repository", "task_kind"], args["context"].EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("chargehand", args["context"].GetProperty("client").GetString());
        Assert.True(enhanced.Changed);
        Assert.Equal("Review the diff against main; bugs first, each with file:line.", enhanced.Rewrite);
        Assert.Equal(("req-2", "tpl-review", "1.2.0"), (enhanced.RequestId, enhanced.TemplateId, enhanced.TemplateVersion));
    }

    [Fact]
    public async Task A_pass_through_answer_has_no_rewrite()
    {
        var profile = ProfileWith("""{"server":"gw"}""");
        await using var server = Whetstone(PassThrough);
        await using var pool = PoolFor(profile, server);

        var enhanced = await PromptEnhancers.From(profile, pool)!.EnhanceAsync(Prompt, Context, CancellationToken.None);

        Assert.False(enhanced.Changed);
        Assert.Equal(Prompt, enhanced.Original);
        Assert.Equal("req-1", enhanced.RequestId);
    }

    [Fact]
    public async Task Feedback_sends_the_request_id_and_only_the_outcome_that_is_known()
    {
        var profile = ProfileWith("""{"server":"gw"}""");
        await using var server = Whetstone(PassThrough);
        await using var pool = PoolFor(profile, server);

        await new McpPromptEnhancer(profile.PromptEnhancer!, pool)
            .FeedbackAsync("req-2", new EnhanceOutcome(RewriteAccepted: true, Score: 0.9, CostUsd: 0.04m), CancellationToken.None);

        var (tool, args) = Assert.Single(server.Calls);
        Assert.Equal("feedback", tool);
        Assert.Equal("req-2", args["request_id"].GetString());
        var outcome = args["outcome"];
        Assert.Equal(["cost_usd", "rewrite_accepted", "score"], outcome.EnumerateObject().Select(p => p.Name).Order());
        Assert.True(outcome.GetProperty("rewrite_accepted").GetBoolean());
        Assert.Equal(0.9, outcome.GetProperty("score").GetDouble());
    }

    [Fact]
    public async Task A_server_error_becomes_the_original_prompt_and_its_text_is_not_kept()
    {
        var profile = ProfileWith("""{"server":"gw"}""");
        await using var server = new FakeMcpServer(new FakeTool("enhance", _ => FakeMcpServer.Error($"cannot enhance '{Prompt}'")));
        await using var pool = PoolFor(profile, server);

        var enhanced = await PromptEnhancers.From(profile, pool)!.EnhanceAsync(Prompt, Context, CancellationToken.None);

        Assert.False(enhanced.Changed);
        Assert.Equal(Prompt, enhanced.Original);
        Assert.DoesNotContain(Prompt, enhanced.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_server_becomes_the_original_prompt()
    {
        var profile = ProfileWith("""{"server":"gw"}""");
        await using var pool = new McpConnectionPool(profile.McpServers!, _ => "", (_, _, _) => throw new IOException("connection refused"));

        var enhanced = await PromptEnhancers.From(profile, pool)!.EnhanceAsync(Prompt, Context, CancellationToken.None);

        Assert.False(enhanced.Changed);
        Assert.Equal(Prompt, enhanced.Original);
    }

    [Fact]
    public async Task With_no_prompt_enhancer_in_the_profile_there_is_none()
    {
        await using var pool = new McpConnectionPool(new Dictionary<string, McpServerSettings>(), _ => "");

        Assert.Null(PromptEnhancers.From(new Profile("profile/v1"), pool));
    }

    [Fact]
    public void An_unknown_server_fails_at_load_with_an_action()
    {
        var e = Assert.Throws<ChargehandException>(() => ProfileWith("""{"server":"nope"}"""));

        Assert.Contains("prompt_enhancer.server 'nope'", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(49)]
    [InlineData(30001)]
    public void A_deadline_out_of_range_fails_at_load(int ms) =>
        Assert.Throws<ChargehandException>(() => ProfileWith($$"""{"server":"gw","deadline_ms":{{ms}}}"""));

    [Fact]
    public void The_profile_deadline_is_the_guards_and_the_one_the_enhancer_is_told()
    {
        Assert.Equal(1500, ProfileWith("""{"server":"gw"}""").PromptEnhancer!.EffectiveDeadlineMs);
        Assert.Equal(800, ProfileWith("""{"server":"gw","deadline_ms":800}""").PromptEnhancer!.EffectiveDeadlineMs);
    }

    [Fact]
    public async Task Extensions_check_lists_the_two_tools()
    {
        var profile = ProfileWith("""{"server":"gw"}""");
        await using var server = Whetstone(PassThrough);
        await using var pool = PoolFor(profile, server);

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, null, CancellationToken.None);

        Assert.True(result.Ok, string.Join("\n", result.Lines));
        Assert.Contains("prompt_enhancer: enhance ok", result.Lines);
        Assert.Contains("prompt_enhancer: feedback ok", result.Lines);
    }

    [Fact]
    public async Task Extensions_check_names_a_missing_tool_with_an_action()
    {
        var profile = ProfileWith("""{"server":"gw"}""");
        await using var server = new FakeMcpServer(new FakeTool("enhance", _ => Answer(PassThrough)));
        await using var pool = PoolFor(profile, server);

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(result.Lines, l => l.StartsWith("prompt_enhancer: tool 'feedback' is not listed", StringComparison.Ordinal) && l.Contains("; action: ", StringComparison.Ordinal));
    }
}
