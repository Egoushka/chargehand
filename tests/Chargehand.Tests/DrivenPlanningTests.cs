using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary>ADR 0049: the planning step. What a plan may change is settled by <see cref="PlanApplier"/> and tested without a model; the step in a batch is tested with a fake planner; the real
/// planner's prompt and parsing with a fake orchestrator.</summary>
public class DrivenPlanningTests
{
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";
    private const string BatchId = "run-batch-plan";

    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static ResolvedTask Item(string id, string? size = null, string[]? dependsOn = null, string? @ref = null, decimal? budget = null, int? turns = null) =>
        new(id, @ref, $"goal of {id}", MaxTurns: turns, Size: size, BudgetUsd: budget, DependsOn: dependsOn);

    private static ProposedTask Plan(string id, string? size = null, ProposedPart[]? split = null, ProposedNumber[]? assign = null) => new(id, size, split, assign);

    private static PlanProposal Proposal(params ProposedTask[] tasks) => new(tasks, ["listed db/migrations: latest is 0041"], null, "run-plan-1");

    private static ProposedPart Part(string goal, string? size = "M") => new(goal, size);

    private static ProposedNumber Number(string what, string value, int? part = null) => new(what, value, part);

    [Fact]
    public void Sizes_fill_in_where_the_caller_gave_none_and_never_replace_the_callers()
    {
        var (tasks, plan) = PlanApplier.Apply([Item("a"), Item("b", "S"), Item("c")], Proposal(Plan("a", "m"), Plan("b", "L"), Plan("c", "XL")), "request");

        Assert.Equal(["M", "S", null], tasks.Select(t => t.Size));
        Assert.Equal([("a", "M", "plan"), ("b", "S", "caller"), ("c", null, null)], plan.Tasks.Select(t => (t.Id, t.Size, t.SizeFrom)));
        Assert.Equal(("applied", "run-plan-1", "request"), (plan.Status, plan.RunId, plan.Source));
        Assert.Equal(["listed db/migrations: latest is 0041"], plan.Checked);
    }

    [Fact]
    public void A_large_task_becomes_an_ordered_chain_that_keeps_its_id_its_reference_and_the_tasks_waiting_on_it()
    {
        var (tasks, plan) = PlanApplier.Apply([Item("x"), Item("t", dependsOn: ["x"], @ref: "TRK-1"), Item("u", dependsOn: ["t"])],
            Proposal(Plan("t", "L", split: [Part("add the model", "S"), Part("add the endpoint"), Part("add the tests", "S")])), "profile");

        Assert.Equal(["x", "t-1", "t-2", "t", "u"], tasks.Select(t => t.Id));
        Assert.Equal([null, "x", "t-1", "t-2", "t"], tasks.Select(t => t.DependsOn is null ? null : string.Join(',', t.DependsOn)));      // the caller's dependency goes to the first link
        Assert.Equal([null, null, null, "TRK-1", null], tasks.Select(t => t.Ref));
        Assert.Equal([null, "t", "t", "t", null], tasks.Select(t => t.SplitFrom));
        Assert.Equal([null, "S", "M", "S", null], tasks.Select(t => t.Size));
        Assert.StartsWith("add the endpoint", tasks[2].Goal);
        Assert.Contains("part 2 of 3", tasks[2].Goal);
        Assert.Contains("The whole task, for context only:\n\ngoal of t", tasks[2].Goal);
        Assert.Equal(["x", "t-1", "t-2", "t", "u"], plan.Tasks.Select(t => t.Id));
        Assert.Equal("t", plan.Tasks[2].SplitFrom);
    }

    [Theory]
    [InlineData("size", "the caller set its size")]
    [InlineData("budget", "the caller set its budget or limits")]
    [InlineData("turns", "the caller set its budget or limits")]
    public void A_task_the_caller_sized_by_hand_is_never_split(string set, string why)
    {
        var task = set switch { "size" => Item("t", "L"), "budget" => Item("t", budget: 2m), _ => Item("t", turns: 50) };
        var (tasks, plan) = PlanApplier.Apply([task], Proposal(Plan("t", "L", split: [Part("one"), Part("two")])), "request");

        Assert.Equal(["t"], tasks.Select(t => t.Id));
        Assert.Equal("goal of t", tasks[0].Goal);
        Assert.Equal([$"t: not split ({why})"], plan.Dropped);
        Assert.Equal(why, plan.Tasks[0].Kept);
    }

