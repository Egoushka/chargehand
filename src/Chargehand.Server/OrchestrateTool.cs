using System.Globalization;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Chargehand.Server;

/// <summary>
/// MCP tool "orchestrate": inputSchema request/v1, outputSchema result/v1, the result in structuredContent (ADR 0014).
/// A client that opts in to the tasks extension gets long runs as tasks. When intake answers an interactive request
/// with questions (ask), the tool asks the client and resends the request with the answers: inside a task through
/// elicitation, which the task reports as input_required; outside one through an input_required result that the
/// client retries with the answers (MCP 2026-07-28). A non-interactive request gets result/v1 with needs_input.
/// Outside a task the client learns the run id before its own timeout can end the call (ADR 0029): as a progress
/// notification when it sent a progress token, and as run-status/v1 after "Prefer: wait=N" when it sent that header.
/// </summary>
public static class OrchestrateTool
{
    public const string Name = "orchestrate";

    private const string AnswersKey = "answers";

    public static McpServerTool Create()
    {
        var tool = McpServerTool.Create(CallAsync, new McpServerToolCreateOptions
        {
            Name = Name,
            Description = "Run a request through chargehand: intake turns it into a Task Spec, coding-agent workers (OpenCode or Claude Code) answer it, and the result "
                        + "comes back as result/v1 with evidence for every claim. Arguments are a request/v1 document.",
            UseStructuredContent = true,
            OutputSchema = Schema(ContractSchemas.Result),
        });
        tool.ProtocolTool.InputSchema = Schema(ContractSchemas.Request);
        return tool;
    }

    private static JsonElement Schema(string name) => JsonDocument.Parse(ContractSchemas.Text(name)).RootElement.Clone();

    private static async Task<CallToolResult> CallAsync(RequestContext<CallToolRequestParams> context, CancellationToken ct)
    {
        var runs = context.Services!.GetRequiredService<RunService>();
        var call = context.Params!;
        // A retry after an input_required result: the original request travels in requestState, the answers in inputResponses.
        if (call.RequestState is { } state && call.InputResponses?.TryGetValue(AnswersKey, out var response) == true)
        {
            var pending = JsonSerializer.Deserialize<Pending>(Convert.FromBase64String(state), ContractJson.Options)!;
            var answers = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo);
            if (answers?.Action != "accept")
                return Output(pending.Result);
            var resent = await Run(context, runs, WithAnswers(pending.Request, pending.Questions, answers), pending.RunId, ct);
            return resent.Done.IsCompleted ? Output(await resent.Done) : Running(resent);
        }

