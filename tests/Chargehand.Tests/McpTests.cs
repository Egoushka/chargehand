using System.Text.Json;
using System.Text.Json.Nodes;
using Chargehand.Contracts;
using Chargehand.Server;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace Chargehand.Tests;

/// <summary>The MCP tool "orchestrate" over Streamable HTTP (ADR 0014, ADR 0018), driven by the SDK's own client.</summary>
public class McpTests
{
    private const string AskSpecDetail = """{"questions":["Which tone?"]}""";

    private static async Task<McpClient> Connect(TestServer s, Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>>? elicit = null) =>
        await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(s.BaseAddress, "/v1/mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {TestServer.Key}" },
            }),
            new McpClientOptions { Handlers = new McpClientHandlers { ElicitationHandler = elicit } });

    private static Dictionary<string, JsonElement> Args(RunRequest request) =>
        JsonSerializer.SerializeToElement(request, ContractJson.Options).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static ResultContract Result(CallToolResult call)
    {
        Assert.NotEqual(true, call.IsError);
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
        var tool = Assert.Single(await client.ListToolsAsync());
        Assert.Equal(OrchestrateTool.Name, tool.Name);
        Assert.Equal(ChargehandServer.Instructions, client.ServerInstructions);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ContractSchemas.Text(ContractSchemas.Request)), JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ContractSchemas.Text(ContractSchemas.Result)), JsonNode.Parse(tool.ProtocolTool.OutputSchema!.Value.GetRawText())));

        var result = Result(await client.CallToolAsync(OrchestrateTool.Name, Args(Runs.DraftRequest()).ToDictionary(a => a.Key, a => (object?)a.Value)));
        Assert.Equal(ResultStatus.Completed, result.Status);
        // A run started over MCP is the same run the HTTP interface reads.
        await ServerTests.WaitUntil(async () => (await s.Http.GetAsync($"/v1/runs/{result.TaskId}")).IsSuccessStatusCode);
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
    public async Task A_task_runs_in_the_background_and_completes()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        await using var client = await Connect(s);
        var raw = await client.CallToolAsTaskAsync(new CallToolRequestParams { Name = OrchestrateTool.Name, Arguments = Args(Runs.DraftRequest()) });
        Assert.True(raw.IsTask);

        var result = Result(await client.CallToolWithPollingAsync(new CallToolRequestParams { Name = OrchestrateTool.Name, Arguments = Args(Runs.DraftRequest()) }));
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
