using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Chargehand.Server;

/// <summary>
/// MCP tool "orchestrate": inputSchema request/v1, outputSchema result/v1, the result in structuredContent (ADR 0014).
/// A client that opts in to the tasks extension gets long runs as tasks. When intake answers an interactive request
/// with questions (ask), the tool asks the client and resends the request with the answers: inside a task through
/// elicitation, which the task reports as input_required; outside one through an input_required result that the
/// client retries with the answers (MCP 2026-07-28). A non-interactive request gets result/v1 with needs_input.
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
            Description = "Run a request through chargehand: intake turns it into a Task Spec, OpenCode workers answer it, and the result "
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
            return answers?.Action == "accept"
                ? Output((await Run(runs, WithAnswers(pending.Request, pending.Questions, answers), pending.RunId, ct)).Result)
                : Output(pending.Result);
        }

        var (request, errors) = runs.Validate(JsonSerializer.SerializeToElement(call.Arguments ?? new Dictionary<string, JsonElement>()));
        if (request is null)
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = $"invalid request/v1: {string.Join("; ", errors)}" }] };

        var (result, run) = await Run(runs, request, null, ct);
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
            return answers.Action == "accept" ? Output((await Run(runs, WithAnswers(request, questions, answers), run.Id, ct)).Result) : Output(result);
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

    /// <summary>A run outlives the call: a cancelled call stops waiting, and the run stays readable at /v1/runs/{id}.</summary>
    private static async Task<(ResultContract Result, RunHandle Run)> Run(RunService runs, RunRequest request, string? parentRunId, CancellationToken ct)
    {
        var run = runs.Start(request, parentRunId) ?? throw new McpException($"{RunService.MaxUnfinished} runs are unfinished; retry later");
        return (await run.Done.WaitAsync(ct), run);
    }

    /// <summary>The resend: the original request with intake's questions and the client's answers appended.</summary>
    private static RunRequest WithAnswers(RunRequest request, IReadOnlyList<string> questions, ElicitResult answers)
    {
        var sb = new StringBuilder(request.Text).AppendLine().AppendLine().AppendLine("Answers to the orchestrator's questions:");
        for (var i = 0; i < questions.Count; i++)
            sb.AppendLine($"- {questions[i]} {(answers.Content?.TryGetValue($"q{i + 1}", out var a) == true ? a.ToString() : "(no answer)")}");
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