    [Fact]
    public void A_split_the_plan_did_not_size_L_one_part_or_five_or_an_empty_goal_is_left_whole()
    {
        var (tasks, plan) = PlanApplier.Apply([Item("a"), Item("b"), Item("c"), Item("d")], Proposal(
            Plan("a", "M", split: [Part("one"), Part("two")]),
            Plan("b", "L", split: [Part("only")]),
            Plan("c", "L", split: [Part("1"), Part("2"), Part("3"), Part("4"), Part("5")]),
            Plan("d", "L", split: [Part("one"), Part(" ")])), "request");

        Assert.Equal(["a", "b", "c", "d"], tasks.Select(t => t.Id));
        Assert.Equal(["a: not split (the plan did not size it L)", "b: not split (the plan's split needs 2 to 4 parts with a goal each)", "c: not split (the plan's split needs 2 to 4 parts with a goal each)",
            "d: not split (the plan's split needs 2 to 4 parts with a goal each)"], plan.Dropped);
    }

    [Fact]
    public void A_split_that_would_pass_twenty_tasks_or_reuse_an_id_is_left_whole()
    {
        var full = Enumerable.Range(1, 19).Select(i => Item($"t{i}")).Append(Item("big")).ToList();
        var (tasks, plan) = PlanApplier.Apply(full, Proposal(Plan("big", "L", split: [Part("one"), Part("two")])), "request");
        Assert.Equal(20, tasks.Count);
        Assert.Equal(["big: not split (a batch has at most 20 tasks)"], plan.Dropped);

        (tasks, plan) = PlanApplier.Apply([Item("big"), Item("big-1")], Proposal(Plan("big", "L", split: [Part("one"), Part("two")])), "request");
        Assert.Equal(["big", "big-1"], tasks.Select(t => t.Id));
        Assert.Equal(["big: not split (the ids for its parts are not free)"], plan.Dropped);

        (tasks, _) = PlanApplier.Apply([Item("a"), Item("b")], Proposal(Plan("a", "L", split: [Part("1"), Part("2"), Part("3"), Part("4")]), Plan("b", "L", split: [Part("1"), Part("2")])), "request");
        Assert.Equal(["a-1", "a-2", "a-3", "a", "b-1", "b"], tasks.Select(t => t.Id));
    }

    [Fact]
    public void Shared_numbers_go_into_the_goal_of_the_task_they_were_given_to_and_a_value_given_twice_stays_with_the_first()
    {
        var (tasks, plan) = PlanApplier.Apply([Item("m1"), Item("m2"), Item("m3")], Proposal(
            Plan("m1", assign: [Number("migration number", "0042")]),
            Plan("m2", assign: [Number("migration number", "0042"), Number("ADR number", "0048")]),
            Plan("m3", assign: [Number("migration number", "bad\nvalue"), new ProposedNumber(null, "7", null)])), "request");

        Assert.Contains("Plan: use migration number 0042 for this task. Other tasks of this batch were given different ones", tasks[0].Goal);
        Assert.DoesNotContain("migration number", tasks[1].Goal);
        Assert.Contains("Plan: use ADR number 0048 for this task", tasks[1].Goal);
        Assert.Equal("goal of m3", tasks[2].Goal);
        Assert.Equal([new PlanAssignment("migration number", "0042")], plan.Tasks[0].Assigned);
        Assert.Equal(["m2: migration number 0042 left out; it was already given to m1", "m3: a number was left out (it must name what and a value of plain text, at most 80 characters)",
            "m3: a number was left out (it must name what and a value of plain text, at most 80 characters)"], plan.Dropped);
    }

