using System.Globalization;
using System.Text.Json;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Oversight.Tests;

/// <summary>Issue 900: <c>chargehand learn publish</c> counts the unattended merge rate of real driven tasks (each one a goal run to a pull request with nobody
/// watching) as <c>{ merged, batches, asOf }</c>, for an external status page. The entries are synthetic.</summary>
public class UnattendedMergeRateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly RemoteRepository Remote = new("o", "r", ForgeKind.GitHub, null);

    private static LearningEntry Entry(string id, string? prOutcome, bool bench = false) =>
        new(new LearningRecord("batch", id, $"run-{id}", Now, "o/r", "M", "caller", new LearningBudget(null, new BudgetSource("none")),
                new LearningSpend(1m, false, 10, 5, 0, 0, 3, 0), LearningOutcome.Completed, "completed", null, [], false,
                Pr: prOutcome is null ? null : new LearningPr($"https://github.com/o/r/pull/1", 1, Remote, null), Bench: bench),
            PrOutcome: prOutcome is null ? null : new PrOutcomeRecord($"run-{id}", Now, prOutcome, null));

    [Fact]
    public void Merged_counts_every_merged_outcome_but_not_closed_open_or_no_pull_request()
    {
        List<LearningEntry> entries =
        [
            Entry("a", PrOutcomes.Merged),
            Entry("b", PrOutcomes.MergedUnchanged),
            Entry("c", PrOutcomes.MergedEdited),
            Entry("d", PrOutcomes.Closed),
            Entry("e", PrOutcomes.Open),
            Entry("f", null),
        ];

        Assert.Equal(3, UnattendedMergeRate.Merged(entries));
    }

    [Fact]
    public void The_json_holds_exactly_merged_batches_and_asof()
    {
        List<LearningEntry> entries = [Entry("a", PrOutcomes.MergedUnchanged), Entry("b", PrOutcomes.Closed), Entry("c", null)];

        var doc = JsonDocument.Parse(UnattendedMergeRate.ToJson(entries, Now));

        Assert.Equal(["merged", "batches", "asOf"], doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(1, doc.RootElement.GetProperty("merged").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("batches").GetInt32());
        Assert.Equal(Now, DateTimeOffset.Parse(doc.RootElement.GetProperty("asOf").GetString()!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task The_publish_command_prints_the_json_and_excludes_benchmark_tasks()
    {
        var store = new MemoryLearning([Entry("a", PrOutcomes.Merged), Entry("bench", PrOutcomes.Merged, bench: true)]);
        StringWriter output = new(), error = new();

        Assert.Equal(0, await UnattendedMergeRate.RunAsync([], store, output, error, Now, default));

        var doc = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, doc.RootElement.GetProperty("merged").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("batches").GetInt32());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task The_out_flag_writes_the_file_instead_of_stdout()
    {
        var store = new MemoryLearning([Entry("a", PrOutcomes.Merged)]);
        var path = Path.Combine(Path.GetTempPath(), $"chargehand-publish-{Guid.NewGuid():N}.json");
        try
        {
            StringWriter output = new(), error = new();
            Assert.Equal(0, await UnattendedMergeRate.RunAsync(["--out", path], store, output, error, Now, default));

            Assert.Equal("", output.ToString());
            var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(1, doc.RootElement.GetProperty("batches").GetInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task An_unknown_flag_is_a_usage_error()
    {
        var store = new MemoryLearning([]);
        StringWriter output = new(), error = new();

        Assert.Equal(2, await UnattendedMergeRate.RunAsync(["--bogus"], store, output, error, Now, default));
        Assert.Contains("usage: chargehand learn publish", error.ToString());
        Assert.Equal("", output.ToString());
    }

    private sealed class MemoryLearning(IReadOnlyList<LearningEntry> entries) : ILearningStore
    {
        public Task AppendAsync(LearningRecord record, CancellationToken ct) => throw new NotSupportedException();

        public Task AppendAsync(PrOutcomeRecord record, CancellationToken ct) => throw new NotSupportedException();

        public Task AppendAsync(AutopsyRecord record, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<LearningEntry>> ReadAsync(CancellationToken ct, bool includeBench = false) =>
            Task.FromResult<IReadOnlyList<LearningEntry>>([.. entries.Where(e => includeBench || !e.Record.Bench)]);
    }
}
