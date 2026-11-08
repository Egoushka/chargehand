using System.Text.Json;
using Chargehand.Bench;

namespace Chargehand.Tests;

/// <summary><c>bench publish</c>: the merge rate of a replay run as a file a status page reads, with counts only and no task names.</summary>
public class BenchPublishTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static TaskResult Task(int i, bool merged, string state = "completed") =>
        new($"pr-secret-{i}", "private-repo", "dotnet", "M", "change", state, null, null, $"chargehand/pr-{i}", null, new AcceptanceResult(merged, merged ? 0 : 1, false, 3, ""),
            false, false, false, null, 0.50m, false, 1000, 0, 0, 10, 0, [], 60, "");

    private static BenchResults Results(int merged, int total, bool complete = true) =>
        new(BenchResults.SchemaId, "replay", "abc123", Start, Start.AddHours(2), complete, [new RepositoryPin("private-repo", "dotnet", "https://example.com/r.git", "c1")],
            [.. Enumerable.Range(0, total).Select(i => Task(i, i < merged))]);

    [Fact]
    public void The_file_has_the_merge_rate_with_its_interval_and_no_task_names()
    {
        var json = BenchPublish.ToJson(Results(9, 20));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(BenchPublish.SchemaId, root.GetProperty("schema").GetString());
        Assert.Equal(9, root.GetProperty("merged").GetInt32());
        Assert.Equal(20, root.GetProperty("graded").GetInt32());
        Assert.Equal(0.45, root.GetProperty("merge_rate").GetDouble());
        Assert.InRange(root.GetProperty("ci95_low").GetDouble(), 0.25, 0.27);
        Assert.InRange(root.GetProperty("ci95_high").GetDouble(), 0.64, 0.67);
        Assert.Equal(1.11m, root.GetProperty("usd_per_merge").GetDecimal());
        Assert.Equal("chargehand", root.GetProperty("arm").GetString());
        Assert.StartsWith("Merge rate 45% (9/20, 95% CI ", root.GetProperty("display").GetString());
        Assert.DoesNotContain("pr-secret", json);
        Assert.DoesNotContain("private-repo", json);
    }

    [Fact]
    public void An_unfinished_or_small_run_is_not_published()
    {
        Assert.Null(BenchPublish.Refusal(Results(9, 20), 20));
        Assert.Contains("did not finish", BenchPublish.Refusal(Results(9, 20, complete: false), 20));
        Assert.Contains("19 graded tasks", BenchPublish.Refusal(Results(9, 19), 20));
        Assert.Null(BenchPublish.Refusal(Results(9, 19), 10));
    }

    [Fact]
    public void Tasks_that_never_ran_are_not_counted_as_graded()
    {
        var results = Results(5, 20);
        results = results with { Tasks = [.. results.Tasks.Select((t, i) => i >= 18 ? t with { State = "not_run", Acceptance = null } : t)] };
        using var doc = JsonDocument.Parse(BenchPublish.ToJson(results));
        Assert.Equal(18, doc.RootElement.GetProperty("graded").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("not_run").GetInt32());
        Assert.NotNull(BenchPublish.Refusal(results, 20));
    }

    [Fact]
    public void The_command_writes_the_file_and_refuses_what_it_cannot_publish()
    {
        using var dir = new TempDir();
        var good = dir.Write("good/results.json", Results(9, 20).ToJson());
        var small = dir.Write("small/results.json", Results(3, 5).ToJson());
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, BenchPublish.Run([good], output, error));
        Assert.Contains("\"merge_rate\": 0.45", output.ToString());

        var file = Path.Combine(dir.Path, "merge-rate.json");
        output = new StringWriter();
        Assert.Equal(0, BenchPublish.Run([good, "--out", file], output, error));
        Assert.StartsWith("Merge rate 45%", output.ToString());
        Assert.Contains(BenchPublish.SchemaId, File.ReadAllText(file));

        Assert.Equal(1, BenchPublish.Run([small], new StringWriter(), error));
        Assert.Contains("not published", error.ToString());
        Assert.Equal(0, BenchPublish.Run([small, "--min-tasks", "5"], new StringWriter(), new StringWriter()));
        Assert.Equal(2, BenchPublish.Run([], new StringWriter(), new StringWriter()));
        Assert.Equal(2, BenchPublish.Run([good, "--min-tasks", "0"], new StringWriter(), new StringWriter()));
        Assert.Equal(2, BenchPublish.Run([dir.Write("not.json", "{\"hello\":1}")], new StringWriter(), new StringWriter()));
    }
}
