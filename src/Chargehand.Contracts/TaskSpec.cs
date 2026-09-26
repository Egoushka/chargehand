using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Contracts;

/// <summary>task-spec/v1: intake's output, one action per spec.</summary>
public sealed record TaskSpec(
    string ContractVersion,
    string Id,
    string Goal,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> AcceptanceCriteria,
    Risk Risk,
    Estimate Estimate,
    TaskAction Action,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] JsonElement? ActionDetail);

public enum Risk { Low, Medium, High }

public enum TaskAction { Answer, Split, Improve, Ask, Deny }

public sealed record Estimate(long TokensLow, long TokensHigh, decimal UsdLow, decimal UsdHigh, string Basis);
