using System.Diagnostics;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Prompts;
using Chargehand.Results;
using Chargehand.Runtime;

namespace Chargehand.Intake;

/// <summary>Result of intake: a spec, or the reason the request cannot run yet.</summary>
public sealed record IntakeOutcome(TaskSpec? Spec, string? NeedsInput, IReadOnlyList<IntakeCall> Calls);

public sealed record IntakeCall(DateTimeOffset Started, double LatencyMs, bool Valid);

/// <summary>
/// Deterministic checks, then one stateless generate call through OpenCode (ADR 0005), schema-validated with one
/// retry. Only "answer" executes in v0; the caller logs the action intake chose.
/// </summary>
/// <param name="needsRepository">False for node kinds that run without a checkout (the draft preset).</param>
public sealed class GenerateIntake(IWorkerRuntime runtime, ModelRef model, PromptBlock block, string runId, bool needsRepository = true) : IIntake
{
    public PromptBlock Block => block;

    public async Task<TaskSpec> CreateSpecAsync(RunRequest request, CancellationToken ct) =>
        (await RunAsync(request, ct)).Spec ?? throw new InvalidOperationException("intake needs input");

    public async Task<IntakeOutcome> RunAsync(RunRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return new(null, "The request text is empty.", []);
        if (needsRepository && request.Context.Repository is null)
            return new(null, "The request names no repository checkout and commit (context.repository).", []);

        var calls = new List<IntakeCall>();
        // Intake sees which inputs a program caller sent (ids and kinds, not their text): without them it asks for facts it has.
        var inputs = request.Inputs is { Count: > 0 } i ? $"\nThe caller supplies these inputs to use: {string.Join(", ", i.Select(x => $"{x.Id} ({x.Kind})"))}.\n" : "";
        var prompt = $"{block.Text}\nJSON Schema:\n{ContractSchemas.Text(ContractSchemas.TaskSpec)}\n\nUse id \"{runId}\".\nRequest:\n{request.Text}\n{inputs}";
        string? errors = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var started = DateTimeOffset.UtcNow;
            var sw = Stopwatch.StartNew();
            var text = await runtime.GenerateAsync(model, errors is null ? prompt : $"{prompt}\nYour previous output was invalid: {errors}\nReturn the corrected JSON object only.", ct);
            var (spec, problems) = Parse(text);
            calls.Add(new IntakeCall(started, sw.Elapsed.TotalMilliseconds, spec is not null));
            if (spec is not null)
                return new(spec with { Id = runId }, null, calls);
            errors = string.Join("; ", problems);
        }
        throw new InvalidOperationException($"intake returned no valid task-spec/v1 after one retry: {errors}");
    }

    internal static (TaskSpec? Spec, IReadOnlyList<string> Errors) Parse(string text)
    {
        var raw = ResultBlocks.ExtractLast(text) ?? text.Trim();
        JsonElement element;
        try
        {
            element = JsonDocument.Parse(raw).RootElement;
        }
        catch (JsonException e)
        {
            return (null, [$"not JSON: {e.Message}"]);
        }
        var errors = ContractSchemas.Validate(ContractSchemas.TaskSpec, element);
        return errors.Count > 0 ? (null, errors) : (element.Deserialize<TaskSpec>(ContractJson.Options), []);
    }
}
