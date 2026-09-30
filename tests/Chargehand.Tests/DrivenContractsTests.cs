using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;

namespace Chargehand.Tests;

/// <summary>Driven sessions (ADR 0039): the additive contract changes a batch of container sessions relies on.</summary>
public class DrivenContractsTests
{
    private const string Repo = "\"repository\":{\"path\":\"/r\",\"commit\":\"abcdef1\"}";

    private static string Request(string driven, bool repository = true) =>
        $$"""{"contract_version":"request/v1","text":"batch","context":{"interactive":false,"preset":"driven"{{(repository ? "," + Repo : "")}}},"driven":{{driven}}}""";

    private static IReadOnlyList<string> Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ContractSchemas.Validate(ContractSchemas.Request, doc.RootElement);
    }

    [Fact]
    public void A_request_with_two_tasks_validates_and_round_trips()
    {
        var json = Request("""{"tasks":[{"id":"t1","ref":"CHARGEHAND-12"},{"id":"t2","goal":"Add a retry"}],"max_parallel":2,"max_tokens_total":4000000,"max_usd_total":6.0,"draft_pr":true}""");
        Assert.Empty(Validate(json));
        var request = JsonSerializer.Deserialize<RunRequest>(json, ContractJson.Options)!;
        Assert.Equal(2, request.Driven!.Tasks.Count);
        Assert.Equal("CHARGEHAND-12", request.Driven.Tasks[0].Ref);
        Assert.Equal("Add a retry", request.Driven.Tasks[1].Goal);
        Assert.Equal(2, request.Driven.MaxParallel);
        Assert.Equal(4_000_000L, request.Driven.MaxTokensTotal);
        Assert.Equal(6.0m, request.Driven.MaxUsdTotal);
        Assert.Empty(Validate(JsonSerializer.Serialize(request, ContractJson.Options)));
    }

    [Theory]
    [InlineData("""{"tasks":[{"id":"t1"}]}""")]                                                            // neither ref nor goal
    [InlineData("""{"tasks":[]}""")]                                                                       // no tasks
    [InlineData("""{"tasks":[{"id":"t1","goal":"g"}],"draft_pr":false}""")]                                // not a draft
    [InlineData("""{"tasks":[{"id":"t1","goal":"g"}],"max_parallel":5}""")]                                // above the ceiling
    [InlineData("""{"tasks":[{"id":"T 1","goal":"g"}]}""")]                                                // id shape
    [InlineData("""{"tasks":[{"id":"t1","goal":"g","extra":1}]}""")]                                       // unknown member
    public void A_malformed_driven_block_is_refused(string driven) => Assert.NotEmpty(Validate(Request(driven)));

    [Fact]
    public void Twenty_one_tasks_are_refused()
    {
        var tasks = string.Join(",", Enumerable.Range(1, 21).Select(i => $$"""{"id":"t{{i}}","goal":"g"}"""));
        Assert.NotEmpty(Validate(Request($$"""{"tasks":[{{tasks}}]}""")));
    }

    [Fact]
    public void A_driven_request_needs_a_repository_by_the_rule_check()
    {
        var json = Request("""{"tasks":[{"id":"t1","goal":"g"}]}""", repository: false);
        Assert.Empty(Validate(json)); // the schema cannot say it without tightening request/v1
        var request = JsonSerializer.Deserialize<RunRequest>(json, ContractJson.Options)!;
        Assert.Contains(DrivenRules.Problems(request), p => p.Contains("context.repository"));
        var withRepo = JsonSerializer.Deserialize<RunRequest>(Request("""{"tasks":[{"id":"t1","goal":"g"}]}"""), ContractJson.Options)!;
        Assert.Empty(DrivenRules.Problems(withRepo));
    }

    [Fact]
    public void A_request_without_driven_is_unchanged()
    {
        Assert.Empty(Validate("""{"contract_version":"request/v1","text":"t","context":{"interactive":false,"preset":"default"}}"""));
    }

    [Fact]
    public void Duplicate_task_ids_are_named_by_the_rule_check()
    {
        var driven = new RequestDriven([new DrivenTask("a", null, "g"), new DrivenTask("a", "X-1", null), new DrivenTask("b", null, "g")]);
        var problems = DrivenRules.Problems(driven);
        Assert.Contains(problems, p => p.Contains("'a'") && p.Contains("more than once"));
        Assert.Empty(DrivenRules.Problems(new RequestDriven([new DrivenTask("a", null, "g"), new DrivenTask("b", null, "g")])));
    }

    [Theory]
    [InlineData(ErrorCode.ContainerUnavailable, "container_unavailable")]
    [InlineData(ErrorCode.CredentialUnavailable, "credential_unavailable")]
    [InlineData(ErrorCode.SessionFailed, "session_failed")]
    [InlineData(ErrorCode.SessionStalled, "session_stalled")]
    [InlineData(ErrorCode.PushRejected, "push_rejected")]
    [InlineData(ErrorCode.PrFailed, "pr_failed")]
    [InlineData(ErrorCode.Cancelled, "cancelled")]
    [InlineData(ErrorCode.TasksIncomplete, "tasks_incomplete")]
    public void The_new_error_codes_serialise_and_validate(ErrorCode code, string wire)
    {
        Assert.Equal($"\"{wire}\"", JsonSerializer.Serialize(code, ContractJson.Options));
        var schema = JsonDocument.Parse(ContractSchemas.Text(ContractSchemas.Result)).RootElement;
        var allowed = schema.GetProperty("properties").GetProperty("error").GetProperty("properties").GetProperty("code").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString());
        Assert.Contains(wire, allowed);
    }

    [Fact]
    public void The_new_run_status_events_and_fields_validate()
    {
        foreach (var ev in new[] { "container_started", "session_progress", "verify_finished", "pushed", "pr_opened", "task_finished" })
        {
            var json = $$"""{"contract_version":"run-status/v1","run_id":"r","status":"running","event":"{{ev}}","task_id":"t1","branch":"chargehand/r","pr_url":"https://example.test/pr/1","usd_total":0.5,"turns":12}""";
            using var doc = JsonDocument.Parse(json);
            Assert.Empty(ContractSchemas.Validate(ContractSchemas.RunStatus, doc.RootElement));
            var typed = JsonSerializer.Deserialize<RunStatus>(json, ContractJson.Options)!;
            Assert.Equal("t1", typed.TaskId);
            Assert.Equal(12, typed.Turns);
            Assert.NotNull(typed.Event);
        }
    }

    [Fact]
    public void A_run_summary_validates_and_round_trips()
    {
        const string json = """{"contract_version":"run-summary/v1","run_id":"r1","parent_run_id":"r0","task_ref":"CHARGEHAND-12","preset":"driven","status":"failed","started_at":"2026-09-30T12:00:00Z","finished_at":"2026-09-30T12:20:00Z","usd":null,"branch":"chargehand/r1","pr_url":null,"error_code":"push_rejected"}""";
        using var doc = JsonDocument.Parse(json);
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.RunSummary, doc.RootElement));
        var typed = doc.RootElement.Deserialize<RunSummary>(ContractJson.Options)!;
        Assert.Equal("r0", typed.ParentRunId);
        Assert.Equal(ErrorCode.PushRejected, typed.ErrorCode);
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.RunSummary, JsonSerializer.SerializeToElement(typed, ContractJson.Options)));
    }

    [Fact]
    public void A_profile_with_a_driven_block_loads_and_one_without_has_none()
    {
        var with = LoadProfile("""{"schema":"profile/v1","driven":{"enabled":true,"max_parallel":2,"max_parallel_total":4,"images":["reg.example/session@sha256:0000000000000000000000000000000000000000000000000000000000000000"],"network":{"allow":["registry.example"]},"runner":{"url":"http://runner.example:4310","api_key_secret":"chargehand-runner-key"},"push_secret":"chargehand-push-key"}}""");
        Assert.True(with.Driven!.Enabled);
        Assert.Equal(4, with.Driven.MaxParallelTotal);
        Assert.Equal(["registry.example"], with.Driven.Network!.Allow);
        Assert.Equal("chargehand-runner-key", with.Driven.Runner!.ApiKeySecret);
        Assert.Null(LoadProfile("""{"schema":"profile/v1"}""").Driven);
    }

    [Fact]
    public void The_driven_preset_loads_with_its_session_limits()
    {
        var preset = Preset.Load(Repo_.Path("presets"), "driven");
        Assert.Equal(45, preset.Driven!.MaxMinutes);
        Assert.Equal(10, preset.Driven.NoProgressMinutes);
        Assert.Equal(80, preset.Driven.MaxTurns);
        Assert.Contains("api.anthropic.com", preset.Driven.Allow!);
        Assert.Equal(4096, preset.Driven.MemoryMb);
        Assert.Null(Preset.Load(Repo_.Path("presets"), "code").Driven);
    }

    private static Profile LoadProfile(string json)
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "profile.json");
        File.WriteAllText(path, json);
        return Profile.Load(path);
    }

    private static class Repo_ { public static string Path(string p) => Chargehand.Tests.Repo.Path(p); }
}
