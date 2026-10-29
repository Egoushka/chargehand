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
    [InlineData("""{"tasks":[{"id":"t1","goal":"g"}],"max_parallel":17}""")]                               // above the ceiling
    [InlineData("""{"tasks":[{"id":"T 1","goal":"g"}]}""")]                                                // id shape
    [InlineData("""{"tasks":[{"id":"t1","goal":"g","extra":1}]}""")]                                       // unknown member
    public void A_malformed_driven_block_is_refused(string driven) => Assert.NotEmpty(Validate(Request(driven)));

    [Fact]
    public void Sixteen_tasks_at_once_are_allowed_by_the_schema()
    {
        Assert.Empty(Validate(Request("""{"tasks":[{"id":"t1","goal":"g"}],"max_parallel":16}""")));
    }

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

    [Fact]
    public void A_task_that_continues_another_validates_without_a_goal_and_round_trips_its_answers()
    {
        var json = Request("""{"tasks":[{"id":"t1","continue_from":{"run_id":"run-20261012-101010-abcdef","task_id":"t1"},"answers":["Postgres","Yes"]}]}""");
        Assert.Empty(Validate(json));
        var task = JsonSerializer.Deserialize<RunRequest>(json, ContractJson.Options)!.Driven!.Tasks.Single();
        Assert.Equal(new ContinueFrom("run-20261012-101010-abcdef", "t1"), task.ContinueFrom);
        Assert.Equal(["Postgres", "Yes"], task.Answers);
        Assert.Contains("\"continue_from\":{\"run_id\":", JsonSerializer.Serialize(task, ContractJson.Options));
    }

    [Fact]
    public void A_tasks_acceptance_criteria_validate_and_round_trip()
    {
        var json = Request("""{"tasks":[{"id":"t1","goal":"g","acceptance":[{"command":"./smoke.sh","output":"ok"},{"command":"test -f x","exit_code":1}]}]}""");
        Assert.Empty(Validate(json));
        var task = JsonSerializer.Deserialize<RunRequest>(json, ContractJson.Options)!.Driven!.Tasks.Single();
        Assert.Equal([new AcceptanceCheck("./smoke.sh", 0, "ok"), new AcceptanceCheck("test -f x", 1)], task.Acceptance);
        Assert.NotEmpty(Validate(Request("""{"tasks":[{"id":"t1","goal":"g","acceptance":[{"exit_code":0}]}]}""")));
        Assert.NotEmpty(Validate(Request("""{"tasks":[{"id":"t1","goal":"g","acceptance":[{"command":"x","shell":"bash"}]}]}""")));
        Assert.Contains(DrivenRules.Problems(new RequestDriven([new DrivenTask("t1", Goal: "g", Acceptance: [new AcceptanceCheck(" ")])])),
            p => p == "task 't1': an acceptance check needs a command");
    }

    [Theory]
    [InlineData("true")]
    [InlineData("exit 0")]
    [InlineData(":")]
    [InlineData("echo")]
    [InlineData("echo all good")]
    public void An_acceptance_check_that_checks_nothing_is_refused_by_the_rule_check(string noOp)
    {
        var problems = DrivenRules.Problems(new RequestDriven([new DrivenTask("t1", Goal: "g", Acceptance: [new AcceptanceCheck(noOp)])]));
        Assert.Contains(problems, p => p == $"task 't1': acceptance check '{noOp}' checks nothing (CHARGEHAND-258); name a command that can fail");
    }

    [Fact]
    public void An_acceptance_check_that_can_fail_is_not_refused()
    {
        Assert.Empty(DrivenRules.Problems(new RequestDriven([new DrivenTask("t1", Goal: "g", Acceptance: [new AcceptanceCheck("./smoke.sh"), new AcceptanceCheck("npm test && echo done"), new AcceptanceCheck("echo building && pytest")])])));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("exit 0")]
    [InlineData("echo")]
    [InlineData("echo nothing to see here")]
    public void A_context_verify_that_checks_nothing_is_refused_by_the_rule_check(string noOp)
    {
        var request = new RunRequest("request/v1", "t",
            new RequestContext(false, "driven", Repository: new RepositoryRef("/r", "abcdef1"), Verify: ["sh", "-c", noOp]),
            Driven: new RequestDriven([new DrivenTask("t1", Goal: "g")]));
        Assert.Contains(DrivenRules.Problems(request), p => p.Contains("checks nothing") && p.Contains("CHARGEHAND-258"));
    }

    [Fact]
    public void A_context_verify_that_can_fail_is_not_refused()
    {
        var request = new RunRequest("request/v1", "t",
            new RequestContext(false, "driven", Repository: new RepositoryRef("/r", "abcdef1"), Verify: ["dotnet", "test"]),
            Driven: new RequestDriven([new DrivenTask("t1", Goal: "g")]));
        Assert.Empty(DrivenRules.Problems(request));
    }

    [Theory]
    [InlineData("""{"tasks":[{"id":"t1","goal":"g","answers":["a"]}]}""", false)]                                                  // answers need continue_from (a rule, not the schema)
    [InlineData("""{"tasks":[{"id":"t1","continue_from":{"run_id":"r1"}}]}""", true)]                                              // task_id missing
    [InlineData("""{"tasks":[{"id":"t1","continue_from":{"run_id":"R 1","task_id":"t1"}}]}""", true)]                              // run id shape
    [InlineData("""{"tasks":[{"id":"t1","continue_from":{"run_id":"r1","task_id":"t1","x":1}}]}""", true)]                         // unknown member
    [InlineData("""{"tasks":[{"id":"t1","continue_from":{"run_id":"r1","task_id":"t1"},"answers":[""]}]}""", true)]                // empty answer
    [InlineData("""{"tasks":[{"id":"t1","continue_from":{"run_id":"r1","task_id":"t1"},"answers":"Postgres"}]}""", true)]          // answers is a list
    public void A_malformed_continuation_is_refused_by_the_schema_or_the_rule_check(string driven, bool bySchema)
    {
        Assert.Equal(bySchema, Validate(Request(driven)).Count > 0);
        if (!bySchema)
            Assert.Contains(DrivenRules.Problems(JsonSerializer.Deserialize<RunRequest>(Request(driven), ContractJson.Options)!), p => p.Contains("answers need continue_from"));
    }

    [Fact]
    public void The_rule_check_names_the_continuation_mistakes_the_schema_cannot()
    {
        var from = new ContinueFrom("run-1", "t1");
        var problems = DrivenRules.Problems(new RequestDriven([
            new DrivenTask("a", Goal: "g", ContinueFrom: from),
            new DrivenTask("b", ContinueFrom: from, Answers: [" "]),
            new DrivenTask("c", ContinueFrom: new ContinueFrom("Run 1", "t1")),
            new DrivenTask("d", Goal: "g", Answers: ["x"])]));
        Assert.Contains(problems, p => p == "task 'a': continue_from takes the earlier task's goal; leave out goal and ref");
        Assert.Contains(problems, p => p == "task 'b': answers must not be empty");
        Assert.Contains(problems, p => p == "task 'c': continue_from needs a run id and a task id");
        Assert.Contains(problems, p => p == "task 'd': answers need continue_from");
        Assert.Contains(problems, p => p == "tasks 'a', 'b' all continue run-1/t1: they would share its branch");
        Assert.Empty(DrivenRules.Problems(new RequestDriven([new DrivenTask("a", ContinueFrom: from, Answers: ["x"]), new DrivenTask("b", ContinueFrom: new ContinueFrom("run-1", "t2"))])));
    }

    [Fact]
    public void Task_dependencies_and_the_plan_flag_validate_and_round_trip()
    {
        var json = Request("""{"tasks":[{"id":"a","goal":"g"},{"id":"b","goal":"g","depends_on":["a"]},{"id":"c","goal":"g","depends_on":["a","b"]}],"plan":true}""");
        Assert.Empty(Validate(json));
        var request = JsonSerializer.Deserialize<RunRequest>(json, ContractJson.Options)!;
        Assert.Equal(["a", "b"], request.Driven!.Tasks[2].DependsOn);
        Assert.True(request.Driven.Plan);
        Assert.Empty(DrivenRules.Problems(request));
        Assert.Empty(Validate(JsonSerializer.Serialize(request, ContractJson.Options)));
    }

    [Theory]
    [InlineData("""{"tasks":[{"id":"a","goal":"g","depends_on":["A B"]}]}""")]                              // id shape
    [InlineData("""{"tasks":[{"id":"a","goal":"g"},{"id":"b","goal":"g","depends_on":["a","a"]}]}""")]       // repeated
    [InlineData("""{"tasks":[{"id":"a","goal":"g","depends_on":"b"}]}""")]                                  // not a list
    [InlineData("""{"tasks":[{"id":"a","goal":"g"}],"plan":"yes"}""")]                                      // not a boolean
    public void A_malformed_dependency_or_plan_flag_is_refused(string driven) => Assert.NotEmpty(Validate(Request(driven)));

    [Theory]
    [InlineData("""{"tasks":[{"id":"a","goal":"g","depends_on":["zzz"]}]}""", "task 'a': depends_on names 'zzz', which is not a task of this batch")]
    [InlineData("""{"tasks":[{"id":"a","goal":"g","depends_on":["a"]}]}""", "task 'a': depends_on names the task itself")]
    [InlineData("""{"tasks":[{"id":"a","goal":"g"},{"id":"b","goal":"g","depends_on":["a","a"]}]}""", "task 'b': depends_on names 'a' more than once")]
    [InlineData("""{"tasks":[{"id":"a","goal":"g","depends_on":["c"]},{"id":"b","goal":"g","depends_on":["a"]},{"id":"c","goal":"g","depends_on":["b"]}]}""", "tasks depend on each other in a cycle: a -> c -> b -> a")]
    [InlineData("""{"tasks":[{"id":"a","goal":"g"},{"id":"b","continue_from":{"run_id":"run-1","task_id":"x"},"answers":["y"],"depends_on":["a"]}]}""", "task 'b': depends_on and continue_from start from different work; use one")]
    public void The_rule_check_names_an_unknown_dependency_a_self_dependency_a_repeat_a_cycle_and_a_mix_with_continue_from(string driven, string problem)
    {
        var request = JsonSerializer.Deserialize<RunRequest>(Request(driven), ContractJson.Options)!;
        Assert.Contains(problem, DrivenRules.Problems(request));
    }

    [Fact]
    public void A_diamond_of_dependencies_is_not_a_cycle()
    {
        var request = JsonSerializer.Deserialize<RunRequest>(Request("""{"tasks":[{"id":"a","goal":"g"},{"id":"b","goal":"g","depends_on":["a"]},{"id":"c","goal":"g","depends_on":["a"]},{"id":"d","goal":"g","depends_on":["b","c"]}]}"""), ContractJson.Options)!;
        Assert.Empty(DrivenRules.Problems(request));
    }

    [Fact]
    public void The_plan_event_validates()
    {
        const string json = """{"contract_version":"run-status/v1","run_id":"r","status":"running","event":"plan","detail":"plan: t1 S."}""";
        using var doc = JsonDocument.Parse(json);
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.RunStatus, doc.RootElement));
        Assert.Equal(RunEventKind.Plan, JsonSerializer.Deserialize<RunStatus>(json, ContractJson.Options)!.Event);
    }

    [Fact]
    public void The_question_event_validates()
    {
        const string json = """{"contract_version":"run-status/v1","run_id":"r","status":"running","event":"question","task_id":"t1","detail":"task t1: the session asked:\n- Which database?"}""";
        using var doc = JsonDocument.Parse(json);
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.RunStatus, doc.RootElement));
        Assert.Equal(RunEventKind.Question, JsonSerializer.Deserialize<RunStatus>(json, ContractJson.Options)!.Event);
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
    [InlineData(ErrorCode.MergeConflict, "merge_conflict")]
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
    public void A_session_progress_event_carries_the_cache_counts_and_a_dollar_estimate_beside_usd()
    {
        const string json = """{"contract_version":"run-status/v1","run_id":"r","status":"running","event":"session_progress","task_id":"t1","tokens":48200,"cache_read":3500000,"cache_write":90000,"usd_estimate":1.75}""";
        using var doc = JsonDocument.Parse(json);
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.RunStatus, doc.RootElement));
        var typed = JsonSerializer.Deserialize<RunStatus>(json, ContractJson.Options)!;
        Assert.Equal((1.75m, 3_500_000L, 90_000L, null), (typed.UsdEstimate, typed.CacheRead, typed.CacheWrite, typed.Usd));
        Assert.Contains("\"usd_estimate\":1.75", JsonSerializer.Serialize(typed, ContractJson.Options));
    }

    [Fact]
    public void A_task_event_names_its_runner()
    {
        const string json = """{"contract_version":"run-status/v1","run_id":"r","status":"running","event":"container_started","task_id":"t1","detail":"task t1: session container started on runner 'laptop'","runner":"laptop"}""";
        using var doc = JsonDocument.Parse(json);
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.RunStatus, doc.RootElement));
        var typed = JsonSerializer.Deserialize<RunStatus>(json, ContractJson.Options)!;
        Assert.Equal("laptop", typed.Runner);
        Assert.Contains("\"runner\":\"laptop\"", JsonSerializer.Serialize(typed, ContractJson.Options));
        Assert.DoesNotContain("\"runner\"", JsonSerializer.Serialize(typed with { Runner = null }, ContractJson.Options), StringComparison.Ordinal);

        using var empty = JsonDocument.Parse(json.Replace("\"runner\":\"laptop\"", "\"runner\":\"\"", StringComparison.Ordinal));
        Assert.NotEmpty(ContractSchemas.Validate(ContractSchemas.RunStatus, empty.RootElement));
    }

    [Fact]
    public void A_session_progress_event_carries_tokens_and_a_stage()
    {
        const string json = """{"contract_version":"run-status/v1","run_id":"r","status":"running","event":"session_progress","task_id":"t1","tokens":48200,"turns":12,"stage":"write"}""";
        using var doc = JsonDocument.Parse(json);
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.RunStatus, doc.RootElement));
        var typed = JsonSerializer.Deserialize<RunStatus>(json, ContractJson.Options)!;
        Assert.Equal((48200L, SessionStage.Write), (typed.Tokens, typed.Stage));
        Assert.Contains("\"stage\":\"write\"", JsonSerializer.Serialize(typed, ContractJson.Options));

        using var bad = JsonDocument.Parse(json.Replace("\"write\"", "\"deploy\"", StringComparison.Ordinal));
        Assert.NotEmpty(ContractSchemas.Validate(ContractSchemas.RunStatus, bad.RootElement));
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
    public void Run_and_session_call_concurrency_default_to_one_and_two_and_load_from_the_profile()
    {
        var defaults = LoadProfile("""{"schema":"profile/v1","http":{"api_key_secret":"k"},"driven":{"enabled":true}}""");
        Assert.Equal(1, defaults.Http!.MaxConcurrentRuns);
        Assert.Equal(2, defaults.Driven!.MaxSessionCalls);

        var raised = LoadProfile("""{"schema":"profile/v1","http":{"api_key_secret":"k","max_concurrent_runs":3},"driven":{"enabled":true,"max_session_calls":6}}""");
        Assert.Equal(3, raised.Http!.MaxConcurrentRuns);
        Assert.Equal(6, raised.Driven!.MaxSessionCalls);
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