        var (request, errors) = runs.Validate(JsonSerializer.SerializeToElement(call.Arguments ?? new Dictionary<string, JsonElement>()));
        if (request is null)
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = $"invalid request/v1: {string.Join("; ", errors)}" }] };

        var run = await Run(context, runs, request, null, ct);
        if (!run.Done.IsCompleted)
            return Running(run);
        var result = await run.Done;
        var asked = run.History.Any(e => e.Event == RunEventKind.Intake && e.ExecutedAction == "ask");
        if (!(request.Context.Interactive && asked && result.Status == ResultStatus.NeedsInput && result.OpenQuestions.Count > 0))
            return Output(result);

        var questions = result.OpenQuestions;
        var elicit = new ElicitRequestParams
        {
            Message = "chargehand needs answers before it can run this request.",
            RequestedSchema = new()
            {
                Properties = questions.Select((q, i) => (Key: $"q{i + 1}", Schema: (ElicitRequestParams.PrimitiveSchemaDefinition)new ElicitRequestParams.StringSchema { Description = q }))
                    .ToDictionary(p => p.Key, p => p.Schema),
                Required = [.. questions.Select((_, i) => $"q{i + 1}")],
            },
        };
        try
        {
            // Inside a task the SDK turns this into the task's input_required; with a session it is a plain elicitation.
            var answers = await context.Server.ElicitAsync(elicit, ct);
            if (answers.Action != "accept")
                return Output(result);
            var resent = await Run(context, runs, WithAnswers(request, questions, answers), run.Id, ct);
            return resent.Done.IsCompleted ? Output(await resent.Done) : Running(resent);
        }
        catch (InvalidOperationException) when (context.Server.IsMrtrSupported)
        {
            // Stateless and outside a task: no server-to-client request is possible, so return input_required instead.
            var pending = new Pending(request, questions, run.Id, result);
            throw new InputRequiredException(
                inputRequests: new Dictionary<string, InputRequest> { [AnswersKey] = InputRequest.ForElicitation(elicit) },
                requestState: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(pending, ContractJson.Options)));
        }
    }

    /// <summary>
    /// A run outlives the call: a cancelled call stops waiting, and the run stays readable at /v1/runs/{id}. Outside a
    /// task the client gets the run id as progress, and stops waiting after its "Prefer: wait=N"; the run may be unfinished.
    /// </summary>
    private static async Task<RunHandle> Run(RequestContext<CallToolRequestParams> context, RunService runs, RunRequest request, string? parentRunId, CancellationToken ct)
    {
        var run = runs.Start(request, parentRunId) ?? throw new McpException($"{RunService.MaxUnfinished} runs are unfinished; retry later");
        // A task already hands the client an id to poll: the tasks filter runs every call from a client that opts in.
        if (context.JsonRpcRequest.Context?.ClientCapabilities?.Extensions?.ContainsKey(TasksProtocol.ExtensionId) == true)
        {
            await run.Done.WaitAsync(ct);
            return run;
        }
        if (context.Params?.ProgressToken is { } token)
            await context.Server.NotifyProgressAsync(token, new ProgressNotificationValue { Progress = 0, Message = Where(run, "started") }, cancellationToken: ct);
        // Unlike POST /v1/runs, no wait=N means waiting for the result: a client with a long timeout keeps getting it.
        var prefer = context.Services!.GetRequiredService<IHttpContextAccessor>().HttpContext?.Request.Headers["Prefer"].ToString() ?? "";
        try
        {
            await run.Done.WaitAsync(prefer.Contains("wait=", StringComparison.Ordinal) ? ChargehandServer.Wait(prefer) : Timeout.InfiniteTimeSpan, ct);
        }
        catch (TimeoutException)
        {
            // Prefer: wait ran out; the caller answers with the run's status.
        }
        return run;
    }

    private static string Where(RunHandle run, string state) =>
        $"chargehand run {run.Id} {state}; GET /v1/runs/{run.Id} or `chargehand show {run.Id}` reads its result";

    /// <summary>An unfinished run: not result/v1, so no structuredContent, and a tool error for a client that validates it.</summary>
    private static CallToolResult Running(RunHandle run) => new()
    {
        IsError = true,
        Content =
        [
            new TextContentBlock { Text = Where(run, "is still running after Prefer: wait") },
            new TextContentBlock { Text = JsonSerializer.Serialize(run.Latest, ContractJson.Options) },
        ],
    };

    /// <summary>The resend: the original request with intake's questions and the client's answers appended.</summary>
    private static RunRequest WithAnswers(RunRequest request, IReadOnlyList<string> questions, ElicitResult answers)
    {
        var sb = new StringBuilder(request.Text).AppendLine().AppendLine().AppendLine("Answers to the orchestrator's questions:");
        for (var i = 0; i < questions.Count; i++)
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {questions[i]} {(answers.Content?.TryGetValue($"q{i + 1}", out var a) == true ? a.ToString() : "(no answer)")}");
        return request with { Text = sb.ToString().TrimEnd() };
    }

    private static CallToolResult Output(ResultContract result)
    {
        var json = JsonSerializer.SerializeToElement(result, ContractJson.Options);
        return new CallToolResult { StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }

    /// <summary>What an input_required result carries in requestState until the client retries.</summary>
    private sealed record Pending(RunRequest Request, IReadOnlyList<string> Questions, string RunId, ResultContract Result);
}
