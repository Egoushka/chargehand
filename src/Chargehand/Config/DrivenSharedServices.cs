using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Config;

/// <summary>ADR 0048: what a batch's containers share. Off unless a key is set; with none, every session and the verifier start as before, cold.</summary>
/// <param name="Caches">Ecosystems whose package cache is a named volume mounted into the sessions and the verifier of one repository: <c>nuget</c>, <c>npm</c>, <c>pip</c>, <c>gradle</c>, <c>maven</c>.</param>
/// <param name="Images">A session image by ecosystem (<c>gradle</c>, <c>maven</c>, ...): used when the repository has that ecosystem. Every value must also be listed in <c>driven.images</c>, which is what the runner allows.</param>
/// <param name="Repositories">Per <c>owner/repo</c>: the test services started on the batch network for the batch's lifetime.</param>
public sealed partial record DrivenSharedServices(IReadOnlyList<string>? Caches = null, IReadOnlyDictionary<string, string>? Images = null,
    IReadOnlyDictionary<string, IReadOnlyList<DrivenTestService>>? Repositories = null)
{
    /// <summary>The ecosystems a cache volume exists for; a cache is keyed by ecosystem and repository.</summary>
    public static readonly IReadOnlyList<string> CacheEcosystems = ["nuget", "npm", "pip", "gradle", "maven"];

    /// <summary>The services of <paramref name="repository"/> (<c>owner/repo</c>, compared without regard to case); none when it is not listed.</summary>
    public IReadOnlyList<DrivenTestService> ServicesOf(string repository) =>
        Repositories?.FirstOrDefault(r => string.Equals(r.Key, repository, StringComparison.OrdinalIgnoreCase)).Value ?? [];

    /// <summary>Throws an invalid_request error naming the key when a value cannot work.</summary>
    /// <param name="sessionImages"><c>driven.images</c>.</param>
    internal void Validate(IReadOnlyList<string>? sessionImages)
    {
        var problems = new List<string>();
        foreach (var cache in Caches ?? [])
            if (!CacheEcosystems.Contains(cache))
                problems.Add($"caches entry '{cache}' must be one of {string.Join(", ", CacheEcosystems)}");
        if ((Caches ?? []).Distinct().Count() != (Caches ?? []).Count)
            problems.Add("caches lists an ecosystem twice");
        foreach (var (ecosystem, image) in Images ?? new Dictionary<string, string>())
        {
            if (!Ecosystem().IsMatch(ecosystem))
                problems.Add($"images key '{ecosystem}' must be an ecosystem name such as gradle");
            if (!ImageReference().IsMatch(image))
                problems.Add($"images.{ecosystem} must be pinned by digest (name@sha256:<64 hex>)");
            else if (sessionImages?.Contains(image, StringComparer.Ordinal) != true)
                problems.Add($"images.{ecosystem} must also be listed in driven.images, which is what the runner allows");
        }
        foreach (var (repository, services) in Repositories ?? new Dictionary<string, IReadOnlyList<DrivenTestService>>())
        {
            if (!RepositoryName().IsMatch(repository))
                problems.Add($"repositories key '{repository}' must be owner/repo");
            if (services.Count > MaxServices)
                problems.Add($"repositories.{repository} has more than {MaxServices} services");
            if (services.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() != services.Count)
                problems.Add($"repositories.{repository} names a service twice");
            foreach (var service in services)
                problems.AddRange(service.Problems($"repositories.{repository}.{service.Name}"));
        }
        if (problems.Count > 0)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"driven.services: {string.Join("; ", problems)}", "Fix driven.services in the profile; docs/guide/driven.md lists the keys.");
    }

    public const int MaxServices = 4;

    [GeneratedRegex(@"^[a-z][a-z0-9]{0,23}$")]
    private static partial Regex Ecosystem();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryName();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._/-]*(:[0-9]+)?(/[a-z0-9._-]+)*@sha256:[0-9a-f]{64}$")]
    internal static partial Regex ImageReference();
}

/// <summary>A service a repository's tests need (a database, a queue), started from a pinned image on the batch's internal network and reachable from the session and the verifier by <paramref name="Name"/>.</summary>
/// <param name="Name">Lowercase letters and digits; the host name on the batch network.</param>
/// <param name="Image">By digest; the runner must list it in <c>--service-images</c>.</param>
/// <param name="Port">The port the service listens on; the session is told <c>name:port</c>.</param>
/// <param name="Env">The service's own environment (for example a database password). Test values for a container with no route out: never a real secret.</param>
/// <param name="Tmpfs">Directories of the image that are memory-backed (for a database, its data directory), so the service leaves no volume behind.</param>
/// <param name="Note">One line the session reads next to the address: how to connect, what the user and the database are called.</param>
/// <param name="MemoryMb">Default 512.</param>
public sealed partial record DrivenTestService(string Name, string Image, int Port, IReadOnlyDictionary<string, string>? Env = null, IReadOnlyList<string>? Tmpfs = null, string? Note = null,
    int MemoryMb = 512)
{
    internal IEnumerable<string> Problems(string path)
    {
        if (!ServiceName().IsMatch(Name))
            yield return $"{path}: name must be lowercase letters and digits, starting with a letter, at most 24";
        if (!DrivenSharedServices.ImageReference().IsMatch(Image))
            yield return $"{path}: image must be pinned by digest (name@sha256:<64 hex>)";
        if (Port is < 1 or > 65535)
            yield return $"{path}: port must be 1 to 65535";
        if (MemoryMb is < 64 or > 8192)
            yield return $"{path}: memory_mb must be 64 to 8192";
        foreach (var (name, value) in Env ?? new Dictionary<string, string>())
        {
            if (!EnvName().IsMatch(name) || name.StartsWith("LD_", StringComparison.Ordinal))
                yield return $"{path}: env name '{name}' must be upper case letters, digits and underscores, and not LD_*";
            if (value.Contains('\n') || value.Contains('\r') || value.Contains('\0'))
                yield return $"{path}: env value of '{name}' has a line break or NUL";
        }
        foreach (var directory in Tmpfs ?? [])
            if (!Directory().IsMatch(directory))
                yield return $"{path}: tmpfs entry '{directory}' must be an absolute path of plain segments";
        if (Note is { } note && (note.Length > 200 || note.Contains('\n') || note.Contains('\r')))
            yield return $"{path}: note must be one line of at most 200 characters";
    }

    [GeneratedRegex(@"^[a-z][a-z0-9]{0,23}$")]
    private static partial Regex ServiceName();

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{0,63}$")]
    private static partial Regex EnvName();

    [GeneratedRegex(@"^/(?!\.\.?(/|$))[A-Za-z0-9_.+@-]+(/(?!\.\.?(/|$))[A-Za-z0-9_.+@-]+)*$")]
    private static partial Regex Directory();
}
