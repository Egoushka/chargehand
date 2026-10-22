using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Oversight.Tests;

/// <summary>ADR 0045: the weekly failure-autopsy cluster job, with a fake store and a fake sink.</summary>
public class AutopsyClustersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static LearningRecord Record(string run, string repository = "o/r") =>
        new("batch", $"task-{run}", run, Now, repository, "M", "default", new LearningBudget(4m, new BudgetSource("flat")),
            new LearningSpend(1m, false, 10, 5, 0, 0, 3, 0), LearningOutcome.Failed, "turn_limit", "observe", [], false);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeStore(IReadOnlyList<LearningEntry> entries) : ILearningStore
    {
        public Task AppendAsync(LearningRecord record, CancellationToken ct) => throw new NotSupportedException();

        public Task AppendAsync(PrOutcomeRecord record, CancellationToken ct) => throw new NotSupportedException();

        public Task AppendAsync(AutopsyRecord record, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<LearningEntry>> ReadAsync(CancellationToken ct, bool includeBench = false) => Task.FromResult(entries);
    }

    private sealed class FakeSink(Exception? failFor = null, string? failClass = null) : IAutopsyClusterSink
    {
        public List<AutopsyCluster> Got { get; } = [];

        public Task PublishAsync(AutopsyCluster cluster, CancellationToken ct)
        {
            Got.Add(cluster);
            return cluster.Class == failClass && failFor is not null ? throw failFor : Task.CompletedTask;
        }
    }

    private static LearningEntry Entry(string run, string cls, string repository = "o/r", DateTimeOffset? at = null) =>
        new(Record(run, repository), null, new AutopsyRecord(run, at ?? Now.AddDays(-1), cls));

    [Fact]
    public async Task Classes_below_the_minimum_are_dropped_and_the_rest_are_filed_most_frequent_first()
    {
        List<LearningEntry> entries = [
            Entry("a1", AutopsyClass.Auth), Entry("a2", AutopsyClass.Auth), Entry("a3", AutopsyClass.Auth),
            Entry("o1", AutopsyClass.OomKill), Entry("o2", AutopsyClass.OomKill),
            Entry("v1", AutopsyClass.Verification),
        ];
        var sink = new FakeSink();
        var job = new AutopsyClusterJob(new FakeStore(entries), sink, new FixedClock(Now));

        var filed = await job.RunAsync(days: 7, min: 2, top: 5, default);

        Assert.Equal(2, filed);
        Assert.Equal([(AutopsyClass.Auth, 3), (AutopsyClass.OomKill, 2)], sink.Got.Select(c => (c.Class, c.Count)));
    }

    [Fact]
    public async Task Only_the_top_clusters_are_filed_and_separate_repositories_are_separate_clusters()
    {
        List<LearningEntry> entries = [
            Entry("a1", AutopsyClass.Auth, "o/r"), Entry("a2", AutopsyClass.Auth, "o/r"),
            Entry("a3", AutopsyClass.Auth, "o/other"), Entry("a4", AutopsyClass.Auth, "o/other"), Entry("a5", AutopsyClass.Auth, "o/other"),
            Entry("v1", AutopsyClass.Verification, "o/r"), Entry("v2", AutopsyClass.Verification, "o/r"),
        ];
        var sink = new FakeSink();
        var job = new AutopsyClusterJob(new FakeStore(entries), sink, new FixedClock(Now));

        var filed = await job.RunAsync(days: 7, min: 2, top: 1, default);

        Assert.Equal(1, filed);
        var only = Assert.Single(sink.Got);
        Assert.Equal((AutopsyClass.Auth, "o/other", 3), (only.Class, only.Repository, only.Count));
    }

    [Fact]
    public async Task An_autopsy_older_than_the_window_or_a_task_with_none_is_left_out()
    {
        List<LearningEntry> entries = [
            Entry("a1", AutopsyClass.Auth, at: Now.AddDays(-10)), Entry("a2", AutopsyClass.Auth, at: Now.AddDays(-10)),
            new(Record("n1"), null, null),
        ];
        var job = new AutopsyClusterJob(new FakeStore(entries), new FakeSink(), new FixedClock(Now));

        Assert.Equal(0, await job.RunAsync(days: 7, min: 1, top: 5, default));
    }

    [Fact]
    public async Task A_sink_that_throws_for_one_cluster_does_not_keep_the_others_from_being_filed()
    {
        List<LearningEntry> entries = [
            Entry("a1", AutopsyClass.Auth), Entry("a2", AutopsyClass.Auth),
            Entry("o1", AutopsyClass.OomKill), Entry("o2", AutopsyClass.OomKill),
        ];
        var sink = new FakeSink(new InvalidOperationException("tracker is down"), failClass: AutopsyClass.Auth);
        var job = new AutopsyClusterJob(new FakeStore(entries), sink, new FixedClock(Now));

        var filed = await job.RunAsync(days: 7, min: 1, top: 5, default);

        Assert.Equal(1, filed);
        Assert.Equal(2, sink.Got.Count);
    }
}
