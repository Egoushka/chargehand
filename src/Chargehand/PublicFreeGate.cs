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
        var (forge, path) = Identify(request.Context.Repository?.Path);
        return await pool.DecideAsync(request.Context.Preset, forge, path, request.Driven is not null, ct);
    }

    /// <summary>The pool's runtime for a run of <paramref name="preset"/> on <paramref name="given"/>; null when the preset is not the pool's. Throws <see cref="ChargehandException"/>
    /// (<c>repository_not_allowed</c>) for the pool preset on a repository that does not qualify.</summary>
    public async Task<IWorkerRuntime?> RuntimeAsync(string preset, string? given, CancellationToken ct)
    {
        if (!string.Equals(preset, Preset, StringComparison.Ordinal))
            return null;
        var (forge, path) = Identify(given);
        var decision = await pool.DecideAsync(preset, forge, path, driven: false, ct);
        return decision.Choice == PoolChoice.Pool
            ? _runtime.Value
            : throw new ChargehandException(ErrorCode.RepositoryNotAllowed, decision.Reason ?? "the free-model pool is not available for this repository",
                "Run it on another preset; the pool is for repositories GitHub reports as public.");
    }

    /// <summary>The forge name and path of the known checkout <paramref name="given"/> names: a path of a known checkout, or a name <see cref="RepositoryNames"/> resolves. Null, null for any other.</summary>
    private (string? Forge, string? Path) Identify(string? given)
    {
        if (string.IsNullOrWhiteSpace(given))
            return (null, null);
        var known = RepositoryNames.List(profile);
        var direct = Path.IsPathRooted(given) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(given)) : null;
        var match = known.FirstOrDefault(k => direct is not null && string.Equals(k.Path, direct, StringComparison.Ordinal))
            ?? (RepositoryNames.Resolve(given, known) is { } resolved ? known.FirstOrDefault(k => k.Path == resolved) : null);
        return (match?.Forge, match?.Path);
    }
}
