using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Mcp;
using Chargehand.RunLog;
using ModelContextProtocol.Protocol;

namespace Chargehand.Tests;

/// <summary>ADR 0041: a run asks the prompt enhancer once, sends the original text whatever it answers, and reports the outcome once.</summary>
public class EnhancerRunTests
{
    private const string Rewrite = """{"prompt":"Read README.md and quote its first line.","changed":true,"template_id":"tpl","template_version":"1.0.0","reason":"r","request_id":"req-9","held_out":false}""";

    private const string PassThrough = """{"prompt":"What does the README say?","changed":false,"template_id":null,"template_version":null,"reason":"r","request_id":"req-9","held_out":false}""";

    private static CallToolResult Answer(string json) =>
        new() { StructuredContent = JsonDocument.Parse(json).RootElement.Clone(), Content = [new TextContentBlock { Text = json }] };

    private static FakeMcpServer Whetstone(string enhance) => new(new FakeTool("enhance", _ => Answer(enhance)), new FakeTool("feedback", _ => Answer("{}")));

    private static Profile WithEnhancer(string workerRoot) => Runs.Profile(workerRoot) with
    {
        McpServers = new Dictionary<string, McpServerSettings> { ["whetstone"] = new McpServerSettings(Url: "https://mcp.example.internal/mcp") },
        PromptEnhancer = new PromptEnhancerSettings("whetstone", 300),
    };

    // A clock nobody advances: the deadline never fires, so a slow in-process server cannot turn into "no call reached it".
    private static async Task<(ResultContract Result, ScriptedRuntime Runtime)> RunAsync(string workerRoot, Profile profile, McpConnectionPool pool, RepositoryRef repo)
    {
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var result = await new Orchestrator(profile, runtime, "2.0.16", Repo.Root, new JsonlRunLog(Path.Combine(workerRoot, "log.jsonl")), new Dictionary<string, int>(),
                enhancer: PromptEnhancers.From(profile, pool, new ManualTimeProvider()))
            .RunAsync(Runs.CheapRequest(repo), CancellationToken.None);
        return (result, runtime);
    }

    private static McpConnectionPool PoolFor(Profile profile, FakeMcpServer server) =>
        new(profile.McpServers!, _ => "", async (_, _, _) => await server.TransportAsync());

    [Fact]
    public async Task A_rewrite_is_not_sent_and_is_reported_as_not_accepted()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var profile = WithEnhancer(root.Path);
        await using var server = Whetstone(Rewrite);
        await using var pool = PoolFor(profile, server);

        var (result, runtime) = await RunAsync(root.Path, profile, pool, repo);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.DoesNotContain(runtime.IntakePrompts.Concat(runtime.Prompts), p => p.Contains("quote its first line", StringComparison.Ordinal));
        Assert.Contains(runtime.Prompts, p => p.Contains("What does the README say?", StringComparison.Ordinal));
        var feedback = Assert.Single(server.Calls, c => c.Tool == "feedback").Arguments;
        Assert.Equal("req-9", feedback["request_id"].GetString());
        var outcome = feedback["outcome"];
        Assert.False(outcome.GetProperty("rewrite_accepted").GetBoolean());
        Assert.Equal("p/small", outcome.GetProperty("model").GetString());
        Assert.True(outcome.TryGetProperty("cost_usd", out _));
        Assert.False(outcome.TryGetProperty("score", out _));
    }

    [Fact]
    public async Task The_context_sent_is_exactly_the_four_allowed_fields()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var profile = WithEnhancer(root.Path);
        await using var server = Whetstone(PassThrough);
        await using var pool = PoolFor(profile, server);

        await RunAsync(root.Path, profile, pool, repo);

        var enhance = Assert.Single(server.Calls, c => c.Tool == "enhance").Arguments;
        Assert.Equal("What does the README say?", enhance["prompt"].GetString());
        var context = enhance["context"];
        Assert.Equal(["client", "commit", "repository", "task_kind"], context.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("repo", context.GetProperty("repository").GetString());
        Assert.Equal(repo.Commit, context.GetProperty("commit").GetString());
        Assert.Equal("cheap", context.GetProperty("task_kind").GetString());
        Assert.Equal("chargehand", context.GetProperty("client").GetString());
        Assert.DoesNotContain(root.Path, enhance.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pass_through_reports_no_acceptance_and_feedback_goes_once()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var profile = WithEnhancer(root.Path);
        await using var server = Whetstone(PassThrough);
        await using var pool = PoolFor(profile, server);

        await RunAsync(root.Path, profile, pool, repo);

        Assert.Equal(["enhance", "feedback"], server.Calls.Select(c => c.Tool));
        Assert.False(server.Calls[1].Arguments["outcome"].TryGetProperty("rewrite_accepted", out _));
    }

    [Fact]
    public async Task No_prompt_enhancer_in_the_profile_means_no_call()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var profile = Runs.Profile(root.Path);
        await using var server = Whetstone(Rewrite);
        await using var pool = new McpConnectionPool(new Dictionary<string, McpServerSettings>(), _ => "", async (_, _, _) => await server.TransportAsync());

        var (result, _) = await RunAsync(root.Path, profile, pool, repo);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Empty(server.Calls);
    }

    [Fact]
    public async Task An_enhancer_that_is_down_leaves_a_normal_run()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var profile = WithEnhancer(root.Path);
        await using var pool = new McpConnectionPool(profile.McpServers!, _ => "", (_, _, _) => throw new IOException("connection refused"));

        var (result, runtime) = await RunAsync(root.Path, profile, pool, repo);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Contains(runtime.Prompts, p => p.Contains("What does the README say?", StringComparison.Ordinal));
    }
}
