namespace Chargehand.Modules.Runners.Api;

/// <summary>A runner that says which session images it will start (<c>GET /images</c>): its <c>--images</c>, so the server finds a drift between its <c>driven.images</c> and the
/// runner's allowlist before a task starts, not as a refused start inside the task.</summary>
public interface IImageAllowlistSource
{
    /// <returns>The images the runner starts, by digest; null when the runner is older than <c>GET /images</c>, read as "unknown" so a mismatch is never claimed from silence.</returns>
    Task<IReadOnlyList<string>?> ReadImagesAsync(CancellationToken ct);
}

/// <summary>The preflight of the session images against a runner's allowlist: what <see cref="RunnerPool"/> and <see cref="HostAdmission"/> check before they admit a task, and what
/// the System page shows.</summary>
public static class ImageAllowlist
{
    /// <summary>What a runner whose allowlist is <paramref name="allowed"/> is missing of <paramref name="wanted"/>, as one sentence; null when nothing is missing or there is nothing to
    /// compare (no images wanted, or an allowlist the runner did not report).</summary>
    public static string? Missing(IReadOnlyList<string>? wanted, IReadOnlyList<string>? allowed)
    {
        if (wanted is not { Count: > 0 } || allowed is null)
            return null;
        var missing = wanted.Where(i => !allowed.Contains(i, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        return missing.Count switch
        {
            0 => null,
            1 => $"the session image {missing[0]} is not on the runner's allowlist (--images); restart the runner with it, or change driven.images",
            _ => $"the session images {string.Join(", ", missing)} are not on the runner's allowlist (--images); restart the runner with them, or change driven.images",
        };
    }

    /// <summary>Reads the allowlist of <paramref name="source"/> when it can report one and compares it with <paramref name="wanted"/> (<see cref="Missing"/>). A source that is not a
    /// runner (the server's own engine) has nothing to check. A failed read is the caller's to judge.</summary>
    public static async Task<string?> CheckAsync(object? source, IReadOnlyList<string>? wanted, CancellationToken ct) =>
        wanted is { Count: > 0 } && source is IImageAllowlistSource runner ? Missing(wanted, await runner.ReadImagesAsync(ct)) : null;
}
