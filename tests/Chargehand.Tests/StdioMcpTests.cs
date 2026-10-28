using System.IO.Pipelines;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Ledger.Api;
using Chargehand.RunLog;
using Chargehand.Runtime;
using Chargehand.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace Chargehand.Tests;

/// <summary>
/// The same "orchestrate" tool over the stdio transport (`chargehand mcp`, ADR 0027), in process: two pipes stand in
/// for the child's stdin and stdout.
/// </summary>
public sealed class StdioMcpTests : IAsyncDisposable
{
    private readonly TempDir _root = new();
    private readonly Pipe _toServer = new();
    private readonly Pipe _toClient = new();
    private IHost? _host;
    private ScriptedRuntime _runtime = new(Runs.DraftReply);

    private Task<McpClient> Start(ScriptedRuntime runtime, List<ElicitRequestParams>? asked = null)
    {
        _runtime = runtime;
        return Start((IWorkerRuntime)runtime, asked);
    }

    private async Task<McpClient> Start(IWorkerRuntime runtime, List<ElicitRequestParams>? asked = null)
    {
        var log = new JsonlRunLog(Path.Combine(_root.Path, "log.jsonl"));
        _host = ChargehandServer.CreateStdio(Repo.Path("presets"), Runs.Orchestrator(runtime, _root.Path, log),
            _toServer.Reader.AsStream(), _toClient.Writer.AsStream());
        await _host.StartAsync();
        return await McpClient.CreateAsync(new StreamClientTransport(_toServer.Writer.AsStream(), _toClient.Reader.AsStream()),
            new McpClientOptions
            {
                // Not left to fall back to the 2025-11-25 handshake (no tasks) when the discover probe is slow on a loaded machine.
                ProtocolVersion = "2026-07-28",
                DiscoverProbeTimeout = TimeSpan.FromSeconds(60),
                Handlers = new McpClientHandlers
                {
                    ElicitationHandler = asked is null ? null : (p, _) =>
                    {
                        asked.Add(p!);
                        return ValueTask.FromResult(new ElicitResult { Action = "accept", Content = new Dictionary<string, JsonElement> { ["q1"] = JsonSerializer.SerializeToElement("warm") } });
                    },
                },
            });
    }

    private static Dictionary<string, JsonElement> Args(RunRequest request) =>
        JsonSerializer.SerializeToElement(request, ContractJson.Options).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static ResultContract Result(CallToolResult call)
    {
        Assert.NotEqual(true, call.IsError);
        return call.StructuredContent!.Value.Deserialize<ResultContract>(ContractJson.Options)!;
    }

    [Fact]
    public async Task The_tool_runs_over_stdio_with_the_http_servers_instructions()
    {
        await using var client = await Start(new ScriptedRuntime(Runs.DraftReply));
        Assert.Equal([OrchestrateTool.Name, RepositoriesTool.Name, ResultTool.Name], (await client.ListToolsAsync()).Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.Equal(ChargehandServer.Instructions, client.ServerInstructions);

        var result = Result(await client.CallToolAsync(OrchestrateTool.Name, Args(Runs.DraftRequest()).ToDictionary(a => a.Key, a => (object?)a.Value)));
        Assert.Equal(ResultStatus.Completed, result.Status);
    }

    [Fact]
    public async Task Logs_never_reach_stdout()
    {
        await using var client = await Start(new ScriptedRuntime(Runs.DraftReply));
        var provider = Assert.Single(_host!.Services.GetServices<ILoggerProvider>());
        Assert.IsType<ConsoleLoggerProvider>(provider);
        Assert.Equal(LogLevel.Trace, _host.Services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.LogToStandardErrorThreshold);
    }

    [Fact]
    public async Task A_task_completes_over_stdio()
    {
        await using var client = await Start(new ScriptedRuntime(Runs.DraftReply));
        var call = new CallToolRequestParams { Name = OrchestrateTool.Name, Arguments = Args(Runs.DraftRequest()) };
        Assert.True((await client.CallToolAsTaskAsync(call)).IsTask);
        Assert.Equal(ResultStatus.Completed, Result(await client.CallToolWithPollingAsync(call)).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ask_elicits_over_stdio_and_the_answers_resend_the_request(bool asTask)
    {
        var asked = new List<ElicitRequestParams>();
        await using var client = await Start(new ScriptedRuntime(Runs.WorkerReply, ScriptedRuntime.Spec("ask", """{"questions":["Which tone?"]}"""), ScriptedRuntime.Spec()), asked);
        var args = Args(Runs.CheapRequest(Runs.GitRepo(_root.Path), interactive: true));

        var result = Result(asTask
            ? await client.CallToolWithPollingAsync(new CallToolRequestParams { Name = OrchestrateTool.Name, Arguments = args })
            : await client.CallToolAsync(OrchestrateTool.Name, args.ToDictionary(a => a.Key, a => (object?)a.Value)));

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Single(asked);
        Assert.Contains("- Which tone? warm", _runtime.IntakePrompts.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_runtime_that_cannot_start_answers_initialize_and_fails_the_call_with_its_reason()
    {
        var error = new ChargehandException(ErrorCode.RuntimeUnavailable, "cannot start claude: no such file", "Install Claude Code.");
        await using var client = await Start(new UnavailableRuntime(error));
        Assert.Equal(3, (await client.ListToolsAsync()).Count);

        var call = await client.CallToolAsync(OrchestrateTool.Name, Args(Runs.DraftRequest()).ToDictionary(a => a.Key, a => (object?)a.Value));

        var failed = call.StructuredContent!.Value.Deserialize<ResultContract>(ContractJson.Options)!;
        Assert.Equal(ResultStatus.Failed, failed.Status);
        Assert.Contains("cannot start claude", failed.Summary, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        _runtime.Hold.TrySetResult();
        await _toServer.Writer.CompleteAsync();
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        _root.Dispose();
    }
}
