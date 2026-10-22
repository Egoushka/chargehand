using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Server;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace Chargehand.Tests;

/// <summary>The MCP tool "orchestrate" over Streamable HTTP (ADR 0014, ADR 0018), driven by the SDK's own client.</summary>
public class McpTests
{
    private const string AskSpecDetail = """{"questions":["Which tone?"]}""";

    private static async Task<McpClient> Connect(TestServer s, Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>>? elicit = null, string? prefer = null, string? bearer = null) =>
        await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(s.BaseAddress, "/v1/mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearer ?? TestServer.Key}" }
                    .Concat(prefer is null ? [] : [new("Prefer", prefer)]).ToDictionary(),
            }),
            new McpClientOptions { Handlers = new McpClientHandlers { ElicitationHandler = elicit } });

    private static Dictionary<string, JsonElement> Args(RunRequest request) =>
        JsonSerializer.SerializeToElement(request, ContractJson.Options).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static ResultContract Result(CallToolResult call)
    {
        Assert.True(call.IsError != true, string.Join(" | ", call.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.Result, call.StructuredContent!.Value));
        return call.StructuredContent.Value.Deserialize<ResultContract>(ContractJson.Options)!;
    }

    /// <summary>Answers every elicitation with "warm" and records what was asked.</summary>
    private static Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> Answer(List<ElicitRequestParams> asked) => (p, _) =>
    {
        asked.Add(p!);
        return ValueTask.FromResult(new ElicitResult { Action = "accept", Content = new Dictionary<string, JsonElement> { ["q1"] = JsonSerializer.SerializeToElement("warm") } });
    };

    [Fact]
    public async Task The_tool_takes_request_v1_and_returns_result_v1()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        await using var client = await Connect(s);
        var tools = await client.ListToolsAsync();
        Assert.Equal([OrchestrateTool.Name, RepositoriesTool.Name, ResultTool.Name], tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        var tool = tools.Single(t => t.Name == OrchestrateTool.Name);
        Assert.Equal(ChargehandServer.Instructions, client.ServerInstructions);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ContractSchemas.Text(ContractSchemas.Request)), JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ContractSchemas.Text(ContractSchemas.Result)), JsonNode.Parse(tool.ProtocolTool.OutputSchema!.Value.GetRawText())));

        var result = Result(await client.CallToolAsync(OrchestrateTool.Name, Args(Runs.DraftRequest()).ToDictionary(a => a.Key, a => (object?)a.Value)));
        Assert.Equal(ResultStatus.Completed, result.Status);
        // A run started over MCP is the same run the HTTP interface reads.
        await ServerTests.WaitUntil(async () => (await s.Http.GetAsync($"/v1/runs/{result.TaskId}")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_sessions_run_token_reaches_the_tool_for_two_presets_on_its_commit_and_records_its_parent()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.WorkerReply));
        var repo = Runs.GitRepo(s.WorkerRoot);
        var token = s.Tokens.Issue(new RunTokenClaims("run-parent", repo.Path, repo.Commit, null, null, DateTimeOffset.UtcNow.AddHours(1)), DateTimeOffset.UtcNow);
        await using var client = await Connect(s, bearer: token);
        Assert.Contains(OrchestrateTool.Name, (await client.ListToolsAsync()).Select(t => t.Name));

        var refused = await client.CallToolAsync(OrchestrateTool.Name, Args(new RunRequest("request/v1", "Write it.", new RequestContext(false, "code", Repository: repo))).ToDictionary(a => a.Key, a => (object?)a.Value));
        Assert.True(refused.IsError);
        Assert.Contains("preset", ((TextContentBlock)refused.Content[0]).Text, StringComparison.Ordinal);

        var request = new RunRequest("request/v1", "What does the README say?", new RequestContext(false, "default", Repository: repo));
        var result = Result(await client.CallToolAsync(OrchestrateTool.Name, Args(request).ToDictionary(a => a.Key, a => (object?)a.Value)));
        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal("run-parent", (await s.Log.ReadAsync(result.TaskId, CancellationToken.None)).Start!.ParentRunId);
    }

    [Fact]
    public async Task The_repositories_tool_lists_each_checkout_with_its_commit()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        var repo = Runs.GitRepo(s.WorkerRoot);
        await using var client = await Connect(s);

        var call = await client.CallToolAsync(RepositoriesTool.Name, new Dictionary<string, object?>());

        Assert.True(call.IsError != true);
        var listed = Assert.Single(JsonDocument.Parse(((TextContentBlock)call.Content[0]).Text).RootElement.EnumerateArray());
        Assert.Equal("repo", listed.GetProperty("name").GetString());
        Assert.Equal(repo.Commit, listed.GetProperty("commit").GetString());
    }

    [Fact]
    public async Task An_invalid_request_is_a_tool_error()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        await using var client = await Connect(s);
        var call = await client.CallToolAsync(OrchestrateTool.Name, new Dictionary<string, object?> { ["text"] = "no context" });
        Assert.True(call.IsError);
        Assert.Contains("request/v1", ((TextContentBlock)call.Content[0]).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_progress_token_gets_the_run_id_before_the_run_finishes()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Hold = new() };
        await using var s = await TestServer.StartAsync(runtime);
        await using var client = await Connect(s);
        var messages = new ConcurrentQueue<string>();
        var call = client.CallToolAsync(OrchestrateTool.Name, Args(Runs.DraftRequest()).ToDictionary(a => a.Key, a => (object?)a.Value),
            new Progress<ProgressNotificationValue>(p => messages.Enqueue(p.Message!))).AsTask();

        await ServerTests.WaitUntil(() => Task.FromResult(!messages.IsEmpty));
        Assert.False(call.IsCompleted);
        runtime.Hold.SetResult();
        var result = Result(await call);
        Assert.Contains($"/v1/runs/{result.TaskId}", messages.First(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_tool_description_says_the_arguments_are_not_wrapped_in_a_request_object()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        await using var client = await Connect(s);
        var tool = (await client.ListToolsAsync()).Single(t => t.Name == OrchestrateTool.Name);
        Assert.Contains("not wrapped in a \"request\" object", tool.Description, StringComparison.Ordinal);
        Assert.Contains("no `request` wrapper", SessionCli.DefaultPrompt, StringComparison.Ordinal);
        var wrapped = await client.CallToolAsync(OrchestrateTool.Name, new Dictionary<string, object?> { ["request"] = Args(Runs.DraftRequest()) });
        Assert.True(wrapped.IsError);
    }

    [Fact]
    public async Task A_session_call_is_accepted_when_the_unfinished_cap_is_full_of_top_level_runs()
    {
        var runtime = new ScriptedRuntime(Runs.WorkerReply) { Hold = new() };
        await using var s = await TestServer.StartAsync(runtime);
        for (var i = 0; i < RunService.MaxUnfinished; i++)
            Assert.Equal(System.Net.HttpStatusCode.Accepted, (await s.PostAsync(Runs.DraftRequest(), waitSeconds: 0)).StatusCode);
        var repo = Runs.GitRepo(s.WorkerRoot);
        var token = s.Tokens.Issue(new RunTokenClaims("run-parent", repo.Path, repo.Commit, null, null, DateTimeOffset.UtcNow.AddHours(1)), DateTimeOffset.UtcNow);
        await using var client = await Connect(s, bearer: token);
        var messages = new ConcurrentQueue<string>();
        var request = new RunRequest("request/v1", "What does the README say?", new RequestContext(false, "default", Repository: repo));
        var call = client.CallToolAsync(OrchestrateTool.Name, Args(request).ToDictionary(a => a.Key, a => (object?)a.Value),
            new Progress<ProgressNotificationValue>(p => messages.Enqueue(p.Message!))).AsTask();

        await ServerTests.WaitUntil(() => Task.FromResult(!messages.IsEmpty || call.IsCompleted));
        Assert.False(call.IsCompleted);
        runtime.Hold.SetResult();
        Assert.Equal(ResultStatus.Completed, Result(await call).Status);
    }

    [Fact]
    public async Task A_waiting_call_sends_progress_at_an_interval_saying_whether_it_is_queued_or_running()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Hold = new() };
        await using var s = await TestServer.StartAsync(runtime);
        await using var client = await Connect(s);
        var messages = new ConcurrentQueue<string>();
        var before = OrchestrateTool.ProgressInterval;
        OrchestrateTool.ProgressInterval = TimeSpan.FromMilliseconds(100);
        try
        {
            var call = client.CallToolAsync(OrchestrateTool.Name, Args(Runs.DraftRequest()).ToDictionary(a => a.Key, a => (object?)a.Value),
                new Progress<ProgressNotificationValue>(p => messages.Enqueue(p.Message!))).AsTask();
            await ServerTests.WaitUntil(() => Task.FromResult(messages.Any(m => m.Contains(" is queued;", StringComparison.Ordinal) || m.Contains(" is running;", StringComparison.Ordinal))));
            Assert.False(call.IsCompleted);
            runtime.Hold.SetResult();
            Result(await call);
        }
        finally
        {
            OrchestrateTool.ProgressInterval = before;
        }
    }

    [Fact]
    public async Task Prefer_wait_returns_the_run_id_while_the_run_goes_on()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Hold = new() };
        await using var s = await TestServer.StartAsync(runtime);
        await using var client = await Connect(s, prefer: "wait=0");
        var call = await client.CallToolAsync(OrchestrateTool.Name, Args(Runs.DraftRequest()).ToDictionary(a => a.Key, a => (object?)a.Value))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        // Not result/v1 yet, so not structuredContent: a tool error whose last block is run-status/v1.
        Assert.True(call.IsError);
        var json = ((TextContentBlock)call.Content[^1]).Text;
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.RunStatus, JsonDocument.Parse(json).RootElement));
        var status = JsonSerializer.Deserialize<RunStatus>(json, ContractJson.Options)!;
        Assert.Contains($"/v1/runs/{status.RunId}", ((TextContentBlock)call.Content[0]).Text, StringComparison.Ordinal);
        runtime.Hold.SetResult();
        await ServerTests.WaitUntil(async () => (await s.Http.GetAsync($"/v1/runs/{status.RunId}")).StatusCode == System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task Without_prefer_an_HTTP_call_returns_the_run_id_and_the_result_tool_reads_the_result()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Hold = new() };
        await using var s = await TestServer.StartAsync(runtime, tweak: (_, settings) => settings with { McpWait = TimeSpan.FromMilliseconds(300) });
        await using var client = await Connect(s);
        var call = await client.CallToolAsync(OrchestrateTool.Name, Args(Runs.DraftRequest()).ToDictionary(a => a.Key, a => (object?)a.Value))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(call.IsError);
        var status = JsonSerializer.Deserialize<RunStatus>(((TextContentBlock)call.Content[^1]).Text, ContractJson.Options)!;
        Assert.Contains(ResultTool.Name, ((TextContentBlock)call.Content[0]).Text, StringComparison.Ordinal);

        var running = await client.CallToolAsync(ResultTool.Name, new Dictionary<string, object?> { ["run_id"] = status.RunId }).AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(running.IsError);
        Assert.Equal(status.RunId, JsonSerializer.Deserialize<RunStatus>(((TextContentBlock)running.Content[^1]).Text, ContractJson.Options)!.RunId);

        runtime.Hold.SetResult();
        await ServerTests.WaitUntil(async () => (await s.Http.GetAsync($"/v1/runs/{status.RunId}")).StatusCode == System.Net.HttpStatusCode.OK);
        var done = await client.CallToolAsync(ResultTool.Name, new Dictionary<string, object?> { ["run_id"] = status.RunId }).AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ResultStatus.Completed, Result(done).Status);
    }

    [Fact]
    public async Task A_session_reads_its_research_with_the_result_tool_when_it_names_its_own_commit()
    {
        // CHARGEHAND-246: the session sent its new HEAD, which the server's checkout lacks, and the call outlived the MCP wait.
        var runtime = new ScriptedRuntime(Runs.WorkerReply) { Hold = new() };
        await using var s = await TestServer.StartAsync(runtime, tweak: (_, settings) => settings with { McpWait = TimeSpan.FromMilliseconds(300) });
        var repo = Runs.GitRepo(s.WorkerRoot);
        var token = s.Tokens.Issue(new RunTokenClaims("run-parent", repo.Path, repo.Commit, null, null, DateTimeOffset.UtcNow.AddHours(1)), DateTimeOffset.UtcNow);
        await using var client = await Connect(s, bearer: token);
        var request = new RunRequest("request/v1", "What does the README say?", new RequestContext(false, "default", Repository: repo with { Commit = new string('e', 40) }));
        var call = await client.CallToolAsync(OrchestrateTool.Name, Args(request).ToDictionary(a => a.Key, a => (object?)a.Value)).AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        var status = JsonSerializer.Deserialize<RunStatus>(((TextContentBlock)call.Content[^1]).Text, ContractJson.Options)!;

        runtime.Hold.SetResult();
        await ServerTests.WaitUntil(async () => (await s.Http.GetAsync($"/v1/runs/{status.RunId}")).StatusCode == System.Net.HttpStatusCode.OK);
        var done = await client.CallToolAsync(ResultTool.Name, new Dictionary<string, object?> { ["run_id"] = status.RunId }).AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ResultStatus.Completed, Result(done).Status);
        var list = JsonDocument.Parse(await (await s.Http.GetAsync($"/v1/runs?run_id={status.RunId}")).Content.ReadAsStringAsync());
        Assert.Equal(repo.Commit, list.RootElement.GetProperty("runs")[0].GetProperty("commit").GetString());
    }

    [Fact]
    public async Task The_result_tool_names_an_unknown_run()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        await using var client = await Connect(s);
        var call = await client.CallToolAsync(ResultTool.Name, new Dictionary<string, object?> { ["run_id"] = "run-00000000-000000-000000" });

        Assert.True(call.IsError);
        Assert.Contains("no run run-00000000-000000-000000", ((TextContentBlock)call.Content[0]).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_task_runs_in_the_background_and_completes()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Hold = new() };
        await using var s = await TestServer.StartAsync(runtime);
        // A task already gives the client an id to poll, so Prefer: wait does not cut it short.
        await using var client = await Connect(s, prefer: "wait=0");
        var raw = await client.CallToolAsTaskAsync(new CallToolRequestParams { Name = OrchestrateTool.Name, Arguments = Args(Runs.DraftRequest()) });
        Assert.True(raw.IsTask);

        var polled = client.CallToolWithPollingAsync(new CallToolRequestParams { Name = OrchestrateTool.Name, Arguments = Args(Runs.DraftRequest()) });
        await ServerTests.WaitUntil(async () => (await s.Log.ReadAllAsync(CancellationToken.None)).Starts.Count > 0);
        runtime.Hold.SetResult();
        var result = Result(await polled);
        Assert.Equal(ResultStatus.Completed, result.Status);
    }

    [Fact]
    public async Task Ask_in_a_task_becomes_input_required_and_the_answers_resend_the_request()
    {
        var runtime = new ScriptedRuntime(Runs.WorkerReply, ScriptedRuntime.Spec("ask", AskSpecDetail), ScriptedRuntime.Spec());
        await using var s = await TestServer.StartAsync(runtime);
        var asked = new List<ElicitRequestParams>();
        await using var client = await Connect(s, Answer(asked));

        var result = Result(await client.CallToolWithPollingAsync(new CallToolRequestParams
        {
            Name = OrchestrateTool.Name,
            Arguments = Args(Runs.CheapRequest(Runs.GitRepo(s.WorkerRoot), interactive: true)),
        }));

        Assert.Equal(ResultStatus.Completed, result.Status);
        await AssertResentWithAnswers(s, asked);
    }

    [Fact]
    public async Task Ask_outside_a_task_returns_input_required_and_the_retry_carries_the_answers()
    {
        var runtime = new ScriptedRuntime(Runs.WorkerReply, ScriptedRuntime.Spec("ask", AskSpecDetail), ScriptedRuntime.Spec());
        await using var s = await TestServer.StartAsync(runtime);
        var asked = new List<ElicitRequestParams>();
        await using var client = await Connect(s, Answer(asked));

        var result = Result(await client.CallToolAsync(OrchestrateTool.Name,
            Args(Runs.CheapRequest(Runs.GitRepo(s.WorkerRoot), interactive: true)).ToDictionary(a => a.Key, a => (object?)a.Value)));

        Assert.Equal(ResultStatus.Completed, result.Status);
        await AssertResentWithAnswers(s, asked);
    }

    [Fact]
    public async Task A_non_interactive_ask_returns_needs_input_without_asking()
    {
        var runtime = new ScriptedRuntime(Runs.WorkerReply, ScriptedRuntime.Spec("ask", AskSpecDetail));
        await using var s = await TestServer.StartAsync(runtime);
        var asked = new List<ElicitRequestParams>();
        await using var client = await Connect(s, Answer(asked));

        var result = Result(await client.CallToolAsync(OrchestrateTool.Name,
            Args(Runs.CheapRequest(Runs.GitRepo(s.WorkerRoot))).ToDictionary(a => a.Key, a => (object?)a.Value)));

        Assert.Equal(ResultStatus.NeedsInput, result.Status);
        Assert.Equal(["Which tone?"], result.OpenQuestions);
        Assert.Empty(asked);
    }

    /// <summary>The client was asked intake's question once, and a second run carried the answer and its parent's id.</summary>
    private static async Task AssertResentWithAnswers(TestServer s, List<ElicitRequestParams> asked)
    {
        var question = Assert.Single(asked).RequestedSchema!.Properties["q1"];
        Assert.Equal("Which tone?", ((ElicitRequestParams.StringSchema)question).Description);
        Assert.Contains("- Which tone? warm", s.Runtime.IntakePrompts.Last(), StringComparison.Ordinal);
        var starts = (await s.Log.ReadAllAsync(CancellationToken.None)).Starts;
        Assert.Equal(2, starts.Count);
        Assert.Equal(starts[0].RunId, starts[1].ParentRunId);
    }
}