    [Fact]
    public void A_number_goes_to_the_part_it_names_and_to_the_first_when_it_names_none_and_a_whole_task_takes_it_whatever_part_it_names()
    {
        var (tasks, _) = PlanApplier.Apply([Item("t"), Item("w")], Proposal(
            Plan("t", "L", split: [Part("one"), Part("two")], assign: [Number("migration number", "0042", part: 2), Number("ADR number", "0048")]),
            Plan("w", assign: [Number("migration number", "0043", part: 3)])), "request");

        Assert.Equal(["t-1", "t", "w"], tasks.Select(t => t.Id));
        Assert.DoesNotContain("migration number", tasks[0].Goal);
        Assert.Contains("ADR number 0048", tasks[0].Goal);
        Assert.Contains("migration number 0042", tasks[1].Goal);
        Assert.Contains("migration number 0043", tasks[2].Goal);
    }

    [Fact]
    public void A_task_the_proposal_does_not_mention_and_one_it_invents_change_nothing()
    {
        var original = Item("a", dependsOn: ["b"]);
        var (tasks, plan) = PlanApplier.Apply([original, Item("b")], Proposal(Plan("ghost", "L", split: [Part("1"), Part("2")])), "request");

        Assert.Equal(original, tasks[0]);
        Assert.Equal(["a", "b"], plan.Tasks.Select(t => t.Id));
        Assert.Equal(["b"], plan.Tasks[0].DependsOn);
        Assert.Equal(BatchPlan.NotCheckedText, plan.NotChecked);
    }

