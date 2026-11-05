using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Chargehand.Modules.Accounts.Api;

public enum PoolChoice { Standard, Pool, Refused }

/// <summary>What the pool says about a request: run it as asked (<see cref="PoolChoice.Standard"/>), run it on <see cref="Preset"/> (<see cref="PoolChoice.Pool"/>), or refuse it with <see cref="Code"/>.</summary>
public sealed record PoolDecision(PoolChoice Choice, string? Preset = null, string? Code = null, string? Reason = null)
{
    public const string RefusedCode = "public_free_refused";

    public static readonly PoolDecision Standard = new(PoolChoice.Standard);
}

/// <summary>Which runs may use the free-model pool (ADR 0093). A repository qualifies only when the pool is switched on, the checkout has a forge name from the server's own mapping, no directory of its path is
/// denied (<c>work</c>), and GitHub says it is public. Every other outcome, an error included, leaves it out: there is no path into the pool except a public answer.</summary>
public sealed partial class PublicFreePool(PublicFreeSettings settings, IRepositoryVisibility visibility, PublicFreeSwitch enabled, TimeProvider clock)
{
    /// <summary>How long a public answer is remembered. A starting value; no other answer is remembered.</summary>
    public static readonly TimeSpan PublicAnswerLasts = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _public = new(StringComparer.OrdinalIgnoreCase);

    public PublicFreeSettings Settings => settings;

    /// <summary>The owner's switch; off by default.</summary>
    public PublicFreeSwitch Switch => enabled;

    public bool Enabled => enabled.Enabled;

    public static bool IsForgeName(string? name) => name is { Length: <= 140 } && ForgeName().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex ForgeName();

    /// <summary>Whether the run on this checkout may use the pool, now. Never throws on a lookup failure.</summary>
    public async Task<bool> QualifiesAsync(string? forge, string? checkoutPath, CancellationToken ct)
    {
        if (!enabled.Enabled || !IsForgeName(forge) || checkoutPath is not { Length: > 0 } || IsDenied(checkoutPath))
            return false;
        var now = clock.GetUtcNow();
        if (_public.TryGetValue(forge!, out var until) && until > now)
            return true;
        _public.TryRemove(forge!, out _);
        RepositoryVisibility seen;
        try
        {
            seen = await visibility.LookupAsync(forge!, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        if (seen != RepositoryVisibility.Public)
            return false;
        _public[forge!] = now + PublicAnswerLasts;
        return true;
    }

    /// <summary>The choice for a request on <paramref name="preset"/> over the checkout <paramref name="forge"/> names. A request naming the pool preset on anything that does not qualify is refused, not moved.</summary>
    public async Task<PoolDecision> DecideAsync(string preset, string? forge, string? checkoutPath, bool driven, CancellationToken ct)
    {
        var named = string.Equals(preset, settings.Preset, StringComparison.Ordinal);
        if (driven || !await QualifiesAsync(forge, checkoutPath, ct).ConfigureAwait(false))
            return named ? Refuse(driven) : PoolDecision.Standard;
        return named || settings.Replaces?.Contains(preset, StringComparer.Ordinal) == true ? new PoolDecision(PoolChoice.Pool, settings.Preset) : PoolDecision.Standard;
    }

    private PoolDecision Refuse(bool driven) => new(PoolChoice.Refused, null, PoolDecision.RefusedCode, driven
        ? $"preset '{settings.Preset}' is for single runs on public repositories; a driven batch never uses it"
        : $"preset '{settings.Preset}' runs only on a repository GitHub reports as public, with the pool switched on; this one is not, or could not be checked");

    private bool IsDenied(string path) =>
        path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(segment => settings.Denied.Contains(segment, StringComparer.OrdinalIgnoreCase));
}
