using System.Text.Json;
using System.Text.Json.Serialization;
using Chargehand.Contracts;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Tests;

/// <summary>The batch result's artifacts are written without reflection (ADR 0060) and byte for byte as the reflection options before wrote them, so their hashes do not change.</summary>
public class DrivenResultJsonTests
{
    private static readonly JsonSerializerOptions Before = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private static readonly TaskOutcome[] Outcomes =
    [
        new("t1", TaskState.Completed, "run-1", null, null, 0.42m, 1200, "chargehand/run-1", "https://example.test/o/r/pull/1", Runner: "box"),
        new("t2", TaskState.Failed, "run-2", "the tests failed \"twice\" <here>", ErrorCode.VerificationFailed, 1.5m, 900),
        new("t3", TaskState.NotStarted, null, "not started", ErrorCode.InvalidRequest),
    ];

    private static readonly BatchPlan Plan = new("request", "run-plan", "applied", null, ["listed db/migrations"], "a note",
        [new("t1", "L", "plan", [], "big", [new PlanAssignment("migration", "0042")], "kept"), new("t2", null, null, ["t1"])], ["t9: unknown task"]);

    private static readonly BatchRetro Retro = new("written", null, "run-retro", 2, "what went well");

    private static object? Usd(TaskOutcome t, bool priced) => priced ? t.Usd : null;

    /// <summary>The rows as the code before this wrote them: a dictionary per task, with the runner only when there is one.</summary>
    private static string BeforeRows(bool priced) => JsonSerializer.Serialize(new
    {
        credential_delivery = "token_exchange",
        tasks = Outcomes.Select(t =>
        {
            var row = new Dictionary<string, object?>
            {
                ["id"] = t.Id,
                ["run_id"] = t.RunId,
                ["status"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(t.State.ToString()),
                ["branch"] = t.Branch,
                ["pr_url"] = t.PrUrl,
                ["usd"] = Usd(t, priced),
                ["tokens"] = t.Tokens,
                ["error_code"] = t.Error is { } e ? JsonNamingPolicy.SnakeCaseLower.ConvertName(e.ToString()) : null,
                ["detail"] = t.Detail,
            };
            if (t.Runner is not null)
                row["runner"] = t.Runner;
            return row;
        }).ToList(),
    }, Before);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_artifacts_are_the_bytes_the_reflection_options_wrote(bool priced)
    {
        var result = DrivenResult.BuildBatch("run-b", new string('0', 32), "", new BatchOutcome(Outcomes, null), "token_exchange", priced, "2.1.283", plan: Plan, retro: Retro);

        var byKind = result.Artifacts.ToDictionary(a => a.Kind, a => a.Content);
        Assert.Equal(BeforeRows(priced), byKind["driven-batch"]);
        Assert.Equal(JsonSerializer.Serialize(Plan, Before), byKind["plan"]);
        Assert.Equal(JsonSerializer.Serialize(Retro, Before), byKind["retro"]);
    }
}
