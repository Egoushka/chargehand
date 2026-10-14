using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0045's Budget decision: a task's budget is the percentile of what finished tasks of its size class cost once there are enough of them, else the flat budget, and a task's own
/// <c>budget_usd</c> always wins. The records are synthetic; nothing starts a container.</summary>
public class BudgetHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private const string Repo = "o/r";
    private const string BatchId = "run-batch-b";
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";

    private static readonly DrivenGovernor Five = new(History: new DrivenGovernorHistory(MinTasks: 5, WindowDays: 30, Percentile: 80));

    private static LearningRecord Past(decimal? usd, string repository = Repo, string size = "M", int daysAgo = 1, string outcome = LearningOutcome.Completed, string? stop = null, bool partialEstimate = false)
    {
        var record = LearningTests.Record($"run-{Guid.NewGuid():N}", repository, size, usd, outcome, at: Now.AddDays(-daysAgo));
        return record with { StopReason = stop ?? (outcome == LearningOutcome.Completed ? "completed" : "turn_limit"), Spend = record.Spend with { UsdEstimatePartial = partialEstimate } };
    }

    private static List<LearningRecord> Tasks(string repository, string size, params decimal[] usd) => [.. usd.Select(u => Past(u, repository, size))];

    private static BudgetChoice Choose(DrivenGovernor settings, IReadOnlyList<LearningRecord> records, string? size = "M", decimal? own = null, string repository = Repo) =>
        BudgetHistory.Choose(settings, size, own, repository, records, Now);

    [Fact]
    public void A_task_with_its_own_budget_keeps_it_however_much_history_there_is()
    {
        var choice = Choose(Five, Tasks(Repo, "M", 1, 2, 3, 4, 5, 6), own: 2.5m);

        Assert.Equal(new BudgetChoice(2.5m, new BudgetSource("task")), choice);
    }

    [Fact]
    public void Below_the_minimum_the_flat_budget_of_the_size_class_applies_and_the_profiles_budgets_replace_the_built_in_ones()
    {
        var records = Tasks(Repo, "M", 1, 2, 3, 4);

        Assert.Equal(new BudgetChoice(4m, new BudgetSource("flat")), Choose(Five, records));
        Assert.Equal(new BudgetChoice(6m, new BudgetSource("flat")), Choose(Five with { Budgets = new Dictionary<string, decimal> { ["M"] = 6m } }, records));
        Assert.Equal(new BudgetChoice(10m, new BudgetSource("flat")), Choose(Five, [], size: "L"));
        Assert.Equal(new BudgetChoice(4m, new BudgetSource("flat")), Choose(Five, [], size: null));
    }

    [Fact]
    public void At_the_minimum_the_budget_is_the_percentile_of_their_spend_for_the_repository()
    {
        var choice = Choose(Five, Tasks(Repo, "M", 5, 1, 3, 2, 4));

        // Nearest rank: the 4th of 5 values for the 80th percentile.
        Assert.Equal(new BudgetChoice(4m, new BudgetSource("history", 5, 80, "repository")), choice);
    }

    [Fact]
    public void A_repository_with_too_few_uses_all_repositories_and_one_with_enough_ignores_the_rest()
    {
        var mixed = new List<LearningRecord>([.. Tasks(Repo, "M", 1, 2, 3), .. Tasks("x/y", "M", 10, 20, 30)]);
        Assert.Equal(new BudgetChoice(20m, new BudgetSource("history", 6, 80, "all")), Choose(Five, mixed));

        var enough = new List<LearningRecord>([.. Tasks(Repo, "M", 1, 2, 3, 4, 5), .. Tasks("x/y", "M", 50, 60, 70, 80, 90)]);
        Assert.Equal(new BudgetChoice(4m, new BudgetSource("history", 5, 80, "repository")), Choose(Five, enough));
        Assert.Equal(new BudgetChoice(80m, new BudgetSource("history", 5, 80, "repository")), Choose(Five, enough, repository: "x/y"));
        Assert.Equal(new BudgetChoice(70m, new BudgetSource("history", 10, 80, "all")), Choose(Five, enough, repository: "z/w"));
    }

    [Fact]
    public void Too_few_in_both_the_repository_and_all_repositories_falls_back_to_the_flat_budget()
    {
        var records = new List<LearningRecord>([.. Tasks(Repo, "M", 1, 2), .. Tasks("x/y", "M", 3, 4)]);

        Assert.Equal(new BudgetSource("flat"), Choose(Five, records).Source);
    }

    [Fact]
    public void Only_a_size_classs_own_tasks_count_and_no_size_means_M()
    {
        var records = new List<LearningRecord>([.. Tasks(Repo, "S", 1, 1, 1, 1, 1), .. Tasks(Repo, "M", 7, 7, 7, 7, 7)]);

        Assert.Equal(new BudgetChoice(1m, new BudgetSource("history", 5, 80, "repository")), Choose(Five, records, size: "S"));
        Assert.Equal(new BudgetChoice(7m, new BudgetSource("history", 5, 80, "repository")), Choose(Five, records, size: "M"));
        Assert.Equal(new BudgetChoice(7m, new BudgetSource("history", 5, 80, "repository")), Choose(Five, records, size: null));
        Assert.Equal(new BudgetSource("flat"), Choose(Five, records, size: "L").Source);
    }

    [Fact]
    public void Tasks_older_than_the_window_do_not_count()
    {
        var old = Enumerable.Range(0, 5).Select(_ => Past(9m, daysAgo: 31)).ToList();
        Assert.Equal(new BudgetSource("flat"), Choose(Five, old).Source);

        // The 30th day is inside a 30-day window and the 31st is not: only the first makes a fifth task.
        var edge = new List<LearningRecord>([.. Tasks(Repo, "M", 1, 2, 3, 4), Past(9m, daysAgo: 31)]);
        Assert.Equal(new BudgetSource("flat"), Choose(Five, edge).Source);
        Assert.Equal(new BudgetSource("history", 5, 80, "repository"), Choose(Five, [.. edge, Past(9m, daysAgo: 30)]).Source);
    }

    [Fact]
    public void Only_finished_tasks_count_a_completed_one_or_one_stopped_at_its_budget()
    {
        var failed = new List<LearningRecord>
        {
            Past(1m), Past(2m), Past(3m),
            Past(90m, outcome: LearningOutcome.Failed, stop: "session_error"),
            Past(90m, outcome: LearningOutcome.Partial, stop: "turn_limit"),
            Past(90m, outcome: LearningOutcome.NeedsInput, stop: "needs_input"),
            Past(90m, outcome: LearningOutcome.Partial, stop: "checkpoint_spend"),
        };
        Assert.Equal(new BudgetSource("flat"), Choose(Five, failed).Source);

        var stopped = new List<LearningRecord>([.. failed, Past(4m, outcome: LearningOutcome.Partial, stop: Governor.BudgetSpent), Past(5m, outcome: LearningOutcome.Partial, stop: Governor.BudgetSpent)]);
        Assert.Equal(new BudgetChoice(4m, new BudgetSource("history", 5, 80, "repository")), Choose(Five, stopped));
    }

    [Fact]
    public void A_task_with_no_spend_or_only_a_partial_estimate_is_no_figure()
    {
        var records = new List<LearningRecord>([.. Tasks(Repo, "M", 1, 2, 3, 4), Past(null), Past(99m, partialEstimate: true)]);

        Assert.Equal(new BudgetSource("flat"), Choose(Five, records).Source);
        Assert.Equal(new BudgetSource("history", 5, 80, "repository"), Choose(Five, [.. records, Past(5m)]).Source);
    }

    [Fact]
    public void Spend_that_is_all_zero_cannot_be_a_budget()
    {
        Assert.Equal(new BudgetSource("flat"), Choose(Five, Tasks(Repo, "M", 0, 0, 0, 0, 0)).Source);
    }

    [Fact]
    public void The_percentile_the_minimum_and_the_window_come_from_the_profile_and_default_to_20_tasks_60_days_and_the_80th_percentile()
    {
        var spends = Enumerable.Range(1, 10).Select(i => (decimal)i).ToArray();
        var median = new DrivenGovernor(History: new DrivenGovernorHistory(MinTasks: 10, Percentile: 50));
        Assert.Equal(new BudgetChoice(5m, new BudgetSource("history", 10, 50, "repository")), Choose(median, Tasks(Repo, "M", spends)));
        var max = new DrivenGovernor(History: new DrivenGovernorHistory(MinTasks: 10, Percentile: 100));
        Assert.Equal(10m, Choose(max, Tasks(Repo, "M", spends)).Usd);

        var defaults = new DrivenGovernor();
        var nineteen = Tasks(Repo, "M", [.. Enumerable.Repeat(2m, 19)]);
        Assert.Equal(new BudgetSource("flat"), Choose(defaults, nineteen).Source);
        var twenty = new List<LearningRecord>([.. nineteen, Past(10m, daysAgo: 59)]);
        Assert.Equal(new BudgetChoice(2m, new BudgetSource("history", 20, 80, "repository")), Choose(defaults, twenty));
        var oneOutside = new List<LearningRecord>([.. nineteen, Past(10m, daysAgo: 61)]);
        Assert.Equal(new BudgetSource("flat"), Choose(defaults, oneOutside).Source);
    }

    // The budget a driven task is held to

    private const string BatchRepo = "o/r";

    private static RunRequest One(DrivenTask? task = null) =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([task ?? new DrivenTask("t1", Goal: "one")], MaxParallel: 1, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

    private static string Token(TaskGrant g) => $"tok-{g.RunId}";

    private static SessionOutcome Done(decimal usd) => new(SessionStatus.Completed, "", 30, 1000, 200, 4000, null, 0, [], true, Commits: 4, UsdEstimate: usd);

    private static async Task<JsonlLearningStore> StoreWith(TempDir dir, params LearningRecord[] records)
    {
        var store = new JsonlLearningStore(Path.Combine(dir.Path, "learning.jsonl"));
        foreach (var record in records)
            await store.AppendAsync(record, default);
        return store;
    }

    private static List<LearningRecord> Recent(string size, params decimal[] usd) => [.. usd.Select(u => Past(u, BatchRepo, size, daysAgo: 0) with { At = DateTimeOffset.UtcNow.AddHours(-1) })];

    [Fact]
    public async Task A_task_is_held_to_the_budget_history_gives_its_size_class_and_the_session_and_the_record_say_where_it_came_from()
    {
        using var dir = new TempDir();
        var store = await StoreWith(dir, [.. Recent("M", 1, 2, 3)]);
        using var rig = new DrivenRunTests.Rig(learning: store, governor: new DrivenGovernor(History: new DrivenGovernorHistory(MinTasks: 3)), outcomes: (_, _) => Done(2.5m));

        await rig.Run.RunAsync(One(), BatchId, Token, default);

        var expected = new BudgetChoice(3m, new BudgetSource("history", 3, 80, "repository"));
        var session = rig.Volumes.Tasks.Single().Governor!;
        Assert.Equal((expected.Usd, expected.Source), (session.BudgetUsd, session.BudgetSource));
        var record = (await store.ReadAsync(default)).Select(e => e.Record).Single(r => r.BatchId == BatchId);
        Assert.Equal(new LearningBudget(expected.Usd, expected.Source), record.Budget);
    }

    [Fact]
    public async Task Every_session_of_a_continued_task_holds_the_same_budget()
    {
        using var dir = new TempDir();
        var store = await StoreWith(dir, [.. Recent("M", 1, 2, 3)]);
        using var rig = new DrivenRunTests.Rig(learning: store, governor: new DrivenGovernor(History: new DrivenGovernorHistory(MinTasks: 3)),
            outcomes: (_, goal) => goal.Contains("Continuation ", StringComparison.Ordinal) ? Done(1m)
                : new SessionOutcome(SessionStatus.Stalled, "turn_limit", 80, 1000, 200, 4000, null, 0, [], true, Commits: 3, UsdEstimate: 1m));

        await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Equal(2, rig.Volumes.Tasks.Count);
        Assert.All(rig.Volumes.Tasks, t => Assert.Equal((3m, new BudgetSource("history", 3, 80, "repository")), (t.Governor!.BudgetUsd, t.Governor.BudgetSource)));
    }

    [Fact]
    public async Task A_tasks_own_budget_beats_history_and_a_size_the_history_lacks_is_flat()
    {
        using var dir = new TempDir();
        var store = await StoreWith(dir, [.. Recent("M", 1, 2, 3)]);
        using var rig = new DrivenRunTests.Rig(learning: store, governor: new DrivenGovernor(History: new DrivenGovernorHistory(MinTasks: 3)), outcomes: (_, _) => Done(1m));

        await rig.Run.RunAsync(One(new DrivenTask("t1", Goal: "one", BudgetUsd: 2.5m)), "run-b1", Token, default);
        await rig.Run.RunAsync(One(new DrivenTask("t1", Goal: "one", Size: "S")), "run-b2", Token, default);

        Assert.Equal((2.5m, new BudgetSource("task")), (rig.Volumes.Tasks[0].Governor!.BudgetUsd, rig.Volumes.Tasks[0].Governor!.BudgetSource));
        Assert.Equal((1.5m, new BudgetSource("flat")), (rig.Volumes.Tasks[1].Governor!.BudgetUsd, rig.Volumes.Tasks[1].Governor!.BudgetSource));
    }

    [Fact]
    public async Task Tasks_of_another_repository_feed_a_budget_when_this_one_has_too_few()
    {
        using var dir = new TempDir();
        var store = await StoreWith(dir, [.. Recent("M", 1, 2, 3).Select(r => r with { Repository = "x/y" })]);
        using var rig = new DrivenRunTests.Rig(learning: store, governor: new DrivenGovernor(History: new DrivenGovernorHistory(MinTasks: 3)), outcomes: (_, _) => Done(1m));

        await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Equal((3m, new BudgetSource("history", 3, 80, "all")), (rig.Volumes.Tasks[0].Governor!.BudgetUsd, rig.Volumes.Tasks[0].Governor!.BudgetSource));
    }

    [Fact]
    public async Task Without_history_or_a_store_the_flat_budget_applies_and_a_store_that_cannot_be_read_does_not_fail_the_task()
    {
        using var dir = new TempDir();
        using var bare = new DrivenRunTests.Rig(outcomes: (_, _) => Done(1m));
        using var empty = new DrivenRunTests.Rig(learning: await StoreWith(dir), outcomes: (_, _) => Done(1m));
        using var broken = new DrivenRunTests.Rig(learning: new Unreadable(), outcomes: (_, _) => Done(1m));

        foreach (var rig in new[] { bare, empty, broken })
        {
            var result = await rig.Run.RunAsync(One(), BatchId, Token, default);
            Assert.Equal(ResultStatus.Completed, result.Status);
            Assert.Equal((4m, new BudgetSource("flat")), (rig.Volumes.Tasks[0].Governor!.BudgetUsd, rig.Volumes.Tasks[0].Governor!.BudgetSource));
        }
    }

    private sealed class Unreadable : ILearningStore
    {
        public Task AppendAsync(LearningRecord record, CancellationToken ct) => Task.CompletedTask;

        public Task AppendAsync(PrOutcomeRecord record, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<LearningEntry>> ReadAsync(CancellationToken ct) => throw new IOException("unreadable");
    }

    [Fact]
    public async Task A_task_the_history_budget_finishes_adds_to_the_history_of_the_next_one()
    {
        using var dir = new TempDir();
        var store = await StoreWith(dir, [.. Recent("M", 1, 1)]);
        using var rig = new DrivenRunTests.Rig(learning: store, governor: new DrivenGovernor(History: new DrivenGovernorHistory(MinTasks: 3)), outcomes: (_, _) => Done(9m));

        await rig.Run.RunAsync(One(), "run-b1", Token, default);
        await rig.Run.RunAsync(One(), "run-b2", Token, default);

        Assert.Equal(new BudgetSource("flat"), rig.Volumes.Tasks[0].Governor!.BudgetSource);
        // Two past tasks and the first batch's: three, so the second batch's task is held to the 80th percentile of 1, 1 and 9.
        Assert.Equal((9m, new BudgetSource("history", 3, 80, "repository")), (rig.Volumes.Tasks[1].Governor!.BudgetUsd, rig.Volumes.Tasks[1].Governor!.BudgetSource));
    }

    [Fact]
    public void A_governor_event_says_when_the_budget_is_from_history_and_says_nothing_of_a_flat_one()
    {
        GovernorDecision Decision(BudgetSource? source) =>
            new(Now, 7, GovernorMode.Observe, GovernorAction.Checkpoint, "spend", "spend_checkpoint", new Dictionary<string, string>(), 3.5m, 4m, "M", BudgetSource: source);

        Assert.Equal("governor observe: checkpoint on spend (spend checkpoint) at turn 7, spend 3.50 of 4.00 (p80 of 23 past tasks)", TaskRunner.Describe(Decision(new BudgetSource("history", 23, 80, "repository"))));
        Assert.Equal("governor observe: checkpoint on spend (spend checkpoint) at turn 7, spend 3.50 of 4.00 (p90 of 40 past tasks, all repositories)", TaskRunner.Describe(Decision(new BudgetSource("history", 40, 90, "all"))));
        Assert.Equal("governor observe: checkpoint on spend (spend checkpoint) at turn 7, spend 3.50 of 4.00", TaskRunner.Describe(Decision(new BudgetSource("flat"))));
        Assert.Equal("governor observe: checkpoint on spend (spend checkpoint) at turn 7, spend 3.50 of 4.00", TaskRunner.Describe(Decision(null)));
    }

    [Theory]
    [InlineData("""{"schema":"profile/v1","driven":{"governor":{"history":{"min_tasks":20,"window_days":60,"percentile":80}}}}""", true)]
    [InlineData("""{"schema":"profile/v1","driven":{"governor":{"history":{}}}}""", true)]
    [InlineData("""{"schema":"profile/v1","driven":{"governor":{"history":{"min_tasks":0}}}}""", false)]
    [InlineData("""{"schema":"profile/v1","driven":{"governor":{"history":{"percentile":101}}}}""", false)]
    [InlineData("""{"schema":"profile/v1","driven":{"governor":{"history":{"percentile":0}}}}""", false)]
    [InlineData("""{"schema":"profile/v1","driven":{"governor":{"history":{"window_days":0}}}}""", false)]
    [InlineData("""{"schema":"profile/v1","driven":{"governor":{"history":{"other":1}}}}""", false)]
    public void The_schema_checks_governor_history(string profile, bool valid)
    {
        using var doc = JsonDocument.Parse(profile);
        Assert.Equal(valid, ConfigFileTests.ProfileSchema.Evaluate(doc.RootElement).IsValid);
    }
}
