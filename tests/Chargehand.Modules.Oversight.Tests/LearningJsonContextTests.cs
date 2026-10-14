using System.Text.Json;
using System.Text.Json.Nodes;
using Chargehand.Contracts;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Oversight.Tests;

/// <summary>ADR 0060: <c>learning.jsonl</c> is written without reflection, byte for byte as the contract options wrote it, and lines written before read back the same.</summary>
public sealed class LearningJsonContextTests : IDisposable
{
    // Characters the default encoder escapes, so a difference in encoders shows.
    private const string Text = "owner/<repo> & \"quotes\" — ünïcode";

    private readonly string _dir = Directory.CreateTempSubdirectory("learning-json-").FullName;

    private static readonly LearningRecord Full = new("b1", "t1", "r1", new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.FromHours(3)), Text, "L", "plan",
        new LearningBudget(12.5m, new BudgetSource("history", 23, 80, "repository")), new LearningSpend(3.1234m, true, 1000, 200, 30000, 400, 17, 1), LearningOutcome.Partial,
        Governor.CheckpointSpend, "enforce", [new GovernorCount("spend", "checkpoint", 1, 1), new GovernorCount("repeated_call", "warn", 2, 0)], true,
        new LearningPr("https://example.test/o/r/pull/7", 7, new RemoteRepository("group/sub", "r", ForgeKind.GitLab, "gitlab.example.test"), "abc1234"), "acct-1", 812);

    // Every optional value left out: the nulls are not written.
    private static readonly LearningRecord Bare = new("b2", "t2", "r2", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), "o/r", "M", "default",
        new LearningBudget(null, new BudgetSource("none")), new LearningSpend(null, false, 0, 0, 0, 0, 0, 0), LearningOutcome.Completed, null, null, [], false);

    private static readonly PrOutcomeRecord Outcome = new("r1", new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero), PrOutcomes.MergedEdited, null);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task A_line_is_written_as_the_contract_options_wrote_it()
    {
        var path = Path.Combine(_dir, "learning.jsonl");
        var store = new JsonlLearningStore(path);
        await store.AppendAsync(Full, CancellationToken.None);
        await store.AppendAsync(Bare, CancellationToken.None);
        await store.AppendAsync(Outcome, CancellationToken.None);

        var before = string.Concat(Before("task", Full), Before("task", Bare), Before("pr_outcome", Outcome));
        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_line_written_before_reads_back_the_same()
    {
        var path = Path.Combine(_dir, "learning.jsonl");
        await File.WriteAllTextAsync(path, string.Concat(Before("task", Full), Before("task", Bare), Before("pr_outcome", Outcome)));

        var entries = await new JsonlLearningStore(path).ReadAsync(CancellationToken.None);

        Assert.Equal([Bare.RunId, Full.RunId], entries.Select(e => e.Record.RunId));
        Assert.Equal(Before("task", Full), Before("task", entries[1].Record));
        Assert.Equal(Before("task", Bare), Before("task", entries[0].Record));
        Assert.Equal(Outcome, entries[1].PrOutcome);
        Assert.Null(entries[0].PrOutcome);
    }

    // What the store wrote before ADR 0060: the reflection serializer with the contract options.
    private static string Before<T>(string type, T record)
    {
        var node = JsonSerializer.SerializeToNode(record, ContractJson.Options)!;
        node["type"] = type;
        return node.ToJsonString() + "\n";
    }
}