    [Fact]
    public void The_plan_is_valid_JSON_with_what_it_checked_and_what_it_did_not()
    {
        var (_, plan) = PlanApplier.Apply([Item("a")], Proposal(Plan("a", "S")), "request");
        var json = JsonSerializer.Serialize(plan, Snake);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("applied", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("listed db/migrations: latest is 0041", doc.RootElement.GetProperty("checked")[0].GetString());
        Assert.Equal("whether the tasks conflict in ways other than the numbers assigned", doc.RootElement.GetProperty("not_checked").GetString());
        Assert.StartsWith("plan: a S", plan.Describe());
    }

    [Fact]
    public void Parse_reads_the_object_a_model_fenced_or_wrapped_in_a_line()
    {
        var proposal = ModelPlanner.Parse("""
            Here is the plan:
            ```json
            {"checked": ["read db/migrations"], "tasks": [{"id": "t1", "size": "L", "split": [{"goal": "a"}, {"goal": "b", "size": "S"}], "assign": [{"what": "migration number", "value": "0042", "part": 1}]}], "note": "n",}
            ```
            """);

        Assert.Equal(["read db/migrations"], proposal.Checked);
        Assert.Equal("n", proposal.Note);
        var task = Assert.Single(proposal.Tasks);
        Assert.Equal(("t1", "L", 2), (task.Id, task.Size, task.Split!.Count));
        Assert.Equal(new ProposedNumber("migration number", "0042", 1), Assert.Single(task.Assign!));
        Assert.Throws<PlanException>(() => ModelPlanner.Parse("no object here"));
        Assert.Contains("not valid JSON", Assert.Throws<PlanException>(() => ModelPlanner.Parse("{\"tasks\": [}")).Message);
    }

    private static ResultContract Answer(ResultStatus status, string summary) =>
        new("result/v1", "run-plan-1", "answer", "trace", new PromptChain([], new AsSent("2.0.16", "build", "m", "2026-10-13")), status, summary, [], [], [], [], 0.9, new Usage(0, 0, 0, 0, 0),
            status == ResultStatus.Failed ? new ResultError(ErrorCode.CheckoutInvalid, "the checkout is invalid", false) : null);

    [Fact]
    public async Task The_planner_asks_the_orchestrator_on_the_cheap_preset_in_the_batchs_repository_as_a_child_of_the_batch()
    {
        RunRequest? sent = null;
        string? parent = null;
        var planner = new ModelPlanner((request, _, parentRunId, _) =>
        {
            (sent, parent) = (request, parentRunId);
            return Task.FromResult(Answer(ResultStatus.Completed, """{"tasks":[{"id":"a","size":"S"}],"checked":["read the tree"]}"""));
        }, "cheap");
        var batch = Request([new DrivenTask("a", Goal: "add a retry"), new DrivenTask("b", Goal: "add a backoff", Size: "L")]);
        var input = new PlanInput(batch, [Item("a") with { Goal = "add a retry" }, Item("b", "L") with { Goal = "add a backoff" }], BatchId);

        var proposal = await planner.ProposeAsync(input, default);

        Assert.Equal(("cheap", false, new RepositoryRef("/srv/repo", Commit)), (sent!.Context.Preset, sent.Context.Interactive, sent.Context.Repository));
        Assert.Null(sent.Driven);
        Assert.Equal(BatchId, parent);
        Assert.Matches("^run-", proposal.RunId);
        Assert.Contains("do not change any file", sent.Text);
        Assert.Contains("- id: a\n  goal: add a retry", sent.Text);
        Assert.Contains("- id: b (size set by the caller: L)", sent.Text);
        Assert.Contains("Name what you looked at in `checked`", sent.Text);
    }

    [Theory]
    [InlineData(ResultStatus.Failed, "the planning run ended failed: the checkout is invalid")]
    [InlineData(ResultStatus.NeedsInput, "the planning run ended needs_input")]
    public async Task A_planning_run_that_did_not_complete_is_a_plan_exception(ResultStatus status, string message)
    {
        var planner = new ModelPlanner((_, _, _, _) => Task.FromResult(Answer(status, "question")), "cheap");
        var thrown = await Assert.ThrowsAsync<PlanException>(() => planner.ProposeAsync(new PlanInput(Request([new DrivenTask("a", Goal: "g")]), [Item("a")], BatchId), default));
        Assert.StartsWith(message, thrown.Message);
    }

    private sealed class FakePlanner(PlanProposal? proposal = null, Exception? fail = null) : IBatchPlanner
    {
        public int Calls;
        public PlanInput? Last;

        public Task<PlanProposal> ProposeAsync(PlanInput input, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Last = input;
            return fail is null ? Task.FromResult(proposal!) : Task.FromException<PlanProposal>(fail);
        }
    }

    private static string Token(TaskGrant g) => $"tok-{g.RunId}";

    private static RunRequest Request(DrivenTask[] tasks, bool? plan = null, int parallel = 2) =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven(tasks, MaxParallel: parallel, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m, Plan: plan));

    private static DrivenTask Goal(string id, string? size = null, params string[] dependsOn) => new(id, Goal: $"goal of {id}", Size: size, DependsOn: dependsOn.Length == 0 ? null : dependsOn);

    private static List<JsonElement> Rows(ResultContract result) =>
        [.. JsonDocument.Parse(result.Artifacts.Single(a => a.Kind == "driven-batch").Content!).RootElement.GetProperty("tasks").EnumerateArray().Select(e => e.Clone())];

    private static JsonElement PlanArtifact(ResultContract result) => JsonDocument.Parse(result.Artifacts.Single(a => a.Kind == "plan").Content!).RootElement;

