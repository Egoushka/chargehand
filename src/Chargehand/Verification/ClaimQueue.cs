using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chargehand.Contracts;

namespace Chargehand.Verification;

/// <summary>Where a claim came from (spec decision 1): a finished run's result/v1, or a line of a doc. Doc intake is a later
/// task; this slice only carries the shape.</summary>
public enum ClaimSourceKind { Result, Doc }

public enum ClaimState { Open, Leased, Disproved, Stands, Stale }

/// <param name="Preset">The preset that wrote the claim, when known (a result/v1 claim, from the run's own request); null for a
/// doc claim, or when the importer did not record it. Used only to refuse a disprove run credit for its own preset's claim.</param>
public sealed record ClaimRecord(string Id, string Text, ClaimSourceKind SourceKind, string SourceRef, string Commit, ClaimState State,
    DateTimeOffset At, string? Preset = null, DateTimeOffset? LeaseExpiresAt = null)
{
    /// <summary>sha256 of the source and the text, hex lower (spec decision 1): the same claim re-imported from the same source keeps its id.</summary>
    public static string IdOf(ClaimSourceKind sourceKind, string sourceRef, string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceKind}\n{sourceRef}\n{text}")));
}

/// <summary>
/// claims.jsonl next to the run log, one JSON object per line, append-only — the way <c>JsonlLearningStore</c> writes
/// learning.jsonl (spec decision 2, no new store): a claim's state is its latest line, and a reader takes the whole file.
/// One open claim is handed out at a time; a handed-out claim reverts to open once its lease expires, so a worker that
/// never answers does not strand it.
/// </summary>
public sealed class ClaimQueue(string path, TimeProvider? clock = null)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Adds claims not already known by id; a claim already in the file is left as it is.</summary>
    public async Task SeedAsync(IEnumerable<ClaimRecord> claims, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var known = Parse(await Text(ct)).Keys;
            foreach (var claim in claims)
                if (!known.Contains(claim.Id))
                    await WriteLine(claim with { State = ClaimState.Open, At = _clock.GetUtcNow(), LeaseExpiresAt = null }, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The oldest open claim, or the oldest leased claim whose lease has expired; null when none is available. Marks it
    /// leased until <paramref name="duration"/> from now.</summary>
    public async Task<ClaimRecord?> LeaseAsync(TimeSpan duration, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var now = _clock.GetUtcNow();
            var claim = Parse(await Text(ct)).Values
                .Where(c => c.State == ClaimState.Open || (c.State == ClaimState.Leased && c.LeaseExpiresAt <= now))
                .OrderBy(c => c.At)
                .FirstOrDefault();
            if (claim is null)
                return null;
            var leased = claim with { State = ClaimState.Leased, At = now, LeaseExpiresAt = now + duration };
            await WriteLine(leased, ct);
            return leased;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Moves a claim to a terminal or reverted state (spec decision 2 and 4): disproved, stands, or back to open after a
    /// disproof that earned nothing.</summary>
    public async Task MarkAsync(string id, ClaimState state, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var claims = Parse(await Text(ct));
            if (!claims.TryGetValue(id, out var claim))
                throw new KeyNotFoundException($"no claim {id}");
            await WriteLine(claim with { State = state, At = _clock.GetUtcNow(), LeaseExpiresAt = null }, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<ClaimRecord?> FindAsync(string id, CancellationToken ct) => Parse(await Text(ct)).GetValueOrDefault(id);

    private async Task<string> Text(CancellationToken ct)
    {
        if (!File.Exists(path))
            return "";
        var text = await File.ReadAllTextAsync(path, ct);
        return text[..(text.LastIndexOf('\n') + 1)];
    }

    private static Dictionary<string, ClaimRecord> Parse(string text)
    {
        var claims = new Dictionary<string, ClaimRecord>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (JsonNode.Parse(line)!.AsObject().Deserialize<ClaimRecord>(ContractJson.Options) is { } claim)
                    claims[claim.Id] = claim;
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException)
            {
            }
        }
        return claims;
    }

    private async Task WriteLine(ClaimRecord claim, CancellationToken ct)
    {
        var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(claim, ContractJson.Options) + "\n");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        await file.WriteAsync(line, ct);
    }
}
