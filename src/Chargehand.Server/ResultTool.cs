using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Accounts.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Chargehand.Server;

/// <summary>
/// MCP tool "result" (ADR 0065): the result of a run an orchestrate call answered with its id, for a client that has only MCP. It waits as orchestrate does
/// (over HTTP at most <see cref="McpHttp.Wait"/>), then returns result/v1, or run-status/v1 as a tool error while the run goes on. Who may read a run is the
/// same as for <c>GET /v1/runs/{id}</c>.
/// </summary>
public static class ResultTool
{
    public const string Name = "result";

    private const string InputSchema = """
        {"type":"object","properties":{"run_id":{"type":"string","description":"The run id an orchestrate call returned instead of result/v1."}},"required":["run_id"]}
        """;

    public static McpServerTool Create()
    {
        var tool = McpServerTool.Create(CallAsync, new McpServerToolCreateOptions
        {
            Name = Name,
            Description = "Read the result of a chargehand run that an orchestrate call answered with a run id because it was still running. Returns result/v1 once the "
                        + "run finished; before that, waits a while and returns the run's status as an error, so call it again.",
            UseStructuredContent = true,
            OutputSchema = JsonDocument.Parse(ContractSchemas.Text(ContractSchemas.Result)).RootElement.Clone(),
        });
        tool.ProtocolTool.InputSchema = JsonDocument.Parse(InputSchema).RootElement.Clone();
        return tool;
    }

    private static async Task<CallToolResult> CallAsync(RequestContext<CallToolRequestParams> context, CancellationToken ct)
    {
        if (context.Params?.Arguments?.TryGetValue("run_id", out var arg) != true || arg.ValueKind != JsonValueKind.String || arg.GetString() is not { Length: > 0 } runId)
            return Error("result takes run_id: the id an orchestrate call returned");
        var runs = context.Services!.GetRequiredService<RunService>();
        var http = context.Services!.GetService<McpHttp>();
        var who = PrincipalHttp.Of(context.Services!.GetService<IHttpContextAccessor>()?.HttpContext);
        if (runs.Find(runId) is { } run && Principal.Sees(who, run.Actor))
        {
            try
            {
                await OrchestrateTool.WaitWithProgress(context, run, http?.Wait ?? Timeout.InfiniteTimeSpan, ct);
            }
            catch (TimeoutException)
            {
                return OrchestrateTool.Running(run);
            }
            catch (Exception) when (run.Done.IsFaulted)
            {
                // Died without a result: the run log says how.
            }
            if (run.Done.IsCompletedSuccessfully)
                return OrchestrateTool.Output(run.Done.Result);
        }
        // Finished runs leave this process for the run log; over stdio there is none to read.
        var stored = http is null ? null : await ChargehandServer.Stored(http.Log, runId, ct);
        return stored switch
        {
            null => Error($"no run {runId} on this server"),
            _ when !Principal.Sees(who, stored.Actor) => Error($"no run {runId} on this server"),
            { Result: { } result } => OrchestrateTool.Output(result),
            { Status: RunState.Lost } => Error($"chargehand run {runId} is lost: {stored.Detail}"),
            _ => OrchestrateTool.Unfinished(stored),
        };
    }

    private static CallToolResult Error(string text) => new() { IsError = true, Content = [new TextContentBlock { Text = text }] };
}
