using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;

namespace Chargehand.Server;

/// <summary>One run (or automated start) held for the owner because its priced spend passes the threshold (ADR 0092).</summary>
/// <param name="Fingerprint">What the same request sent again hashes to: the approval is for this request, from this actor.</param>
/// <param name="Usd">The figure that passed the threshold: the requested budget, or a forecast.</param>
/// <param name="Basis"><see cref="RunApprovalGate.BudgetBasis"/> or <see cref="RunApprovalGate.ForecastBasis"/>.</param>
/// <param name="State"><see cref="RunApprovalGate.StatePending"/>, <see cref="RunApprovalGate.StateApproved"/> or <see cref="RunApprovalGate.StateDeclined"/>.</param>
public sealed record RunApproval(string Id, string Fingerprint, string? Actor, string? Repository, string Text, decimal Usd, string Basis, DateTimeOffset At, string State = RunApprovalGate.StatePending,
    DateTimeOffset? DecidedAt = null);

/// <summary>The per-run approval threshold and the requests held for it (ADR 0092), kept in <c>limits/run-approval.json</c> beside the run log and written whole through a temporary file. A request
/// above the threshold is held until the owner approves it; one approval pays for one start of the same request.</summary>
public sealed class RunApprovalGate
{
    public const decimal DefaultUsd = 5m;

    public const string BudgetBasis = "budget";
    public const string ForecastBasis = "forecast";

    public const string StatePending = "pending";
    public const string StateApproved = "approved";
    public const string StateDeclined = "declined";

    /// <summary>How long a decision stays good before the request has to be asked about again. A starting value.</summary>
    public static readonly TimeSpan DecisionLasts = TimeSpan.FromHours(24);

    public enum Verdict { Start, Held, Declined }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    private readonly string? _path;
    private readonly Lock _lock = new();
    private readonly List<RunApproval> _requests = [];
    private decimal? _limit = DefaultUsd;

    public RunApprovalGate(string? path)
    {
        _path = path;
        Limit = new Threshold(this);
        if (path is not null && File.Exists(path) && JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json) is { } stored)
        {
            _limit = stored.LimitUsd;
            _requests.AddRange(stored.Requests ?? []);
        }
    }

    private sealed record Stored(decimal? LimitUsd, List<RunApproval>? Requests);

    /// <summary>The threshold alone: <c>null</c> never asks.</summary>
    public Threshold Limit { get; }

    public sealed class Threshold(RunApprovalGate gate)
    {
        public decimal? LimitUsd
        {
            get
            {
                lock (gate._lock)
                    return gate._limit;
            }
        }

        public void Set(decimal? usd)
        {
            lock (gate._lock)
            {
                gate._limit = usd;
                gate.Persist();
            }
        }
    }

    /// <summary>The same request from the same actor hashes the same, whatever commit the repository is at and however an account's budget was cut.</summary>
    public static string Fingerprint(string? actor, RunRequest request)
    {
        var pinned = request with { Context = request.Context with { Repository = request.Context.Repository is { } repository ? repository with { Commit = "" } : null } };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{actor}\n{JsonSerializer.Serialize(pinned, ContractJson.Options)}")));
    }

    /// <summary>What the gate says about a request over the threshold: start it (an approval is used up), hold it (asked, or still waiting), or refuse it (the owner declined; the refusal is used up).</summary>
    public (RunApproval Request, Verdict Verdict) Check(string fingerprint, string? actor, string? repository, string text, decimal usd, string basis, DateTimeOffset now)
    {
        lock (_lock)
        {
            _requests.RemoveAll(r => r.State != StatePending && now - (r.DecidedAt ?? r.At) > DecisionLasts);
            var known = _requests.FirstOrDefault(r => r.Fingerprint == fingerprint);
            if (known is { State: StateApproved or StateDeclined })
            {
                _requests.Remove(known);
                TryPersist();
                return (known, known.State == StateApproved ? Verdict.Start : Verdict.Declined);
            }
            if (known is not null)
                return (known, Verdict.Held);
            var asked = new RunApproval(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)), fingerprint, actor, repository, text, usd, basis, now);
            _requests.Add(asked);
            TryPersist();
            return (asked, Verdict.Held);
        }
    }

    /// <summary>The requests waiting for the owner, newest first.</summary>
    public IReadOnlyList<RunApproval> Pending()
    {
        lock (_lock)
            return [.. _requests.Where(r => r.State == StatePending).OrderByDescending(r => r.At)];
    }

    /// <summary>Records the owner's decision on a waiting request; null when no request waits under <paramref name="id"/>.</summary>
    public RunApproval? Decide(string id, bool approve, DateTimeOffset? now = null)
    {
        lock (_lock)
        {
            var index = _requests.FindIndex(r => r.Id == id && r.State == StatePending);
            if (index < 0)
                return null;
            var decided = _requests[index] with { State = approve ? StateApproved : StateDeclined, DecidedAt = now ?? DateTimeOffset.UtcNow };
            _requests[index] = decided;
            TryPersist();
            return decided;
        }
    }

    private void TryPersist()
    {
        try
        {
            Persist();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The gate keeps working from memory; only a restart forgets what it held.
            Console.Error.WriteLine($"chargehand serve: could not write the run approvals: {e.Message}");
        }
    }

    private void Persist()
    {
        if (_path is null)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temp = $"{_path}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Stored(_limit, _requests), Json));
        File.Move(temp, _path, overwrite: true);
    }
}
