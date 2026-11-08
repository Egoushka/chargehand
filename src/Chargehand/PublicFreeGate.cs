using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Runtime;
using Chargehand.Workspace;

namespace Chargehand;

/// <summary>The one place a run is matched to the free-model pool (ADR 0093). The server asks it at admission to move a run to the pool preset; the orchestrator asks it again before it hands
/// out the pool's runtime, so the key reaches a repository only through the same fail-closed answer. The repository is identified by the server's own mapping of its checkouts, never by the
/// caller's text.</summary>
/// <param name="poolRuntime">The runtime over the pool's key and gateway, made on first use; it is the claude_code runtime without a fallback.</param>
public sealed class PublicFreeGate(Profile profile, PublicFreePool pool, Func<IWorkerRuntime> poolRuntime)
{
    private readonly Lazy<IWorkerRuntime> _runtime = new(poolRuntime, LazyThreadSafetyMode.PublicationOnly);

    public PublicFreePool Pool => pool;

    public string Preset => pool.Settings.Preset;

    /// <summary>The model a pool run's intake uses; never the profile's <c>intake_model</c>, which is the owner's usual model.</summary>
    public string? IntakeModel => pool.Settings.IntakeModel;

    /// <summary>Whether <paramref name="worker"/> is the pool's runtime, so the run it carries uses the pool's intake model.</summary>
    public bool Owns(IWorkerRuntime worker) => _runtime.IsValueCreated && ReferenceEquals(worker, _runtime.Value);

    public async Task<PoolDecision> DecideAsync(RunRequest request, CancellationToken ct)
    {
        var (forge, path) = await IdentifyAsync(request.Context.Repository?.Path, ct);
        return await pool.DecideAsync(request.Context.Preset, forge, path, request.Driven is not null, ct);
    }

    /// <summary>The pool's runtime for a run of <paramref name="preset"/> on <paramref name="given"/>; null when the preset is not the pool's. Throws <see cref="ChargehandException"/>
    /// (<c>repository_not_allowed</c>) for the pool preset on a repository that does not qualify.</summary>
    public async Task<IWorkerRuntime?> RuntimeAsync(string preset, string? given, CancellationToken ct)
    {
        if (!string.Equals(preset, Preset, StringComparison.Ordinal))
            return null;
        var (forge, path) = await IdentifyAsync(given, ct);
        var decision = await pool.DecideAsync(preset, forge, path, driven: false, ct);
        return decision.Choice == PoolChoice.Pool
            ? _runtime.Value
            : throw new ChargehandException(ErrorCode.RepositoryNotAllowed, decision.Reason ?? "the free-model pool is not available for this repository",
                "Run it on another preset; the pool is for repositories GitHub reports as public.");
    }

    private Task<(string? Forge, string? Path)> IdentifyAsync(string? given, CancellationToken ct) => RepositoryIdentity.IdentifyAsync(profile, given, ct);
}

/// <summary>The forge name and path of a known checkout, by the server's own mapping, never by the caller's text (ADR 0093). Shared by the free-model pool and the data-class gate of the budget router (ADR 0095).</summary>
public static class RepositoryIdentity
{
    /// <summary>The forge name and path of the known checkout <paramref name="given"/> names: a path of a known checkout, or a name <see cref="RepositoryNames"/> resolves. The forge name is kept only when
    /// the checkout's own <c>origin</c> names the same repository; otherwise it is null (fail closed).</summary>
    public static async Task<(string? Forge, string? Path)> IdentifyAsync(Profile profile, string? given, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(given))
            return (null, null);
        var known = RepositoryNames.List(profile);
        var direct = Path.IsPathRooted(given) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(given)) : null;
        var match = known.FirstOrDefault(k => direct is not null && string.Equals(k.Path, direct, StringComparison.Ordinal))
            ?? ((await RepositoryNames.ResolveAsync(given, known, ct)).Path is { } resolved ? known.FirstOrDefault(k => k.Path == resolved) : null);
        if (match?.Forge is not { } forge)
            return (null, match?.Path);
        // The mapping is a claim: a private clone in a folder named like a public repository, or a renamed repository whose old name is public now, must not borrow its answer.
        var origin = await RepositoryNames.OriginForgeAsync(match.Path, ct);
        return (string.Equals(origin, forge, StringComparison.OrdinalIgnoreCase) ? forge : null, match.Path);
    }
}
