using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary>ADR 0044: a session stopped by the turn limit or the wall clock with work on its branch gets a continuation on the same branch; work that no continuation can finish is handed over
/// as a partial draft pull request. The sessions are scripted by the fake output volume; nothing starts a container.</summary>
public class DrivenContinuationTests
{
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";
    private const string BatchId = "run-batch-c";

    private static string Token(TaskGrant g) => $"tok-{g.RunId}";

    private static RunRequest Request(params DrivenTask[] tasks) =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([.. tasks], MaxParallel: 1, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

    private static RunRequest One(string goal = "one") => Request(new DrivenTask("t1", Goal: goal));

    private static SessionOutcome Stopped(SessionStatus status, string reason, int commits = 3, int turns = 80) =>
        new(status, reason, turns, 1000, 200, 4000, null, 0, [], commits > 0, Commits: commits);

    private static SessionOutcome Done(int commits = 4) =>
        new(SessionStatus.Completed, "", 30, 1000, 200, 4000, null, 0, [], true, Commits: commits);

    private static bool IsContinuation(string goal) => goal.Contains("Continuation ", StringComparison.Ordinal);

    /// <summary>A session that is stopped but has added a commit to what it started from: the number of the continuation in its prompt is how many sessions came before.</summary>
    private static SessionOutcome Progressing(string goal, string reason = "turn_limit") =>
        Stopped(SessionStatus.Stalled, reason, commits: 3 + (goal.Contains("Continuation 2 of", StringComparison.Ordinal) ? 2 : IsContinuation(goal) ? 1 : 0));

    private static SessionOutcome OutOfMemory() => new(SessionStatus.Failed, "out_of_memory", 0, 0, 0, 0, null, null, [], false);

    private static JsonElement Row(ResultContract result) =>
        JsonDocument.Parse(result.Artifacts.Single(a => a.Kind == "driven-batch").Content!).RootElement.GetProperty("tasks").EnumerateArray().Single();

    [Fact]
    public async Task A_session_stopped_by_the_turn_limit_continues_on_the_same_branch_and_ends_in_one_pull_request()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: (_, goal) => IsContinuation(goal) ? Done() : Stopped(SessionStatus.Stalled, "turn_limit"));
        List<RunStatus> events = [];
        var result = await rig.Run.RunAsync(One(), BatchId, Token, default, events.Add);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal(2, rig.Engine.Started.Count);
        var (first, second) = (rig.Engine.Started[0].RunId, rig.Engine.Started[1].RunId);
        Assert.NotEqual(first, second);
        // The continuation starts from the first session's bundle, on the first session's branch name.
        Assert.Equal(second, Assert.Single(rig.Volumes.Seeds).RunId);
        Assert.EndsWith(Path.Combine("out", first, "chargehand.bundle"), rig.Volumes.Seeds[0].Path);
        Assert.Equal($"chargehand/{first}", rig.Volumes.Tasks[1].Branch);
        Assert.StartsWith("one\n\nContinuation 1 of 2. A previous session was stopped by turn limit after 80 turns", rig.Volumes.Tasks[1].Goal);
        Assert.Contains($"git diff {Commit}..HEAD", rig.Volumes.Tasks[1].Goal);
        // One pull request, from the last session, with the note; the usage is both sessions'.
        var handover = Assert.Single(rig.Handover.Inputs);
        Assert.Equal($"chargehand/{first}", handover.Branch);
        Assert.Equal(second, handover.RunId);
        Assert.Contains("1 continuation session(s)", handover.Body);
        Assert.False(handover.Partial);
        Assert.Equal(2400, Row(result).GetProperty("tokens").GetInt64());
        Assert.Contains(events, e => e.Event == RunEventKind.ContainerStarted && e.Detail == "task t1: session container started (continuation 1 of 2, previous stopped by turn_limit)");
        // Earlier sessions are cleaned up; the token cap handed to the next one is what is left.
        Assert.Contains($"rm cid-{first}", rig.Engine.Calls);
        Assert.Contains($"volume-rm chargehand-work-{first}", rig.Engine.Calls);
        Assert.Equal(2_000_000 - 1200, rig.Volumes.Tasks[1].MaxTokens);
    }

    [Fact]
    public async Task A_task_stopped_on_every_session_is_handed_over_as_a_partial_draft_pull_request_and_stays_failed()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: (_, goal) => Progressing(goal));
        var result = await rig.Run.RunAsync(One("Add a retry"), BatchId, Token, default);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Equal(3, rig.Engine.Started.Count);
        var handover = Assert.Single(rig.Handover.Inputs);
        Assert.True(handover.Partial);
        Assert.Equal("[partial] Add a retry", handover.Title);
        Assert.Contains("turn limit after 240 turns", handover.Body);
        Assert.Equal(rig.Engine.Started[2].RunId, handover.RunId);
        Assert.Equal($"chargehand/{rig.Engine.Started[0].RunId}", handover.Branch);
        var row = Row(result);
        Assert.Equal("failed", row.GetProperty("status").GetString());
        Assert.Equal("https://example.test/o/r/pull/1", row.GetProperty("pr_url").GetString());
        Assert.Equal($"chargehand/{rig.Engine.Started[0].RunId}", row.GetProperty("branch").GetString());
        Assert.Equal("partial: stopped by turn_limit; draft PR opened", row.GetProperty("detail").GetString());
        Assert.Equal(3600, row.GetProperty("tokens").GetInt64());
    }

    [Theory]
    [InlineData(SessionStatus.Stalled, "turn_limit", 0)]
    [InlineData(SessionStatus.Stalled, "wall_clock", 0)]
    public async Task A_stop_with_no_commits_is_failed_with_no_pull_request_and_no_continuation(SessionStatus status, string reason, int commits)
    {
        using var rig = new DrivenRunTests.Rig(outcomes: (_, _) => Stopped(status, reason, commits));
        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Single(rig.Engine.Started);
        Assert.Empty(rig.Handover.Inputs);
        Assert.Empty(rig.Volumes.Seeds);
        var row = Row(result);
        Assert.Equal("failed", row.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("pr_url").ValueKind);
    }

    [Theory]
    [InlineData(SessionStatus.Stalled, "no_progress")]
    [InlineData(SessionStatus.Stalled, "repeating")]
    [InlineData(SessionStatus.Stalled, "budget_spent")]
    [InlineData(SessionStatus.TokenCap, "token_cap")]
    [InlineData(SessionStatus.Failed, "session_error")]
    [InlineData(SessionStatus.RateLimited, "rate_limited")]
    public async Task Other_stops_do_not_continue_but_their_commits_are_handed_over_as_partial(SessionStatus status, string reason)
    {
        using var rig = new DrivenRunTests.Rig(outcomes: (_, _) => Stopped(status, reason));
        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Single(rig.Engine.Started);
        Assert.Empty(rig.Volumes.Seeds);
        Assert.True(Assert.Single(rig.Handover.Inputs).Partial);
        Assert.Equal($"partial: stopped by {reason}; draft PR opened", Row(result).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Each_session_gets_the_governor_with_the_tasks_budget_and_the_earlier_sessions_spend_and_its_decisions_become_events()
    {
        var warn = new GovernorDecision(DateTimeOffset.UtcNow, 7, GovernorMode.Observe, GovernorAction.Warn, "repeated_call", "first_hit", new Dictionary<string, string>(), 1.2m, 10m, "L");
        using var rig = new DrivenRunTests.Rig(outcomes: (_, goal) => IsContinuation(goal)
            ? Done() with { UsdEstimate = 0.5m }
            : Stopped(SessionStatus.Stalled, "turn_limit") with { UsdEstimate = 1.25m, Governor = [warn] });
        List<RunStatus> events = [];
        await rig.Run.RunAsync(Request(new DrivenTask("t1", Goal: "one", Size: "L")), BatchId, Token, default, events.Add);

        Assert.Equal(new SessionGovernor(BudgetUsd: 10m, PriorUsd: 0m, SizeClass: "L"), rig.Volumes.Tasks[0].Governor);
        Assert.Equal(new SessionGovernor(BudgetUsd: 10m, PriorUsd: 1.25m, SizeClass: "L"), rig.Volumes.Tasks[1].Governor);
        Assert.Contains(events, e => e.Event == RunEventKind.Governor && e.Detail == "task t1: governor observe: warn on repeated_call (first hit) at turn 7, spend 1.20 of 10.00");
    }

    [Fact]
    public async Task A_tasks_own_budget_replaces_its_size_class_budget()
    {
        using var rig = new DrivenRunTests.Rig();
        await rig.Run.RunAsync(Request(new DrivenTask("t1", Goal: "one", BudgetUsd: 2.5m)), BatchId, Token, default);
        Assert.Equal((2.5m, "M"), (rig.Volumes.Tasks[0].Governor!.BudgetUsd, rig.Volumes.Tasks[0].Governor!.SizeClass));
    }

    [Fact]
    public async Task A_question_after_commits_is_handed_over_as_a_partial_draft_with_the_questions_and_stays_a_question()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: (_, _) => new SessionOutcome(SessionStatus.NeedsInput, "", 5, 1000, 200, 0, null, 0, ["Which database?", "Keep the old API?"], true, Commits: 2));
        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Single(rig.Engine.Started);
        var handover = Assert.Single(rig.Handover.Inputs);
        Assert.True(handover.Partial);
        Assert.StartsWith("[partial] ", handover.Title);
        Assert.Contains("Which database?", handover.Body);
        Assert.Contains("Keep the old API?", handover.Body);
        var row = Row(result);
        Assert.Equal("needs_input", row.GetProperty("status").GetString());
        Assert.Equal("https://example.test/o/r/pull/1", row.GetProperty("pr_url").GetString());
        Assert.Equal("partial: needs input (Which database?; Keep the old API?); draft PR opened", row.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_continuation_that_asks_a_question_after_commits_of_its_own_hands_over_its_branch()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: (_, goal) => IsContinuation(goal)
            ? new SessionOutcome(SessionStatus.NeedsInput, "", 5, 1000, 200, 0, null, 0, ["Which database?"], true, Commits: 4)
            : Stopped(SessionStatus.Stalled, "turn_limit"));
        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        var handover = Assert.Single(rig.Handover.Inputs);
        Assert.Equal(rig.Engine.Started[1].RunId, handover.RunId);
        Assert.Contains("Which database?", handover.Body);
        Assert.Equal("needs_input", Row(result).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_question_with_no_commits_is_neither_continued_nor_handed_over()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: (_, _) => new SessionOutcome(SessionStatus.NeedsInput, "", 5, 1000, 200, 0, null, 0, ["Which database?"], false));
        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Single(rig.Engine.Started);
        Assert.Empty(rig.Handover.Inputs);
        Assert.Equal("needs_input", Row(result).GetProperty("status").GetString());
    }

    [Fact]
    public async Task No_continuation_starts_once_the_tasks_token_cap_is_spent()
    {
        // The preset caps a task at 2,000,000 tokens; the first session used all of it.
        using var rig = new DrivenRunTests.Rig(outcomes: (_, _) => Stopped(SessionStatus.Stalled, "turn_limit") with { InputTokens = 1_999_900, OutputTokens = 100 });
        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Single(rig.Engine.Started);
        Assert.True(Assert.Single(rig.Handover.Inputs).Partial);
        Assert.Equal("failed", Row(result).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_task_can_set_its_own_turn_and_time_limits()
    {
        using var rig = new DrivenRunTests.Rig();
        await rig.Run.RunAsync(Request(new DrivenTask("t1", Goal: "one", MaxTurns: 5, MaxMinutes: 7), new DrivenTask("t2", Goal: "two")), BatchId, Token, default);

        var byGoal = rig.Volumes.Tasks.ToDictionary(t => t.Goal);
        Assert.Equal((5, 7), (byGoal["one"].MaxTurns, byGoal["one"].MaxMinutes));
        Assert.Equal((80, 45), (byGoal["two"].MaxTurns, byGoal["two"].MaxMinutes));
    }

    [Fact]
    public void A_size_other_than_s_m_or_l_or_a_budget_not_above_zero_is_a_problem_and_the_schema_says_so()
    {
        var request = Request(new DrivenTask("t1", Goal: "one", Size: "XL"), new DrivenTask("t2", Goal: "two", BudgetUsd: 0m), new DrivenTask("t3", Goal: "three", Size: "S", BudgetUsd: 1m));
        Assert.Equal(["task 't1': size must be S, M or L", "task 't2': budget_usd must be above 0"], DrivenRules.Problems(request.Driven!));
        Assert.NotEmpty(ContractSchemas.Validate(ContractSchemas.Request, JsonSerializer.SerializeToElement(Request(new DrivenTask("t1", Goal: "one", Size: "XL")), ContractJson.Options)));
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.Request, JsonSerializer.SerializeToElement(Request(new DrivenTask("t3", Goal: "three", Size: "S", BudgetUsd: 1m)), ContractJson.Options)));
    }

    [Fact]
    public void A_per_task_limit_outside_its_range_is_a_problem_and_the_schema_says_so()
    {
        var request = Request(new DrivenTask("t1", Goal: "one", MaxTurns: 0), new DrivenTask("t2", Goal: "two", MaxMinutes: 481), new DrivenTask("t3", Goal: "three", MaxTurns: 1000, MaxMinutes: 480));
        Assert.Equal(["task 't1': max_turns must be 1 to 1000", "task 't2': max_minutes must be 1 to 480"], DrivenRules.Problems(request.Driven!));

        var json = JsonSerializer.SerializeToElement(Request(new DrivenTask("t1", Goal: "one", MaxTurns: 0)), ContractJson.Options);
        Assert.NotEmpty(ContractSchemas.Validate(ContractSchemas.Request, json));
        json = JsonSerializer.SerializeToElement(Request(new DrivenTask("t1", Goal: "one", MaxTurns: 10, MaxMinutes: 20)), ContractJson.Options);
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.Request, json));
    }

    [Fact]
    public async Task A_restart_during_a_continuation_resumes_that_continuation()
    {
        using var rig = new DrivenRunTests.Rig(durable: true, outcomes: (_, goal) => IsContinuation(goal) ? Done() : Stopped(SessionStatus.Stalled, "turn_limit"));
        rig.Engine.HoldNth = 2;
        using var stop = new CancellationTokenSource();
        var first = rig.Run.RunAsync(One(), BatchId, Token, stop.Token, null, () => false);
        await Until(() => rig.Engine.Started.Count == 2);
        await stop.CancelAsync();
        await first;

        var state = Assert.Single(await rig.Restart().UnfinishedAsync(default));
        var entry = state.Tasks.Single();
        Assert.Equal((TaskPhase.Started, 1), (entry.Phase, entry.Continuation));
        Assert.Equal(rig.Engine.Started[1].RunId, entry.RunId);
        Assert.Equal($"chargehand/{rig.Engine.Started[0].RunId}", entry.Branch);
        Assert.Equal(1200, entry.Prior!.InputTokens + entry.Prior.OutputTokens);

        rig.Engine.Release();
        var result = await rig.Run.RunAsync(state.Request, state.RunId, Token, default, null, () => false, state);

        Assert.Equal(ResultStatus.Completed, result.Status);
        // The continuation's container was taken up, not started again; the earlier session's usage is still counted.
        Assert.Equal(2, rig.Engine.Started.Count);
        var handover = Assert.Single(rig.Handover.Inputs);
        Assert.Equal(entry.Branch, handover.Branch);
        Assert.Contains("1 continuation session(s)", handover.Body);
        Assert.Equal(2400, Row(result).GetProperty("tokens").GetInt64());
    }

    public static TheoryData<Exception> StartFailures => new()
    {
        new ChargehandException(ErrorCode.ContainerUnavailable, "the runner holds 20 session containers already (HTTP 429)"),
        new HttpRequestException("the seed bundle is over the runner's limit (HTTP 400)"),
        new TaskCanceledException("the request timed out"),
    };

    [Theory]
    [MemberData(nameof(StartFailures))]
    public async Task A_continuation_that_cannot_be_started_hands_over_the_work_the_first_session_left(Exception failure)
    {
        using var rig = new DrivenRunTests.Rig(outcomes: (_, goal) => Progressing(goal));
        rig.Engine.OnStart = n => n == 2 ? failure : null;
        var result = await rig.Run.RunAsync(One("Add a retry"), BatchId, Token, default);

        var first = rig.Engine.Started[0].RunId;
        var handover = Assert.Single(rig.Handover.Inputs);
        Assert.True(handover.Partial);
        Assert.Equal(first, handover.RunId);
        Assert.Equal($"chargehand/{first}", handover.Branch);
        Assert.True(Assert.Single(rig.Handover.BundleSeen), "the earlier session's bundle was still on disk for the handover");
        Assert.Contains("could not be started", handover.Body);
        Assert.DoesNotContain("continuation session(s)", handover.Body);
        var row = Row(result);
        Assert.Equal("failed", row.GetProperty("status").GetString());
        Assert.Equal("https://example.test/o/r/pull/1", row.GetProperty("pr_url").GetString());
        Assert.Equal("partial: stopped by turn_limit; draft PR opened; continuation 1 did not start", row.GetProperty("detail").GetString());
        // The run that never started leaves nothing behind.
        var failed = rig.Engine.Started[1].RunId;
        Assert.Contains($"volume-rm chargehand-out-{failed}", rig.Engine.Calls);
        Assert.Contains($"volume-rm chargehand-work-{failed}", rig.Engine.Calls);
    }

    [Fact]
    public async Task A_first_session_that_cannot_be_started_is_still_a_failed_task_with_its_error()
    {
        using var rig = new DrivenRunTests.Rig();
        rig.Engine.OnStart = _ => new ChargehandException(ErrorCode.ContainerUnavailable, "the runner is full");
        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Empty(rig.Handover.Inputs);
        Assert.Equal("container_unavailable", Row(result).GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task A_token_cap_stop_that_overshoots_the_cap_is_still_handed_over_as_partial()
    {
        // The cap is 2,000,000 tokens for a task; the session's last message took it past.
        using var rig = new DrivenRunTests.Rig(outcomes: (_, _) => Stopped(SessionStatus.TokenCap, "token_cap") with { InputTokens = 2_300_000, OutputTokens = 4_000 });
        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.True(Assert.Single(rig.Handover.Inputs).Partial);
        var row = Row(result);
        Assert.Equal("partial: stopped by token_cap; draft PR opened", row.GetProperty("detail").GetString());
        Assert.Equal("https://example.test/o/r/pull/1", row.GetProperty("pr_url").GetString());
        Assert.Equal(2_304_000, row.GetProperty("tokens").GetInt64());
    }

    [Fact]
    public async Task A_live_tally_a_little_over_the_cap_while_the_session_winds_down_does_not_cancel_the_task()
    {
        // The driver stopped the session at the cap; the tally the host read in its last seconds is 5% over, and 5 inspections go by before the container is seen to exit.
        var volumes = new DrivenRunTests.FakeVolumes(_ => SessionStatus.Completed, (_, _) => Stopped(SessionStatus.TokenCap, "token_cap")) { Usage = new TaskUsage(2_100_000, 0) };
        using var rig = new DrivenRunTests.Rig(volumes: volumes);
        rig.Engine.RunFor = 5;
        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.True(volumes.UsageReads >= 1);
        Assert.DoesNotContain(rig.Engine.Calls, c => c.StartsWith("signal ", StringComparison.Ordinal));
        Assert.True(Assert.Single(rig.Handover.Inputs).Partial);
        Assert.Equal("partial: stopped by token_cap; draft PR opened", Row(result).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_dollar_budget_stop_that_overshoots_the_cap_is_still_handed_over_as_partial()
    {
        // With an API key the task's cap is 3 dollars; the session ended on 3.50.
        using var rig = new DrivenRunTests.Rig(apiKey: true, outcomes: (_, _) => Stopped(SessionStatus.Failed, "budget_exceeded") with { CostUsd = 3.5m });
        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.True(Assert.Single(rig.Handover.Inputs).Partial);
        Assert.Equal("partial: stopped by budget_exceeded; draft PR opened", Row(result).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task The_sweep_keeps_the_output_volumes_of_a_continuing_tasks_earlier_sessions()
    {
        using var rig = new DrivenRunTests.Rig(durable: true);
        rig.Engine.Resources = new([], [], ["chargehand-out-run-a", "chargehand-out-run-b", "chargehand-work-run-b", "chargehand-out-run-x"]);
        var state = new BatchState("run-b1", DateTimeOffset.UtcNow, "t", One(), null,
            [new TaskEntry("t1", null, "one", TaskPhase.Started, "run-b", "chargehand/run-a", Continuation: 1, Runs: ["run-a", "run-b"])], []);

        var removed = await rig.Run.SweepOrphansAsync([state], default);

        Assert.Equal(["chargehand-out-run-x"], removed);
    }

    [Fact]
    public async Task A_restart_before_a_continuations_container_started_redoes_it_from_the_previous_bundle_on_the_same_branch()
    {
        using var rig = new DrivenRunTests.Rig(durable: true, outcomes: (_, goal) => IsContinuation(goal) ? Done() : Stopped(SessionStatus.Stalled, "turn_limit"));
        using var stop = new CancellationTokenSource();
        // The server stops as the continuation's container is being started: its journal entry names the run, but no container.
        rig.Engine.OnStart = n => n == 2 ? Stopping(stop) : null;
        var first = rig.Run.RunAsync(One(), BatchId, Token, stop.Token, null, () => false);
        await first;
        var state = Assert.Single(await rig.Restart().UnfinishedAsync(default));
        var entry = state.Tasks.Single();
        var (firstRun, secondRun) = (rig.Engine.Started[0].RunId, rig.Engine.Started[1].RunId);
        Assert.Equal((secondRun, 1), (entry.RunId, entry.Continuation));
        Assert.Null(entry.Container);
        // The container is gone and the host's scratch directory with the first bundle was cleared by the restart.
        rig.Engine.Gone.Add($"chargehand-{secondRun}");
        rig.Volumes.NoOutput.Add(secondRun);
        Directory.Delete(Path.Combine(rig.Profile.WorkerRoot, ".driven", "out", firstRun), recursive: true);
        rig.Engine.OnStart = null;

        var result = await rig.Run.RunAsync(state.Request, state.RunId, Token, default, null, () => false, state);

        Assert.Equal(ResultStatus.Completed, result.Status);
        var handover = Assert.Single(rig.Handover.Inputs);
        Assert.Equal($"chargehand/{firstRun}", handover.Branch);
        Assert.False(handover.Partial);
        Assert.Contains("1 continuation session(s)", handover.Body);
        // Seeded from the first session's bundle, read again from its output volume.
        Assert.All(rig.Volumes.Seeds, s => Assert.EndsWith(Path.Combine("out", firstRun, "chargehand.bundle"), s.Path));
        Assert.Equal(2, rig.Volumes.Seeds.Count);
        Assert.NotEqual(secondRun, rig.Engine.Started[2].RunId);
        Assert.Equal(rig.Engine.Started[2].RunId, rig.Volumes.Seeds[1].RunId);
        Assert.Contains("Continuation 1 of 2. A previous session was stopped by turn limit", rig.Volumes.Tasks[^1].Goal);
    }

    private static OperationCanceledException Stopping(CancellationTokenSource stop)
    {
        stop.Cancel();
        return new OperationCanceledException(stop.Token);
    }

    [Fact]
    public async Task A_continuation_that_ends_without_work_leaves_the_earlier_sessions_work_to_be_handed_over()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: (_, goal) => IsContinuation(goal) ? OutOfMemory() : Stopped(SessionStatus.Stalled, "turn_limit"));
        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        var handover = Assert.Single(rig.Handover.Inputs);
        Assert.True(handover.Partial);
        Assert.Equal(rig.Engine.Started[0].RunId, handover.RunId);
        Assert.True(Assert.Single(rig.Handover.BundleSeen));
        var row = Row(result);
        Assert.Equal("failed", row.GetProperty("status").GetString());
        Assert.Equal("partial: stopped by out_of_memory; draft PR opened", row.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_continuation_that_asks_a_question_and_left_no_work_of_its_own_keeps_the_earlier_work_as_a_partial_draft_and_stays_a_question()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: (_, goal) => IsContinuation(goal)
            ? new SessionOutcome(SessionStatus.NeedsInput, "", 5, 1000, 200, 0, null, 0, ["Which database?"], false)
            : Stopped(SessionStatus.Stalled, "turn_limit"));
        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Equal(rig.Engine.Started[0].RunId, Assert.Single(rig.Handover.Inputs).RunId);
        var row = Row(result);
        Assert.Equal("needs_input", row.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("pr_url").ValueKind);
    }

    [Fact]
    public async Task A_restart_during_a_continuation_keeps_the_earlier_work_for_a_partial_handover()
    {
        using var rig = new DrivenRunTests.Rig(durable: true, outcomes: (_, goal) => IsContinuation(goal) ? OutOfMemory() : Stopped(SessionStatus.Stalled, "turn_limit"));
        rig.Engine.HoldNth = 2;
        using var stop = new CancellationTokenSource();
        var running = rig.Run.RunAsync(One(), BatchId, Token, stop.Token, null, () => false);
        await Until(() => rig.Engine.Started.Count == 2);
        await stop.CancelAsync();
        await running;
        var state = Assert.Single(await rig.Restart().UnfinishedAsync(default));
        Assert.Equal(rig.Engine.Started[0].RunId, state.Tasks.Single().Best!.RunId);

        rig.Engine.Release();
        await rig.Run.RunAsync(state.Request, state.RunId, Token, default, null, () => false, state);

        var handover = Assert.Single(rig.Handover.Inputs);
        Assert.True(handover.Partial);
        Assert.Equal(rig.Engine.Started[0].RunId, handover.RunId);
    }

    [Fact]
    public async Task A_continuation_that_adds_no_commit_past_its_seed_does_not_continue_again()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: (_, _) => Stopped(SessionStatus.Stalled, "turn_limit"));
        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Equal(2, rig.Engine.Started.Count);
        Assert.True(Assert.Single(rig.Handover.Inputs).Partial);
        Assert.Equal("failed", Row(result).GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public async Task The_profiles_limit_sets_how_many_continuations_may_follow(int allowed, int sessions)
    {
        using var rig = new DrivenRunTests.Rig(limits: new DrivenLimits(MaxContinuations: allowed), outcomes: (_, goal) => Progressing(goal));
        await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Equal(sessions, rig.Engine.Started.Count);
        Assert.True(Assert.Single(rig.Handover.Inputs).Partial);
    }

    [Fact]
    public void A_profile_limit_of_continuations_outside_0_to_5_is_refused_and_the_schema_says_so()
    {
        Assert.Throws<ChargehandException>(() => new DrivenLimits(MaxContinuations: 6).Validate());
        Assert.Throws<ChargehandException>(() => new DrivenLimits(MaxContinuations: -1).Validate());
        new DrivenLimits(MaxContinuations: 0).Validate();
        Assert.Equal(2, new DrivenLimits().Apply(new DrivenPreset()).MaxContinuations);
    }

    private const string Progress = "# Handoff from the previous session\n\nThe session was stopped by checkpoint stuck after 37 turns.\n\n- [x] research\n- [ ] test\n";

    [Theory]
    [InlineData("checkpoint_stuck")]
    [InlineData("checkpoint_context")]
    [InlineData("checkpoint_spend")]
    public async Task A_governor_checkpoint_continues_in_a_fresh_session_whose_prompt_carries_the_handoff_and_offers_the_earlier_commits(string reason)
    {
        using var rig = new DrivenRunTests.Rig(outcomes: (_, goal) => IsContinuation(goal) ? Done() : Stopped(SessionStatus.Stalled, reason));
        rig.Volumes.Progress = (_, goal) => IsContinuation(goal) ? null : Progress;
        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal(2, rig.Engine.Started.Count);
        var first = rig.Volumes.Tasks[0].Goal;
        var goal = rig.Volumes.Tasks[1].Goal;
        Assert.DoesNotContain("handoff", first, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"stopped by {reason.Replace('_', ' ')} after 80 turns", goal);
        Assert.Contains("offered, not required", goal);
        Assert.Contains($"git reset --hard {Commit}", goal);
        Assert.EndsWith(Progress.TrimEnd(), goal);
        Assert.False(Assert.Single(rig.Handover.Inputs).Partial);
    }

    [Fact]
    public async Task A_continuation_without_a_handoff_keeps_the_plain_prompt()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: (_, goal) => IsContinuation(goal) ? Done() : Stopped(SessionStatus.Stalled, "checkpoint_stuck"));
        await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.EndsWith("do not redo finished work, finish the task, run the tests, and end with the same report block.", rig.Volumes.Tasks[1].Goal);
        Assert.DoesNotContain("offered, not required", rig.Volumes.Tasks[1].Goal);
    }

    [Fact]
    public async Task A_long_handoff_is_cut_in_the_prompt_and_in_a_partial_pull_requests_body()
    {
        var long_ = Progress + new string('z', 20_000);
        using var rig = new DrivenRunTests.Rig(outcomes: (_, goal) => Progressing(goal));
        rig.Volumes.Progress = (run, _) => long_;
        await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.All(rig.Volumes.Tasks.Skip(1), t => Assert.Contains("[handoff truncated]", t.Goal));
        Assert.All(rig.Volumes.Tasks.Skip(1), t => Assert.True(t.Goal.Length < 9500, $"{t.Goal.Length}"));
        var handover = Assert.Single(rig.Handover.Inputs);
        Assert.True(handover.Partial);
        Assert.Contains("[handoff truncated]", handover.Body);
        Assert.True(handover.Body.Length < 7500, $"{handover.Body.Length}");
    }

    [Fact]
    public async Task A_partial_pull_requests_body_carries_the_last_sessions_progress()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: (_, _) => Stopped(SessionStatus.Stalled, "budget_spent"));
        rig.Volumes.Progress = (run, _) => $"# Handoff from the previous session\n\nprogress of {run}\n";
        await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Single(rig.Engine.Started);       // a spent budget never continues
        var handover = Assert.Single(rig.Handover.Inputs);
        Assert.True(handover.Partial);
        Assert.Contains("was stopped by budget spent after 80 turns", handover.Body);
        Assert.Contains($"progress of {rig.Engine.Started[0].RunId}", handover.Body);
    }

    [Fact]
    public async Task A_partial_pull_request_from_a_session_that_left_no_handoff_has_no_handoff_section()
    {
        using var rig = new DrivenRunTests.Rig(outcomes: (_, _) => Stopped(SessionStatus.Stalled, "no_progress"));
        await rig.Run.RunAsync(One(), BatchId, Token, default);
        Assert.DoesNotContain("---", Assert.Single(rig.Handover.Inputs).Body);
    }

    [Fact]
    public void The_stop_reasons_that_continue_are_listed_in_one_place()
    {
        Assert.Equal(["turn_limit", "wall_clock", "checkpoint_stuck", "checkpoint_context", "checkpoint_spend"], StallDetector.ContinuableReasons);
        foreach (var reason in (string[])["no_progress", "repeating", "token_cap", "out_of_memory", "budget_spent"])
            Assert.False(StallDetector.Continuable(reason));
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 400; i++)
        {
            if (condition())
                return;
            await Task.Delay(25);
        }
        Assert.Fail("the condition did not hold within 10 seconds");
    }
}
