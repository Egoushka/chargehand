using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Tests;

/// <summary>Issue 454: a task that ends failed or partial gets a root-cause class in the learning log from one cheap model call (off by default), and a weekly job hands the top classes to a sink.
/// The model is a fake runner and the sink a fake; nothing reaches a network.</summary>
public sealed class FailureAutopsyTests : IDisposable
{
    private const string BatchId = "run-batch-l";
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private JsonlLearningStore Store => new(Path.Combine(_dir.Path, "learning.jsonl"));

    private static RunRequest One() =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", "abc1234def5678abc1234def5678abc1234def56")),
            Driven: new RequestDriven([new DrivenTask("t1", Goal: "one")], MaxParallel: 1, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

    private static ResultContract Answer(ResultStatus status, string summary) =>
        new("result/v1", "run-autopsy", "answer", "trace", new PromptChain([], new AsSent("2.0.16", "build", "m", "2026-10-13")), status, summary, [], [], [], [], 0.9, new Usage(0, 0, 0, 0, 0),
            status == ResultStatus.Failed ? new ResultError(ErrorCode.CheckoutInvalid, "the checkout is invalid", false) : null);

    private sealed class FakeModel(Func<string, ResultContract> answer) : IFailureAutopsy
    {
        public List<AutopsyInput> Inputs { get; } = [];

        public Task<AutopsyRecord> ClassifyAsync(AutopsyInput input, CancellationToken ct)
        {
            Inputs.Add(input);
            var result = answer(input.Record.RunId);
            return result.Status == ResultStatus.Completed
                ? Task.FromResult(new AutopsyRecord(input.Record.RunId, Now, result.Summary.Split(' ')[0], null))
                : throw new AutopsyException(result.Summary);
        }
    }

    private sealed class FakeSink(Exception? fail = null) : IFailureClusterSink
    {
        public List<IReadOnlyList<FailureCluster>> Got { get; } = [];

        public Task PublishAsync(IReadOnlyList<FailureCluster> clusters, CancellationToken ct)
        {
            Got.Add(clusters);
            return fail is null ? Task.CompletedTask : throw fail;
        }
    }

    private static SessionOutcome Failed() => new(SessionStatus.Failed, "session_error", 3, 10, 5, 0, null, 1, [], false);

    private static SessionOutcome Done() => new(SessionStatus.Completed, "", 30, 1000, 200, 4000, null, 0, [], true, CacheWriteTokens: 300, Commits: 4, UsdEstimate: 1m);

    [Fact]
    public async Task A_failed_task_gets_the_models_class_in_the_learning_log_and_a_completed_one_is_not_asked_about()
    {
        var model = new FakeModel(_ => Answer(ResultStatus.Completed, "auth the token was refused"));
        using var rig = new DrivenRunTests.Rig(learning: Store, learningSettings: new DrivenLearning(Autopsy: true), autopsy: model, outcomes: (_, _) => Failed());
        using var ok = new DrivenRunTests.Rig(learning: new JsonlLearningStore(Path.Combine(_dir.Path, "ok.jsonl")), learningSettings: new DrivenLearning(Autopsy: true), autopsy: model, outcomes: (_, _) => Done());

        var result = await rig.Run.RunAsync(One(), BatchId, id => $"tok-{id.RunId}", default);
        await ok.Run.RunAsync(One(), BatchId, id => $"tok-{id.RunId}", default);

        Assert.Equal(ResultStatus.Failed, result.Status);
        var asked = Assert.Single(model.Inputs);
        Assert.Equal((LearningOutcome.Failed, BatchId), (asked.Record.Outcome, asked.BatchId));
        var entry = Assert.Single(await Store.ReadAsync(default));
        Assert.Equal("auth", entry.Autopsy!.Class);
    }

    [Fact]
    public async Task With_the_autopsy_off_nothing_is_asked()
    {
        var model = new FakeModel(_ => Answer(ResultStatus.Completed, "auth"));
        using var rig = new DrivenRunTests.Rig(learning: Store, autopsy: model, outcomes: (_, _) => Failed());

        await rig.Run.RunAsync(One(), BatchId, id => $"tok-{id.RunId}", default);

        Assert.Empty(model.Inputs);
        Assert.False(new DrivenLearning().Autopsy);
        Assert.Null(Assert.Single(await Store.ReadAsync(default)).Autopsy);
    }

    [Fact]
    public async Task An_autopsy_that_cannot_be_classified_is_skipped_and_never_changes_the_batch_result()
    {
        var model = new FakeModel(_ => Answer(ResultStatus.Failed, "the autopsy run ended failed"));
        using var rig = new DrivenRunTests.Rig(learning: Store, learningSettings: new DrivenLearning(Autopsy: true), autopsy: model, outcomes: (_, _) => Failed());

        var result = await rig.Run.RunAsync(One(), BatchId, id => $"tok-{id.RunId}", default);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Null(Assert.Single(await Store.ReadAsync(default)).Autopsy);
    }

    [Theory]
    [InlineData("oom_kill the container was killed for memory", "oom_kill", "the container was killed for memory")]
    [InlineData("OOM_KILL", "oom_kill", null)]
    [InlineData("disk_full the volume filled up", "other", "the volume filled up")]
    public async Task The_model_answer_is_one_listed_class_and_a_short_reason_and_anything_else_is_other(string answer, string cls, string? reason)
    {
        List<RunRequest> asked = [];
        var autopsy = new ModelAutopsy((request, _, _, _) =>
        {
            asked.Add(request);
            return Task.FromResult(Answer(ResultStatus.Completed, answer));
        }, "cheap");

        var record = await autopsy.ClassifyAsync(new AutopsyInput(One(), BatchId, LearningTests.Record("r1", outcome: LearningOutcome.Failed)), default);

        Assert.Equal((cls, reason), (record.Class, record.Reason));
        Assert.Equal("cheap", Assert.Single(asked).Context.Preset);
        Assert.Contains("stop_reason: turn_limit", asked[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Run the tasks", asked[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reason_over_the_limit_is_cut()
    {
        var autopsy = new ModelAutopsy((_, _, _, _) => Task.FromResult(Answer(ResultStatus.Completed, "other " + new string('x', 500))), "cheap");

        var record = await autopsy.ClassifyAsync(new AutopsyInput(One(), BatchId, LearningTests.Record("r1", outcome: LearningOutcome.Partial)), default);

        Assert.EndsWith(" [cut]", record.Reason, StringComparison.Ordinal);
        Assert.True(record.Reason!.Length <= ModelAutopsy.MaxReasonLength + " [cut]".Length);
    }

    [Fact]
    public async Task The_latest_autopsy_of_a_run_wins_and_the_clusters_count_the_window_largest_first()
    {
        var store = Store;
        foreach (var (run, cls) in new[] { ("a", "auth"), ("b", "verification"), ("c", "verification"), ("d", "oom_kill") })
        {
            await store.AppendAsync(LearningTests.Record(run, outcome: LearningOutcome.Failed), default);
            await store.AppendAsync(new AutopsyRecord(run, Now, "other"), default);
            await store.AppendAsync(new AutopsyRecord(run, Now, cls), default);
        }
        await store.AppendAsync(LearningTests.Record("old", outcome: LearningOutcome.Failed, at: Now.AddDays(-30)), default);
        await store.AppendAsync(new AutopsyRecord("old", Now, "secret_guard"), default);
        await store.AppendAsync(LearningTests.Record("plain", outcome: LearningOutcome.Failed), default);

        var top = FailureClusterReport.Top(await store.ReadAsync(default), Now.AddDays(-7), 2);

        Assert.Equal([("verification", 2), ("auth", 1)], top.Select(c => (c.Class, c.Count)));
        Assert.Equal(["b", "c"], top[0].RunIds);
    }

    [Fact]
    public async Task The_weekly_job_hands_the_top_clusters_to_the_sink_and_a_sink_that_throws_is_ignored()
    {
        var store = Store;
        await store.AppendAsync(LearningTests.Record("a", outcome: LearningOutcome.Failed, at: DateTimeOffset.UtcNow.AddHours(-1)), default);
        await store.AppendAsync(new AutopsyRecord("a", Now, "auth"), default);
        var sink = new FakeSink();
        using var rig = new DrivenRunTests.Rig(learning: store, learningSettings: new DrivenLearning(), clusterSink: sink);
        using var broken = new DrivenRunTests.Rig(learning: store, learningSettings: new DrivenLearning(), clusterSink: new FakeSink(new IOException("tracker down")));
        using var none = new DrivenRunTests.Rig(learning: store, learningSettings: new DrivenLearning());

        Assert.Equal(1, await rig.Run.ClusterReportAsync(default));
        Assert.Equal("auth", Assert.Single(Assert.Single(sink.Got)).Class);
        Assert.Equal(0, await broken.Run.ClusterReportAsync(default));
        Assert.Equal(0, await none.Run.ClusterReportAsync(default));
        Assert.Equal(TimeSpan.FromDays(7), rig.Run.ClusterCheckEvery);
    }

    [Theory]
    [InlineData("""{"autopsy":true,"autopsy_preset":"cheap","cluster_top":3,"cluster_check_days":7}""", true)]
    [InlineData("""{"autopsy":"yes"}""", false)]
    [InlineData("""{"cluster_top":0}""", false)]
    public void The_schema_checks_the_autopsy_keys(string learning, bool valid)
    {
        using var doc = System.Text.Json.JsonDocument.Parse("{\"schema\":\"profile/v1\",\"driven\":{\"learning\":" + learning + "}}");
        Assert.Equal(valid, ConfigFileTests.ProfileSchema.Evaluate(doc.RootElement).IsValid);
    }
}
