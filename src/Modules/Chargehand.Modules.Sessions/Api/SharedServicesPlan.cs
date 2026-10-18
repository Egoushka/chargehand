using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Gateway.Api;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Modules.Sessions.Api;

/// <summary>What ADR 0048 gives one batch's containers, worked out once from the profile and the repository: the cache volumes its sessions and verifier mount, the test services started on
/// the batch network, and the facts the environment note carries.</summary>
/// <param name="Caches">Cache volume names (<see cref="CacheVolumes.Name"/>), one per ecosystem the profile caches.</param>
/// <param name="Services">The repository's declared test services, on the batch's network.</param>
/// <param name="Ecosystems">The ecosystems found at the repository's top level, in the detector's order.</param>
/// <param name="Verify">The command the handover runs, and where it came from (<c>request</c> or <c>detected</c>); null when there is none.</param>
/// <param name="Notes">The profile's one-line note per service name.</param>
/// <param name="SessionEnv">The declared services' <c>session_env</c>, merged: set in the session container and in the verify run, next to <see cref="ContainerTemplate.ServicesVariable"/>.</param>
public sealed record SharedServicesPlan(IReadOnlyList<string> Caches, IReadOnlyList<ServiceSpec> Services, IReadOnlyList<string> Ecosystems, IReadOnlyList<string>? Verify, string? VerifySource,
    IReadOnlyDictionary<string, string>? Notes = null, IReadOnlyDictionary<string, string>? SessionEnv = null)
{
    /// <summary>The names of <see cref="SessionEnv"/>: what a session or verifier container may additionally set, beyond <see cref="ContainerTemplate.AllowedEnv"/>.</summary>
    public IReadOnlyList<string>? SessionEnvNames => SessionEnv is { Count: > 0 } ? [.. SessionEnv.Keys] : null;

    /// <summary>The same repository is the same key whichever way its remote is spelled, so the cache outlives a change from ssh to https.</summary>
    public static string Identity(string remoteUrl, string fallback) =>
        RemoteRepository.Parse(remoteUrl) is { } remote ? $"{remote.Owner}/{remote.Name}" : fallback;

    /// <param name="requestedVerify">The request's <c>context.verify</c>.</param>
    /// <param name="identity">See <see cref="Identity"/>.</param>
    /// <param name="network">The batch network the services join.</param>
    public static SharedServicesPlan For(DrivenSharedServices? settings, string sourcePath, IReadOnlyList<string>? requestedVerify, string identity, string batchId, string network)
    {
        var scan = Directory.Exists(sourcePath) ? RepositoryDetector.Scan(sourcePath) : new RepositoryScan([], []);
        var verify = Directory.Exists(sourcePath) ? VerifyCommand.Resolve(requestedVerify, sourcePath) : requestedVerify is { Count: > 0 } ? new VerifyPlan(requestedVerify, "request") : null;
        IReadOnlyList<string> caches = [.. (settings?.Caches ?? []).Select(e => CacheVolumes.Name(e, identity))];
        var declared = settings?.ServicesOf(identity) ?? [];
        IReadOnlyList<ServiceSpec> services = [.. declared.Select(s => new ServiceSpec(batchId, s.Name, s.Image, network, s.Port,
            s.Env ?? new Dictionary<string, string>(), s.Tmpfs ?? [], s.MemoryMb))];
        return new SharedServicesPlan(caches, services, [.. scan.Ecosystems.Select(e => e.Name)], verify?.Argv, verify?.Source,
            declared.Where(s => s.Note is not null).ToDictionary(s => s.Name, s => s.Note!, StringComparer.Ordinal), MergedSessionEnv(declared));
    }

    /// <summary>The repository's declared services' <c>session_env</c>, merged into one dictionary; null when none set any.</summary>
    private static Dictionary<string, string>? MergedSessionEnv(IReadOnlyList<DrivenTestService> declared)
    {
        Dictionary<string, string> sessionEnv = new(StringComparer.Ordinal);
        foreach (var service in declared)
            foreach (var (name, value) in service.SessionEnv ?? new Dictionary<string, string>())
                sessionEnv[name] = value;
        return sessionEnv.Count > 0 ? sessionEnv : null;
    }

    /// <summary>The session image for a repository: the profile's image for the first ecosystem the detector found that has one (a Gradle repository gets the JDK image), else <paramref name="fallback"/>.</summary>
    public static string PickImage(DrivenSharedServices? settings, IEnumerable<string> ecosystems, string fallback) =>
        ecosystems.Select(e => settings?.Images?.GetValueOrDefault(e)).FirstOrDefault(image => image is not null) ?? fallback;

    /// <summary>The environment a container needs for the caches and the services: where each package manager keeps its cache and where the services are.</summary>
    public IReadOnlyDictionary<string, string> Environment()
    {
        Dictionary<string, string> env = new(CacheVolumes.Environment(Caches), StringComparer.Ordinal);
        if (Services.Count > 0)
            env[ContainerTemplate.ServicesVariable] = BatchServices.Addresses(Services);
        foreach (var (name, value) in SessionEnv ?? new Dictionary<string, string>())
            env[name] = value;
        return env;
    }

    /// <summary>Adds the services' names to <c>NO_PROXY</c> in <paramref name="env"/>: a service answers on the batch network, not through the egress proxy.</summary>
    public void BypassProxy(IDictionary<string, string> env)
    {
        if (Services.Count == 0)
            return;
        var hosts = string.Join(',', Services.Select(s => s.Name));
        env["NO_PROXY"] = env["no_proxy"] = env.TryGetValue("NO_PROXY", out var existing) && existing.Length > 0 ? $"{existing},{hosts}" : hosts;
    }

    public SessionEnvironment ForSession() => new(Ecosystems, Verify, VerifySource, [.. Caches.Select(CacheVolumes.EcosystemOf).OfType<string>()],
        [.. Services.Select(s => new EnvironmentService(s.Name, $"{s.Name}:{s.Port}", Notes?.GetValueOrDefault(s.Name)))]);
}
