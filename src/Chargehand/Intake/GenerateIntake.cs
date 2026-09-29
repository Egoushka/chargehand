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
public sealed class GenerateIntake(IWorkerRuntime runtime, ModelRef? model, PromptBlock block, string runId, bool needsRepository = true) : IIntake
{
    /// <summary>Characters of each caller input that intake reads. Intake runs on a small model, so the head of a long input (a diff)
    /// stands in for all of it; the worker gets the input whole (Orchestrator.Execute).</summary>
    public const int MaxInputChars = 2000;

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
        var prompt = $"{block.Text}\nJSON Schema:\n{ContractSchemas.Text(ContractSchemas.TaskSpec)}\n\nUse id \"{runId}\".\nRequest:\n{request.Text}\n{InputsText(request.Inputs)}";
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
        throw new ChargehandException(ErrorCode.IntakeFailed, $"intake returned no valid task-spec/v1 after one retry: {errors}",
            "Retry, or set the profile's intake_model to a stronger model.");
    }

    /// <summary>Each input's id, kind, size and text, so intake does not ask for facts the caller sent; the head only, and it says when it cut.</summary>
    private static string InputsText(IReadOnlyList<CallerInput>? inputs)
    {
        if (inputs is not { Count: > 0 })
            return "";
        var lines = inputs.Select(i =>
        {
            if (i.Text.Length <= MaxInputChars)
                return $"- id \"{i.Id}\" ({i.Kind}, {i.Text.Length} characters): {i.Text}";
            // A cut inside a surrogate pair would leave half of it, which the request body cannot encode.
            var head = i.Text[..(char.IsHighSurrogate(i.Text[MaxInputChars - 1]) ? MaxInputChars - 1 : MaxInputChars)];
            return $"- id \"{i.Id}\" ({i.Kind}, {i.Text.Length} characters, cut to the first {head.Length} here; the worker gets all of it): {head}";
        });
        return $"\nThe caller supplies these inputs to use:\n{string.Join("\n", lines)}\n";
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
