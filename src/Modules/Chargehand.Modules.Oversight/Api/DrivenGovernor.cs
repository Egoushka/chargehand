using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Microsoft.Extensions.Options;

namespace Chargehand.Modules.Oversight.Api;

/// <summary>What the governor does with its decisions: <c>off</c> makes none, <c>observe</c> logs them to <c>governor.jsonl</c> and acts on none (ADR 0045's first release),
/// <c>enforce</c> also stops the session on a checkpoint or a stop.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<GovernorMode>))]
public enum GovernorMode { Off, Observe, Enforce }

/// <summary>The governor's settings for every session (ADR 0045). Profile keys, not preset keys, for the reason <c>driven.limits</c> gives. Each threshold left out keeps
/// the session governor's default.</summary>
/// <param name="Mode"><c>off</c>, <c>observe</c> (the default: decisions are logged, none acted on) or <c>enforce</c> (a checkpoint or a stop ends the session).</param>
/// <param name="Budgets">The flat budget per size class (S, M, L) in the dollar equivalent, until enough history exists for a percentile.</param>
/// <param name="History">When a size class has enough finished tasks, its budget is a percentile of their spend; null: the defaults of <see cref="DrivenGovernorHistory"/>.</param>
public sealed record DrivenGovernor(GovernorMode Mode = GovernorMode.Observe, IReadOnlyDictionary<string, decimal>? Budgets = null, int? RepeatCalls = null,
    int? RepeatFailures = null, int? FailingCalls = null, int? ProbeCommands = null, int? NoGrowthTurns = null, int? RewarnTurns = null, long? ContextTokens = null,
    decimal? CheckpointAt = null, IReadOnlyList<string>? ProbePatterns = null, IReadOnlyList<string>? WaitPatterns = null, DrivenGovernorHistory? History = null) : IValidatableObject
{
    public static IReadOnlyDictionary<string, decimal> DefaultBudgets { get; } = new Dictionary<string, decimal> { ["S"] = 1.5m, ["M"] = 4m, ["L"] = 10m };

    /// <summary>Throws an invalid_request error naming the key when a value cannot work: a count below 1, a share outside (0, 1], a budget not above 0, a pattern that does not compile.</summary>
    public void Validate() => SettingsValidation.ThrowIfFailed(new DrivenGovernorValidator().Validate(null, this), "driven.governor",
        "Fix driven.governor in the profile; docs/guide/driven.md lists the keys.");

    IEnumerable<ValidationResult> IValidatableObject.Validate(ValidationContext validationContext) => SettingsValidation.Results(Problems());

    private List<string> Problems()
    {
        var problems = new List<string>();
        foreach (var (key, value) in new (string, long?)[] { ("repeat_calls", RepeatCalls), ("repeat_failures", RepeatFailures), ("failing_calls", FailingCalls),
            ("probe_commands", ProbeCommands), ("no_growth_turns", NoGrowthTurns), ("rewarn_turns", RewarnTurns), ("context_tokens", ContextTokens) })
            if (value is < 1)
                problems.Add($"{key} must be at least 1");
        if (CheckpointAt is <= 0 or > 1)
            problems.Add("checkpoint_at must be above 0 and at most 1");
        foreach (var (size, budget) in Budgets ?? new Dictionary<string, decimal>())
            if (size is not ("S" or "M" or "L") || budget <= 0)
                problems.Add($"budgets.{size} must be one of S, M, L with a value above 0");
        History?.Validate(problems);
        foreach (var pattern in (ProbePatterns ?? []).Concat(WaitPatterns ?? []))
            try
            {
                _ = new Regex(pattern);
            }
            catch (ArgumentException)
            {
                problems.Add($"pattern '{pattern}' is not a regular expression");
            }
        return problems;
    }
}

/// <summary><c>driven.governor</c>'s validator, source-generated (ADR 0060).</summary>
[OptionsValidator]
public sealed partial class DrivenGovernorValidator : IValidateOptions<DrivenGovernor>;

/// <summary>Budgets from past spend (ADR 0045): once a size class has <paramref name="MinTasks"/> finished tasks in the last <paramref name="WindowDays"/> days for the repository (else for all
/// repositories), its budget is the <paramref name="Percentile"/>th percentile of their spend; below that the flat budget applies, and a task's own <c>budget_usd</c> always wins.</summary>
public sealed record DrivenGovernorHistory(int MinTasks = 20, int WindowDays = 60, int Percentile = 80)
{
    internal void Validate(List<string> problems)
    {
        if (MinTasks < 1) problems.Add("history.min_tasks must be at least 1");
        if (WindowDays < 1) problems.Add("history.window_days must be at least 1");
        if (Percentile is < 1 or > 100) problems.Add("history.percentile must be 1 to 100");
    }
}
