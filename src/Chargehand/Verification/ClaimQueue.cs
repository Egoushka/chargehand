using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chargehand.Verification;

public enum ClaimState { Open, Disproved, Stands, Stale }

/// <summary>One claim of <c>claims.jsonl</c>. <c>Source</c> is <c>result:&lt;run id&gt;</c> or <c>doc:&lt;path&gt;:&lt;line&gt;</c>; <c>Preset</c> is the preset that wrote it, so a worker never disproves its own claim.</summary>
public sealed record BountyClaim(string Id, string Text, string Source, string Commit, ClaimState State, string? Preset = null, DateTimeOffset? LeaseUntil = null)
{
    /// <summary>The claim's id: sha256 of its text and source, so the same claim from the same place is one claim.</summary>
    public static string IdOf(string text, string source) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{text}\n{source}")));
}

/// <summary>
/// The queue of claims for workers to disprove (spec 2026-11-11-claim-bounty): a JSONL file next to the run log, append-only, the latest line of an id
/// winning, as <c>JsonlLearningStore</c> reads <c>learning.jsonl</c>. One open claim is out at a time: it is leased, and returns to open when the lease expires.
/// </summary>
public sealed class ClaimQueue(string path, TimeProvider? time = null)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<BountyClaim> AddAsync(string text, string source, string commit, string? preset, CancellationToken ct)
    {
        var claim = new BountyClaim(BountyClaim.IdOf(text, source), text, source, commit, ClaimState.Open, preset);
        await Gate.WaitAsync(ct);
        try
        {
            if (Latest().TryGetValue(claim.Id, out var held))
                return held;
            Write(claim);
            return claim;
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<IReadOnlyList<BountyClaim>> ReadAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return [.. Latest().Values];
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The next open claim, now leased; null when none is open or another claim's lease is still running.</summary>
    public async Task<BountyClaim?> LeaseNextAsync(TimeSpan lease, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();
            var open = Latest().Values.Where(c => c.State == ClaimState.Open).ToList();
            if (open.Any(c => c.LeaseUntil > now) || open.FirstOrDefault() is not { } next)
                return null;
            var leased = next with { LeaseUntil = now + lease };
            Write(leased);
            return leased;
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task SetStateAsync(string id, ClaimState state, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (Latest().TryGetValue(id, out var held))
                Write(held with { State = state, LeaseUntil = null });
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Marks every open claim whose commit <paramref name="isAncestor"/> rejects as stale; returns how many.</summary>
    public async Task<int> MarkStaleAsync(Func<string, Task<bool>> isAncestor, CancellationToken ct)
    {
        var stale = 0;
        foreach (var claim in (await ReadAsync(ct)).Where(c => c.State == ClaimState.Open))
            if (!await isAncestor(claim.Commit))
            {
                await SetStateAsync(claim.Id, ClaimState.Stale, ct);
                stale++;
            }
        return stale;
    }

    /// <summary>Whether <paramref name="commit"/> is an ancestor of <paramref name="branch"/> in the repository at <paramref name="repo"/>.</summary>
    public static async Task<bool> IsAncestorAsync(string repo, string commit, string branch, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "merge-base", "--is-ancestor", commit, branch })
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0;
    }

    private Dictionary<string, BountyClaim> Latest()
    {
        Dictionary<string, BountyClaim> claims = [];
        if (!File.Exists(path))
            return claims;
        var text = File.ReadAllText(path);
        foreach (var line in text[..(text.LastIndexOf('\n') + 1)].Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (Parse(line) is { } claim)
                claims[claim.Id] = claim;
        return claims;
    }

    private static BountyClaim? Parse(string line)
    {
        try
        {
            var o = JsonNode.Parse(line)!.AsObject();
            return new BountyClaim((string)o["id"]!, (string)o["text"]!, (string)o["source"]!, (string)o["commit"]!,
                Enum.Parse<ClaimState>((string)o["state"]!, ignoreCase: true), (string?)o["preset"], (DateTimeOffset?)o["lease_until"]);
        }
        catch (Exception e) when (e is JsonException or InvalidCastException or NullReferenceException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    private void Write(BountyClaim c)
    {
        var o = new JsonObject { ["id"] = c.Id, ["text"] = c.Text, ["source"] = c.Source, ["commit"] = c.Commit, ["state"] = c.State.ToString().ToLowerInvariant() };
        if (c.Preset is not null)
            o["preset"] = c.Preset;
        if (c.LeaseUntil is { } lease)
            o["lease_until"] = lease;
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);
        File.AppendAllText(path, o.ToJsonString() + "\n", new UTF8Encoding(false));
    }
}
