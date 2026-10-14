using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Batches.Tests;

/// <summary>ADR 0060: the planner's answer, the retro's lines and the kept-work artifact are read and written without reflection, byte for byte as the reflection serializer did with the
/// same options.</summary>
public sealed class BatchesJsonContextTests
{
    // Characters the default encoder escapes, so a difference in encoders shows.
    private const string Text = "a <b> & \"c\" — ü";

    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private sealed record Wire(IReadOnlyList<ProposedTask>? Tasks, IReadOnlyList<string>? Checked, string? Note);

    public static TheoryData<string> Plans() => new()
    {
        """{"checked": ["listed db/migrations: latest is 0041"], "tasks": [{"id": "t1", "size": "L", "split": [{"goal": "first", "size": "M"}, {"goal": "second"}], "assign": [{"what": "migration number", "value": "0042", "part": 1}]}], "note": "n"}""",
        """{"Tasks": [{"ID": "a", "Size": "s",}], /* a comment */ "CHECKED": [],}""",
        """{"tasks": [{"id": "a", "assign": [{"what": "adr", "value": "61", "part": null}]}], "later_field": {"x": 1}}""",
        """{}""",
        """{"tasks": null, "checked": null, "note": "a <b> & \"c\" — ü"}""",
    };

    [Theory]
    [MemberData(nameof(Plans))]
    public void A_plan_reads_as_before(string json)
    {
        var old = JsonSerializer.Deserialize<Wire>(json, WireJson);
        var expected = new PlanProposal(old?.Tasks ?? [], old?.Checked ?? [], old?.Note);

        Assert.Equal(JsonSerializer.Serialize(expected, ContractJson.Options), JsonSerializer.Serialize(ModelPlanner.Parse($"Here it is:\n```json\n{json}\n```"), ContractJson.Options));
    }

    [Theory]
    [InlineData("""{"tasks": [}""")]
    [InlineData("""{"tasks": [{"id": 7}]}""")]
    [InlineData("""{"tasks": [{"id": "a", "assign": [{"part": "one"}]}]}""")]
    [InlineData("""{"checked": "one line"}""")]
    public void A_plan_that_is_not_valid_fails_with_the_same_message(string json)
    {
        var old = Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<Wire>(json, WireJson));

        Assert.Equal($"the plan is not valid JSON: {old.Message}", Assert.Throws<PlanException>(() => ModelPlanner.Parse(json)).Message);
    }

    private static LearningRecord Record(string id, decimal? budget, decimal? usd, string? stop, string? mode, params GovernorCount[] governor) =>
        new("run-batch", id, $"run-{id}", DateTimeOffset.UnixEpoch, "o/r", "L", "plan", new LearningBudget(budget, new BudgetSource("history", 5, 80)),
            new LearningSpend(usd, true, 10, 5, 1, 2, 30, 1), LearningOutcome.Partial, stop, mode, governor, true);

    [Fact]
    public void The_retro_lines_are_written_as_before()
    {
        LearningRecord[] records =
        [
            Record(Text, 4m, 2.5m, "turn_limit", "enforce", new GovernorCount("spend", "checkpoint", 2, 1), new GovernorCount(Text, "log", 1, 0)),
            Record("t2", null, null, null, null),
        ];

        var lines = ModelRetro.RetroPrompt(records).Split('\n', StringSplitOptions.RemoveEmptyEntries)[^records.Length..];

        Assert.Equal(records.Select(Old), lines.Select(l => l.TrimEnd('\r')));
    }

    // The line as the retro wrote it before, with an anonymous type and the reflection serializer.
    private static string Old(LearningRecord r) => JsonSerializer.Serialize(new
    {
        id = r.TaskId,
        size = r.SizeClass,
        size_from = r.SizeFrom,
        budget_usd = r.Budget.Usd,
        budget_source = r.Budget.Source.Kind,
        usd = r.Spend.UsdEstimate,
        turns = r.Spend.Turns,
        continuations = r.Spend.Continuations,
        outcome = r.Outcome,
        stop_reason = r.StopReason,
        governor_mode = r.GovernorMode,
        governor = r.Governor.Select(g => new { g.Signal, g.Action, g.Count, g.Acted }),
        test_integrity_flagged = r.TestIntegrityFlagged,
    }, ContractJson.Options);

    private static ResultContract WithKeptWork(string content) =>
        new("result/v1", "run-1", "session", "trace", new PromptChain([], new AsSent("2.0.16", "session", "m", "2026-10-14")), ResultStatus.Completed, "s", [], [], [new Artifact(ContinuationSource.KeptWorkKind, "application/json", "sha", Content: content)], [], 0.5,
            new Usage(0, 0, 0, 0, 0));

    [Theory]
    [InlineData("""{"branch":"chargehand/run-1","bundle_run":"run-2","commits":3,"base":"abc1234"}""")]
    [InlineData("""{"branch":"a <b> & \"c\" — ü","bundle_run":"run-2","commits":0,"base":"abc1234","later_field":true}""")]
    [InlineData("""{"Branch":"chargehand/run-1","commits":1}""")]
    [InlineData("""{}""")]
    public void The_kept_work_artifact_reads_as_before(string content)
    {
        var old = JsonSerializer.Deserialize<KeptWork>(content, ContractJson.Options);

        Assert.Equal(old, ContinuationSource.KeptWorkOf(WithKeptWork(content)));
    }

    [Theory]
    [InlineData("""{"commits":"three"}""")]
    [InlineData("""not json""")]
    public void A_kept_work_artifact_that_cannot_be_read_is_none_as_before(string content)
    {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<KeptWork>(content, ContractJson.Options));

        Assert.Null(ContinuationSource.KeptWorkOf(WithKeptWork(content)));
    }
}
