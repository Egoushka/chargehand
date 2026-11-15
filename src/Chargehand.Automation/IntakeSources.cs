using System.Text.Json;

namespace Chargehand.Automation;

/// <summary>The on/off switch of each watched repository (CHARGEHAND-241), and the owner's review policy (ADR 0085: repositories left out of reviews, and a review budget above the preset's cap), kept beside intake's state in <c>intake/sources.json</c> and written whole through a temporary file. The profile says what
/// may be watched; this says what is watched now. A repository is on until it is switched off. Off, intake reads nothing of it and starts nothing for it: its labels and comments wait, and are read
/// when it is switched on again. Batches already running still report. Intake's poller and the server's <c>PUT /v1/intake/sources</c> share one instance, so every read and write takes its lock.</summary>
public sealed class IntakeSources
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    private readonly string? _path;
    private readonly HashSet<string> _off = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reviewsOff = new(StringComparer.OrdinalIgnoreCase);
    private decimal? _reviewBudget;
    private Limits _limits = new();
    private SweepOverrides _sweep = new();
    private readonly Lock _lock = new();

    public IntakeSources()
    {
    }

    private IntakeSources(string path, Stored stored)
    {
        _path = path;
        _off.UnionWith(stored.Off ?? []);
        _reviewsOff.UnionWith(stored.ReviewsOff ?? []);
        _reviewBudget = stored.ReviewBudgetUsd;
        _limits = stored.Limits ?? new Limits();
        _sweep = stored.Sweep ?? new SweepOverrides();
    }

    private sealed record Stored(List<string>? Off, List<string>? ReviewsOff = null, decimal? ReviewBudgetUsd = null, Limits? Limits = null, SweepOverrides? Sweep = null);

    /// <summary>The owner's overrides of the profile's intake caps (null: the profile's own), and a switch that holds every new start.</summary>
    public sealed record Limits(int? MaxConcurrent = null, int? MaxReviewRounds = null, decimal? MaxUsdTotal = null, bool Paused = false);

    /// <summary>The owner's overrides of the sweep (null: the profile's own). <paramref name="Repositories"/> maps a repository to its sweep switch; one not listed follows the profile (on).
    /// <paramref name="HoldPercent"/> is <c>subscription_hold_percent</c>, read when a batch's gateway is built.</summary>
    public sealed record SweepOverrides(bool? Enabled = null, List<string>? At = null, List<DayOfWeek>? Days = null, Dictionary<string, bool>? Repositories = null, decimal? HoldPercent = null);

    public SweepOverrides Sweep
    {
        get
        {
            lock (_lock)
                return _sweep;
        }
    }

    /// <returns>Whether anything changed.</returns>
    public bool SetSweep(SweepOverrides sweep)
    {
        lock (_lock)
        {
            if (SameSweep(_sweep, sweep))
                return false;
            var previous = _sweep;
            _sweep = sweep;
            try
            {
                Save();
            }
            catch
            {
                // A value that did not reach the file is not in force either: the console says the save failed.
                _sweep = previous;
                throw;
            }
            return true;
        }
    }

    public bool SweepOn(string repository) => Sweep.Repositories?.GetValueOrDefault(repository, true) ?? true;

    private static bool SameSweep(SweepOverrides a, SweepOverrides b) =>
        a.Enabled == b.Enabled && a.HoldPercent == b.HoldPercent && (a.At ?? []).SequenceEqual(b.At ?? []) && (a.Days ?? []).SequenceEqual(b.Days ?? []) && (a.At is null) == (b.At is null) && (a.Days is null) == (b.Days is null)
        && (a.Repositories ?? []).OrderBy(r => r.Key).SequenceEqual((b.Repositories ?? []).OrderBy(r => r.Key));

    public Limits Current
    {
        get
        {
            lock (_lock)
                return _limits;
        }
    }

    public int? MaxConcurrent => Current.MaxConcurrent;

    public int? MaxReviewRounds => Current.MaxReviewRounds;

    public decimal? MaxUsdTotal => Current.MaxUsdTotal;

    public bool Paused => Current.Paused;

    /// <returns>Whether anything changed.</returns>
    public bool SetLimits(Limits limits)
    {
        lock (_lock)
        {
            if (_limits == limits)
                return false;
            _limits = limits;
            Save();
            return true;
        }
    }

    public static IntakeSources Load(string path) =>
        new(path, File.Exists(path) ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json) ?? new Stored([]) : new Stored([]));

    /// <summary>Repositories the owner left out of reviews, in order.</summary>
    public IReadOnlyList<string> ReviewsOff
    {
        get
        {
            lock (_lock)
                return [.. _reviewsOff.Order(StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>The dollar budget the owner set for one review, lifting the preset's cap; null: the preset's own.</summary>
    public decimal? ReviewBudgetUsd
    {
        get
        {
            lock (_lock)
                return _reviewBudget;
        }
    }

    public bool ReviewsOn(string repository)
    {
        lock (_lock)
            return !_reviewsOff.Contains(repository);
    }

    /// <returns>Whether the policy changed.</returns>
    public bool SetReviews(string repository, bool on)
    {
        lock (_lock)
        {
            var changed = on ? _reviewsOff.Remove(repository) : _reviewsOff.Add(repository);
            if (changed)
                Save();
            return changed;
        }
    }

    /// <returns>Whether the budget changed.</returns>
    public bool SetReviewBudget(decimal? usd)
    {
        lock (_lock)
        {
            if (_reviewBudget == usd)
                return false;
            _reviewBudget = usd;
            Save();
            return true;
        }
    }

    public bool IsOn(string repository)
    {
        lock (_lock)
            return !_off.Contains(repository);
    }

    /// <returns>Whether the switch changed.</returns>
    public bool Set(string repository, bool on)
    {
        lock (_lock)
        {
            var changed = on ? _off.Remove(repository) : _off.Add(repository);
            if (changed)
                Save();
            return changed;
        }
    }

    /// <summary>Written whole through a temporary file; the caller holds the lock.</summary>
    private void Save()
    {
        if (_path is null)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temp = $"{_path}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Stored([.. _off.Order(StringComparer.OrdinalIgnoreCase)], [.. _reviewsOff.Order(StringComparer.OrdinalIgnoreCase)], _reviewBudget, _limits, _sweep), Json));
        File.Move(temp, _path, overwrite: true);
    }
}
