using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chargehand.Verification;

/// <summary>One line of <c>bounty.jsonl</c>: a point for the preset whose disproof was accepted. A human revokes with <c>Points</c> of -1.</summary>
public sealed record BountyEntry(string ClaimId, string RunId, string Preset, int Points, DateTimeOffset At);

/// <summary>Append-only; a later <c>stands</c> run never removes a point. The tally per preset is a suggestion: nothing reads it to change a route or an intake rank.</summary>
public sealed class BountyLedger(string path, TimeProvider? time = null)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<BountyEntry> AppendAsync(string claimId, string runId, string preset, int points, CancellationToken ct)
    {
        var entry = new BountyEntry(claimId, runId, preset, points, _time.GetUtcNow());
        var o = new JsonObject { ["claim_id"] = entry.ClaimId, ["run_id"] = entry.RunId, ["preset"] = entry.Preset, ["points"] = entry.Points, ["at"] = entry.At };
        await Gate.WaitAsync(ct);
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } dir)
                Directory.CreateDirectory(dir);
            await File.AppendAllTextAsync(path, o.ToJsonString() + "\n", new UTF8Encoding(false), ct);
        }
        finally
        {
            Gate.Release();
        }
        return entry;
    }

    public async Task<IReadOnlyList<BountyEntry>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(path))
            return [];
        var text = await File.ReadAllTextAsync(path, ct);
        var entries = new List<BountyEntry>();
        foreach (var line in text[..(text.LastIndexOf('\n') + 1)].Split('\n', StringSplitOptions.RemoveEmptyEntries))
            try
            {
                var o = JsonNode.Parse(line)!.AsObject();
                entries.Add(new BountyEntry((string)o["claim_id"]!, (string)o["run_id"]!, (string)o["preset"]!, (int)o["points"]!, (DateTimeOffset)o["at"]!));
            }
            catch (Exception e) when (e is JsonException or InvalidCastException or NullReferenceException or FormatException)
            {
                // A torn or foreign line is skipped, as the learning store skips one.
            }
        return entries;
    }

    public async Task<IReadOnlyDictionary<string, int>> TallyAsync(CancellationToken ct) =>
        (await ReadAsync(ct)).GroupBy(e => e.Preset).ToDictionary(g => g.Key, g => g.Sum(e => e.Points));
}
