using System.Text;
using System.Text.Json;
using Chargehand.Contracts;

namespace Chargehand.Verification;

/// <summary>One accepted disproof (spec decision 5): the claim it disproved, the disprove run that earned it, that run's
/// preset, always 1 point, and when. A human may append a line with <c>Points</c> -1 to revoke a point by hand; nothing
/// else removes one.</summary>
public sealed record BountyEntry(string ClaimId, string RunId, string Preset, int Points, DateTimeOffset At);

/// <summary>bounty.jsonl next to the run log: append-only, one line per accepted disproof (spec decision 5). Nothing reads it
/// to change a route, a preset or an intake rank on its own (spec "Why not"); <c>chargehand routes</c> only tallies it as a
/// suggestion.</summary>
public sealed class BountyLedger(string path)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task AppendAsync(BountyEntry entry, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, ContractJson.Options) + "\n");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await file.WriteAsync(line, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<IReadOnlyList<BountyEntry>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(path))
            return [];
        var entries = new List<BountyEntry>();
        foreach (var line in (await File.ReadAllTextAsync(path, ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (JsonSerializer.Deserialize<BountyEntry>(line, ContractJson.Options) is { } entry)
                    entries.Add(entry);
            }
            catch (JsonException)
            {
            }
        }
        return entries;
    }

    /// <summary>Points per preset, summed (a revoking line's negative points included) — <c>chargehand routes</c>' bounty column.</summary>
    public static IReadOnlyDictionary<string, int> TallyByPreset(IEnumerable<BountyEntry> entries) =>
        entries.GroupBy(e => e.Preset).ToDictionary(g => g.Key, g => g.Sum(e => e.Points));
}
