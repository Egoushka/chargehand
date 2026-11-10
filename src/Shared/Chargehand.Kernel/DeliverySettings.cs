using Chargehand.Contracts;

namespace Chargehand.Kernel;

/// <summary>ADR 0069. Repositories under these directories are always delivered locally: nothing is pushed, no pull request is opened, no forge is called, and a request that asks to push is refused.</summary>
/// <param name="LocalOnly">Directories, absolute or starting with <c>~/</c>; <c>/</c> makes every repository on the server local-only.</param>
public sealed record DeliverySettings(IReadOnlyList<string>? LocalOnly = null)
{
    /// <summary>The policy these roots make; <see cref="DeliveryPolicy.None"/> with no roots.</summary>
    public DeliveryPolicy Policy => new(LocalOnly);

    /// <summary>A root must be absolute or start with <c>~/</c>, and no <paramref name="watched"/> checkout (a profile key, the repository's name and its path, as the
    /// intake's <c>automation.github.repositories</c> lists them) may sit under one: intake reads the forge and starts pushing batches.</summary>
    public void Validate(IEnumerable<(string Section, string Name, string Path)> watched)
    {
        var roots = LocalOnly ?? [];
        if (roots.FirstOrDefault(r => string.IsNullOrWhiteSpace(r) || !(Path.IsPathRooted(r) || r.StartsWith("~/", StringComparison.Ordinal))) is { } bad)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"delivery.local_only: '{bad}' must be an absolute directory or start with ~/",
                "Fix delivery.local_only in the profile; docs/guide/reference.md lists the fields.");
        var policy = new DeliveryPolicy(roots);
        foreach (var (section, name, path) in watched)
            if (policy.Forced(path))
                throw new ChargehandException(ErrorCode.InvalidRequest, $"{section}.{name}.path is under delivery.local_only: intake reads a forge and starts batches that push",
                    $"Remove {name} from {section}, or take its checkout out of delivery.local_only.");
    }
}