    [Fact]
    public async Task A_planned_batch_runs_sized_split_and_numbered_and_the_result_carries_the_plan()
    {
        var planner = new FakePlanner(Proposal(
            Plan("big", "L", split: [Part("first half of big", "S"), Part("second half of big", "M")]),
            Plan("m1", "S", assign: [Number("migration number", "0042")]),
            Plan("m2", "M", assign: [Number("migration number", "0043")])));
        var stacker = new DrivenDependsOnTests.FakeStacker();
        using var rig = new DrivenRunTests.Rig(planner: planner, stacker: stacker);
        List<RunStatus> events = [];
        var result = await rig.Run.RunAsync(Request([Goal("big"), Goal("m1"), Goal("m2")], plan: true), BatchId, Token, default, e => { lock (events) events.Add(e); });

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal(["big-1", "big", "m1", "m2"], Rows(result).Select(r => r.GetProperty("id").GetString()));
        Assert.All(Rows(result), r => Assert.Equal("completed", r.GetProperty("status").GetString()));

        // The chain ran in order: the second link started from the first's branch.
        var request = Assert.Single(stacker.Requests);
        Assert.Equal("big-1", Assert.Single(request.Inputs).TaskId);
        var goals = rig.Volumes.Tasks.Select(t => t.Goal).ToList();
        Assert.Contains(goals, g => g.StartsWith("first half of big", StringComparison.Ordinal));
        Assert.Contains(goals, g => g.StartsWith("second half of big", StringComparison.Ordinal) && g.Contains("is already on your branch", StringComparison.Ordinal));
        Assert.Contains(goals, g => g.Contains("use migration number 0042", StringComparison.Ordinal));
        Assert.Contains(goals, g => g.Contains("use migration number 0043", StringComparison.Ordinal));
        string SizeOf(string prefix) => rig.Volumes.Tasks.Single(t => t.Goal.StartsWith(prefix, StringComparison.Ordinal)).Governor!.SizeClass!;
        Assert.Equal(("S", "M", "S", "M"), (SizeOf("first half"), SizeOf("second half"), SizeOf("goal of m1"), SizeOf("goal of m2")));

        var plan = PlanArtifact(result);
        Assert.Equal(("applied", "request"), (plan.GetProperty("status").GetString(), plan.GetProperty("source").GetString()));
        Assert.Equal(["big-1", "big", "m1", "m2"], plan.GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("id").GetString()));
        Assert.Equal("listed db/migrations: latest is 0041", plan.GetProperty("checked")[0].GetString());
        var published = Assert.Single(events, e => e.Event == RunEventKind.Plan);
        Assert.StartsWith("plan: big-1 S (split from big); big M (split from big); m1 S [migration number 0042]; m2 M [migration number 0043]. Checked: listed db/migrations", published.Detail);
        Assert.Equal(1, planner.Calls);
        Assert.Equal(["big", "m1", "m2"], planner.Last!.Tasks.Select(t => t.Id));
    }

    [Fact]
    public async Task The_caller_overrides_the_plan_a_given_size_depends_on_and_budget_stand()
    {
        var planner = new FakePlanner(Proposal(
            Plan("held", "L", split: [Part("one"), Part("two")]),
            Plan("after", "L", split: [Part("one"), Part("two")]),
            Plan("sized", "L")));
        using var rig = new DrivenRunTests.Rig(planner: planner, stacker: new DrivenDependsOnTests.FakeStacker());
        DrivenTask[] tasks = [Goal("held", "S"), Goal("after", null, "held"), Goal("sized", "S")];
        var result = await rig.Run.RunAsync(Request(tasks, plan: true), BatchId, Token, default);

        var rows = Rows(result);
        // "held" and "sized" keep their size S and stay whole; "after" was unsized, so the plan's chain applies, and its first link still waits for "held".
        Assert.Equal(["held", "after-1", "after", "sized"], rows.Select(r => r.GetProperty("id").GetString()));
        Assert.Equal("completed", rows[0].GetProperty("status").GetString());
        var plan = PlanArtifact(result);
        var entries = plan.GetProperty("tasks").EnumerateArray().ToList();
        Assert.Equal(("S", "caller"), (entries[0].GetProperty("size").GetString(), entries[0].GetProperty("size_from").GetString()));
        Assert.Equal(["held"], entries[1].GetProperty("depends_on").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(["after-1"], entries[2].GetProperty("depends_on").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(("S", "caller"), (entries[3].GetProperty("size").GetString(), entries[3].GetProperty("size_from").GetString()));
        Assert.Equal(["S", "S"], rig.Volumes.Tasks.Where(t => t.Goal.StartsWith("goal of held", StringComparison.Ordinal) || t.Goal.StartsWith("goal of sized", StringComparison.Ordinal)).Select(t => t.Governor!.SizeClass));
    }

    [Fact]
    public async Task The_profile_turns_planning_on_and_the_request_turns_it_off()
    {
        var planner = new FakePlanner(Proposal(Plan("a", "S")));
        using var rig = new DrivenRunTests.Rig(planner: planner, planning: new DrivenPlanning(Enabled: true));
        var onByProfile = await rig.Run.RunAsync(Request([Goal("a")]), "run-batch-p1", Token, default);
        Assert.Equal("profile", PlanArtifact(onByProfile).GetProperty("source").GetString());
        Assert.Equal(1, planner.Calls);

        var offByRequest = await rig.Run.RunAsync(Request([Goal("a")], plan: false), "run-batch-p2", Token, default);
        Assert.DoesNotContain(offByRequest.Artifacts, a => a.Kind == "plan");
        Assert.Equal(1, planner.Calls);

        using var off = new DrivenRunTests.Rig(planner: planner);
        var untouched = await off.Run.RunAsync(Request([Goal("a")]), "run-batch-p3", Token, default);
        Assert.DoesNotContain(untouched.Artifacts, a => a.Kind == "plan");
        Assert.Equal(1, planner.Calls);
    }

    [Theory]
    [InlineData("fails")]
    [InlineData("missing")]
    public async Task A_planner_that_fails_or_is_missing_leaves_the_batch_as_sent_and_the_plan_says_why(string how)
    {
        var planner = how == "fails" ? new FakePlanner(fail: new PlanException("the planning run printed no JSON plan")) : null;
        using var rig = new DrivenRunTests.Rig(planner: planner);
        List<RunStatus> events = [];
        var result = await rig.Run.RunAsync(Request([Goal("a"), Goal("b", "S")], plan: true), BatchId, Token, default, e => { lock (events) events.Add(e); });

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal(["a", "b"], Rows(result).Select(r => r.GetProperty("id").GetString()));
        var plan = PlanArtifact(result);
        Assert.Equal("skipped", plan.GetProperty("status").GetString());
        Assert.Equal(how == "fails" ? "the planning run printed no JSON plan" : "this server has no planner", plan.GetProperty("reason").GetString());
        Assert.StartsWith("plan skipped: ", Assert.Single(events, e => e.Event == RunEventKind.Plan).Detail);
        Assert.Equal(["goal of a", "goal of b"], rig.Volumes.Tasks.Select(t => t.Goal).Order());
    }

    [Fact]
    public async Task A_restart_carries_the_planned_batch_and_does_not_plan_again()
    {
        var planner = new FakePlanner(Proposal(Plan("big", "L", split: [Part("first half"), Part("second half")])));
        using var rig = new DrivenRunTests.Rig(durable: true, planner: planner, stacker: new DrivenDependsOnTests.FakeStacker());
        var request = Request([Goal("big"), Goal("other")], plan: true, parallel: 1);
        rig.Engine.HoldNth = 1;
        using var stop = new CancellationTokenSource();
        var first = rig.Run.RunAsync(request, BatchId, Token, stop.Token, null, () => false);
        for (var i = 0; i < 400 && rig.Engine.Started.Count < 1; i++)
            await Task.Delay(25);
        await stop.CancelAsync();
        await first;
        var state = Assert.Single(await rig.Restart().UnfinishedAsync(default));

        Assert.Equal(["big-1", "big", "other"], state.Tasks.Select(t => t.Id));
        Assert.Equal([null, "big-1", null], state.Tasks.Select(t => t.DependsOn is null ? null : string.Join(',', t.DependsOn)));
        Assert.Equal(["big", "big", null], state.Tasks.Select(t => t.SplitFrom));
        Assert.True(state.Plan!.Applied);

        rig.Engine.Release();
        var result = await rig.Run.RunAsync(state.Request, state.RunId, Token, default, null, () => false, state);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal(["big-1", "big", "other"], Rows(result).Select(r => r.GetProperty("id").GetString()));
        Assert.Equal(1, planner.Calls);
        Assert.Equal("applied", PlanArtifact(result).GetProperty("status").GetString());
    }
}
