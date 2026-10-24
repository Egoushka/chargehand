using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Chargehand.Bench;

/// <summary><c>bench-results/v1</c>: what one run of a task set measured, as facts. The report derives every rate from these, so a result file can be rendered again, and two of them compared,
/// by a later version of the report.</summary>
/// <param name="Complete">False while the run is unfinished (the file is rewritten after each repository), or when it stopped early.</param>
/// <param name="Arm"><c>bare</c> for the control arm (ADR 0080); left out of the file for chargehand's own arm, so those files keep the shape they had.</param>
public sealed record BenchResults(
    string Schema,
    string Taskset,
    string TasksetSha256,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    bool Complete,
    IReadOnlyList<RepositoryPin> Repositories,
    IReadOnlyList<TaskResult> Tasks,
    string? Arm = null)
{
    public const string SchemaId = "bench-results/v1";
    public const string ChargehandArm = "chargehand", BareArm = "bare";

    [JsonIgnore]
    public string ArmName => Arm ?? ChargehandArm;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        WriteIndented = true,
    };

    public double WallSeconds => FinishedAt is { } end ? (end - StartedAt).TotalSeconds : 0;

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static BenchResults? Parse(string json) => JsonSerializer.Deserialize<BenchResults>(json, Options);
}

public sealed record RepositoryPin(string Name, string Ecosystem, string Url, string Commit);

/// <param name="State">How the driven task ended: <c>completed</c> (a draft pull request), <c>failed</c>, <c>needs_input</c>, <c>not_started</c>, <c>cancelled</c>; <c>not_run</c> when
/// its batch never produced a result for it.</param>
/// <param name="Branch">The branch the session pushed; null: it pushed none.</param>
/// <param name="Acceptance">Null when no acceptance check ran (no branch, or the batch did not finish).</param>
/// <param name="AskedQuestion">The session ended with a question for a person.</param>
/// <param name="Answered">The question was answered in a second batch and that batch ran.</param>
/// <param name="TestIntegrityFlagged">Chargehand's flag: every changed file is a test or build file for a goal that did not ask for that.</param>
/// <param name="UsdEstimate">The dollar equivalent of the session's tokens at the profile's prices; null when the profile has no price table. Never a bill.</param>
/// <param name="Tokens">Input plus output tokens over all the task's sessions.</param>
/// <param name="Turns">Assistant messages over all the task's sessions.</param>
/// <param name="Continuations">Sessions started after the first because an earlier one stopped with work to keep.</param>
/// <param name="Governor">What the governor logged; empty when it was off or the image predates it.</param>
/// <param name="WallSeconds">From the first session container to the task's end, both batches for a question task.</param>
public sealed record TaskResult(
    string Id,
    string Repository,
    string Ecosystem,
    string Size,
    string Kind,
    string State,
    string? ErrorCode,
    string? Detail,
    string? Branch,
    string? PrUrl,
    AcceptanceResult? Acceptance,
    bool AskedQuestion,
    bool Answered,
    bool TestIntegrityFlagged,
    string? TestIntegrityReason,
    decimal? UsdEstimate,
    bool UsdEstimatePartial,
    long Tokens,
    long CacheRead,
    long CacheWrite,
    int Turns,
    int Continuations,
    IReadOnlyList<GovernorDecisionRecord> Governor,
    double WallSeconds,
    string Outcome);

public sealed partial record AcceptanceResult(bool Passed, int ExitCode, bool TimedOut, double DurationSeconds, string OutputTail)
{
    /// <summary>The command could not run (126 not executable, 127 not found): the image lacks a tool. That says nothing about the change, so no expectation of a pass or a fail applies.</summary>
    [JsonIgnore]
    public bool SetupFailure => !TimedOut && ExitCode is 126 or 127;

    /// <summary>The program that was missing: the name a shell or the verifier reported, else the command's own first word.</summary>
    public string MissingTool(IReadOnlyList<string> argv)
    {
        var m = NotFound().Match(OutputTail);
        return m.Success ? m.Groups["tool"].Value : argv.Count > 0 ? argv[0] : "unknown";
    }

    [GeneratedRegex(@"(?:(?<tool>[\w./+-]+): (?:command )?not found|command not found: (?<tool>[\w./+-]+))")]
    private static partial Regex NotFound();
}

/// <param name="Acted">The governor really stopped the session (enforce mode); false: it only logged what it would do.</param>
/// <param name="Action"><c>warn</c>, <c>checkpoint</c> or <c>stop</c>.</param>
public sealed record GovernorDecisionRecord(string Action, string Signal, string Rule, int Turn, bool Acted, decimal? SpendUsd = null, decimal? BudgetUsd = null)
{
    /// <summary>A checkpoint or a stop: the decisions that end a session when the governor enforces. A warn never does.</summary>
    [JsonIgnore]
    public bool WouldEnd => Action is "checkpoint" or "stop";
}
