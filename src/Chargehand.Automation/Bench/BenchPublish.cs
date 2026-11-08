using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Chargehand.Bench;

/// <summary><c>chargehand bench publish &lt;results.json&gt;</c>: the merge rate of a replay run (merged pull requests replayed as tasks, ADR 0087) as one small JSON file a status page
/// can read. A replayed task counts as merged when its draft pull request exists and the merged pull request's withheld tests pass on its branch. The file holds counts and
/// rates only: no task id, repository name, goal or pull request number, because replay tasks are real work.</summary>
public static class BenchPublish
{
    public const string SchemaId = "bench-merge-rate/v1";
    public const int DefaultMinTasks = 20;

    public const string Definition = "Merged pull requests replayed from their parent commit; a task counts as merged when chargehand's draft pull request exists and the merged pull request's own tests, withheld from the session, pass on its branch.";

    /// <summary>Why the run cannot be published, or null. An unfinished run or one with fewer graded tasks than <paramref name="minTasks"/> is not a release number.</summary>
    public static string? Refusal(BenchResults results, int minTasks)
    {
        if (!results.Complete)
            return "the run did not finish; publish a complete run.";
        var graded = BenchStats.Compute(results).Pass.Total;
        return graded < minTasks ? $"{graded} graded tasks, fewer than the {minTasks} a published merge rate needs (--min-tasks)." : null;
    }

    public static string ToJson(BenchResults results)
    {
        var s = BenchStats.Compute(results);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("schema", SchemaId);
            w.WriteString("taskset", results.Taskset);
            w.WriteString("taskset_sha256", results.TasksetSha256);
            w.WriteString("arm", results.ArmName);
            w.WriteString("started_at", results.StartedAt);
            if (results.FinishedAt is { } finished)
                w.WriteString("finished_at", finished);
            w.WriteNumber("tasks", s.Tasks);
            w.WriteNumber("not_run", s.NotRun);
            w.WriteNumber("merged", s.Pass.Hits);
            w.WriteNumber("graded", s.Pass.Total);
            if (s.Pass.Value is { } rate && s.Pass.Interval is var (low, high))
            {
                w.WriteNumber("merge_rate", Math.Round(rate, 4));
                w.WriteNumber("ci95_low", Math.Round(low, 4));
                w.WriteNumber("ci95_high", Math.Round(high, 4));
            }
            else
                w.WriteNull("merge_rate");
            if (s.UsdPerPass is { } usd)
                w.WriteNumber("usd_per_merge", Math.Round(usd, 2));
            w.WriteString("display", Display(s.Pass));
            w.WriteString("definition", Definition);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>"Merge rate 45% (9/20, 95% CI 26-66%)": the line a status page shows as is.</summary>
    public static string Display(Rate rate) => string.Create(CultureInfo.InvariantCulture, $"Merge rate {rate.Format()}");

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        string? path = null, outFile = null;
        var minTasks = DefaultMinTasks;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Count:
                    outFile = args[++i];
                    break;
                case "--min-tasks" when i + 1 < args.Count && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0:
                    minTasks = n;
                    i++;
                    break;
                case var a when !a.StartsWith("--", StringComparison.Ordinal) && path is null:
                    path = a;
                    break;
                default:
                    error.WriteLine(BenchCli.Usage);
                    return 2;
            }
        }
        if (path is null)
        {
            error.WriteLine(BenchCli.Usage);
            return 2;
        }
        BenchResults results;
        try
        {
            results = BenchResults.Parse(File.ReadAllText(path)) is { Schema: BenchResults.SchemaId } parsed ? parsed : throw new FormatException($"not a {BenchResults.SchemaId} file");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or JsonException)
        {
            error.WriteLine($"{path}: {e.Message}");
            return 2;
        }
        if (Refusal(results, minTasks) is { } why)
        {
            error.WriteLine($"{path}: not published: {why}");
            return 1;
        }
        var json = ToJson(results);
        if (outFile is null)
            output.Write(json);
        else
        {
            File.WriteAllText(outFile, json);
            output.WriteLine(Display(BenchStats.Compute(results).Pass));
        }
        return 0;
    }
}
