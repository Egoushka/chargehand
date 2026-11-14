using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Chargehand.Modules.Oversight.Api;

/// <summary><c>chargehand learn publish</c> (issue 900): the merge rate of real driven tasks, each one a GitHub issue chargehand ran unattended to a pull request, as the
/// small JSON file an external status page reads: <c>{ "merged": n, "batches": n, "asOf": "&lt;ISO 8601&gt;" }</c>. Every record in the learning log is a driven task that
/// ran with nobody watching the session (ADR 0039), so "batches" here counts task attempts, not <see cref="LearningRecord.BatchId"/> (several tasks started together under
/// one request); that is what the hand count this replaces (docs/baseline-2026-10.md) also counted. Benchmark tasks (<see cref="LearningRecord.Bench"/>) are left out, same
/// as <c>learn report</c>'s default. This is not <c>chargehand bench publish</c> (<c>bench-merge-rate/v1</c>): that is a replay benchmark's rate, not production batches.</summary>
public static class UnattendedMergeRate
{
    public static int Merged(IReadOnlyList<LearningEntry> entries) => entries.Count(IsMerged);

    private static bool IsMerged(LearningEntry e) =>
        e.Record.Pr is not null && e.PrOutcome?.Outcome is PrOutcomes.Merged or PrOutcomes.MergedUnchanged or PrOutcomes.MergedEdited;

    public static string ToJson(IReadOnlyList<LearningEntry> entries, DateTimeOffset asOf)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("merged", Merged(entries));
            w.WriteNumber("batches", entries.Count);
            w.WriteString("asOf", asOf.ToString("O", CultureInfo.InvariantCulture));
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary><c>chargehand learn publish [--out &lt;file&gt;]</c>: prints the JSON to stdout, or writes it to <c>--out</c>. Exit 0, 2 on a usage error.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, ILearningStore store, TextWriter output, TextWriter error, DateTimeOffset now, CancellationToken ct)
    {
        string? outFile = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Count)
                outFile = args[++i];
            else
            {
                error.WriteLine("usage: chargehand learn publish [--out <file>]");
                return 2;
            }
        }
        var json = ToJson(await store.ReadAsync(ct), now);
        if (outFile is null)
            output.Write(json);
        else
            await File.WriteAllTextAsync(outFile, json, ct);
        return 0;
    }
}
