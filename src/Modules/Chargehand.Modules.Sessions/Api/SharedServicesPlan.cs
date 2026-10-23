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
/// <param name="Home">The repository's home volume (<see cref="HomeVolumes.Name"/>) when the profile says <c>driven.environment: "mise"</c> (ADR 0078); null: the tmpfs. <paramref name="Caches"/> is empty then.</param>
public sealed record SharedServicesPlan(IReadOnlyList<string> Caches, IReadOnlyList<ServiceSpec> Services, IReadOnlyList<string> Ecosystems, IReadOnlyList<string>? Verify, string? VerifySource,
    IReadOnlyDictionary<string, string>? Notes = null, string? Home = null)
{
    /// <summary>The same repository is the same key whichever way its remote is spelled, so the cache outlives a change from ssh to https.</summary>
    public static string Identity(string remoteUrl, string fallback) =>
        RemoteRepository.Parse(remoteUrl) is { } remote ? $"{remote.Owner}/{remote.Name}" : fallback;

    /// <param name="requestedVerify">The request's <c>context.verify</c>.</param>
    /// <param name="identity">See <see cref="Identity"/>.</param>
    /// <param name="network">The batch network the services join.</param>
    /// <param name="mise">The profile says <c>driven.environment: "mise"</c>: the repository's home volume replaces the ecosystem caches (<c>driven.services.caches</c> is ignored).</param>
    public static SharedServicesPlan For(DrivenSharedServices? settings, string sourcePath, IReadOnlyList<string>? requestedVerify, string identity, string batchId, string network, bool mise = false)
    {
        var scan = Directory.Exists(sourcePath) ? RepositoryDetector.Scan(sourcePath) : new RepositoryScan([], []);
        var verify = Directory.Exists(sourcePath) ? VerifyCommand.Resolve(requestedVerify, sourcePath) : requestedVerify is { Count: > 0 } ? new VerifyPlan(requestedVerify, "request") : null;
        var caches = CachesOf(settings, identity, mise);
        IReadOnlyList<ServiceSpec> services = [.. (settings?.ServicesOf(identity) ?? []).Select(s => new ServiceSpec(batchId, s.Name, s.Image, network, s.Port,
            s.Env ?? new Dictionary<string, string>(), s.Tmpfs ?? [], s.MemoryMb))];
        return new SharedServicesPlan(caches, services, [.. scan.Ecosystems.Select(e => e.Name)], verify?.Argv, verify?.Source,
            (settings?.ServicesOf(identity) ?? []).Where(s => s.Note is not null).ToDictionary(s => s.Name, s => s.Note!, StringComparer.Ordinal),
            mise ? HomeVolumes.Name(identity) : null);
    }

    private static IReadOnlyList<string> CachesOf(DrivenSharedServices? settings, string identity, bool mise) =>
        mise ? [] : [.. (settings?.Caches ?? []).Select(e => CacheVolumes.Name(e, identity))];

    /// <summary>The session image for a repository: the profile's image for the first ecosystem the detector found that has one (a Gradle repository gets the JDK image), else <paramref name="fallback"/>.</summary>
    public static string PickImage(DrivenSharedServices? settings, IEnumerable<string> ecosystems, string fallback) =>
        ecosystems.Select(e => settings?.Images?.GetValueOrDefault(e)).FirstOrDefault(image => image is not null) ?? fallback;

    /// <summary>The environment a container needs for the caches and the services: where each package manager keeps its cache and where the services are.</summary>
    public IReadOnlyDictionary<string, string> Environment()
    {
        Dictionary<string, string> env = new(CacheVolumes.Environment(Caches), StringComparer.Ordinal);
        if (Services.Count > 0)
            env[ContainerTemplate.ServicesVariable] = BatchServices.Addresses(Services);
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

    /// <param name="tools">What the install helper listed (<see cref="MiseInstallResult.Tools"/>); null: no helper ran.</param>
    public SessionEnvironment ForSession(IReadOnlyList<string>? tools = null) => new(Ecosystems, Verify, VerifySource, [.. Caches.Select(CacheVolumes.EcosystemOf).OfType<string>()],
        [.. Services.Select(s => new EnvironmentService(s.Name, $"{s.Name}:{s.Port}", Notes?.GetValueOrDefault(s.Name)))], tools);
}
