using Chargehand.Contracts;

namespace Chargehand.Kernel;

/// <summary>ADR 0069. Repositories under these directories are always delivered locally: nothing is pushed, no pull request is opened, no forge is called, and a request that asks to push is refused.</summary>
/// <param name="LocalOnly">Directories, absolute or starting with <c>~/</c>; <c>/</c> makes every repository on the server local-only.</param>
/// <param name="PushRemote">Opt-in (issue #569): a <c>code</c> run that is not local pushes its <c>chargehand/&lt;run&gt;/&lt;node&gt;</c> branch to the caller's repository's remote of this name,
/// never forced and never to another ref, with the credentials git already has for that remote. Unset: nothing is pushed.</param>
public sealed record DeliverySettings(IReadOnlyList<string>? LocalOnly = null, string? PushRemote = null)
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
        if (PushRemote is not null && !System.Text.RegularExpressions.Regex.IsMatch(PushRemote, "^[A-Za-z0-9][A-Za-z0-9._-]*$"))
            throw new ChargehandException(ErrorCode.InvalidRequest, $"delivery.push_remote: '{PushRemote}' must be the name of a git remote, such as origin",
                "Fix delivery.push_remote in the profile, or leave it out to push nothing.");
        var policy = new DeliveryPolicy(roots);
        foreach (var (section, name, path) in watched)
            if (policy.Forced(path))
                throw new ChargehandException(ErrorCode.InvalidRequest, $"{section}.{name}.path is under delivery.local_only: intake reads a forge and starts batches that push",
                    $"Remove {name} from {section}, or take its checkout out of delivery.local_only.");
    }

    /// <summary>A remote URL without its user info (<c>https://user:token@host/…</c> → <c>https://host/…</c>), so a token in it never reaches a result.</summary>
    public static string Redact(string url) =>
        System.Text.RegularExpressions.Regex.Replace(url, "^([A-Za-z][A-Za-z0-9+.-]*://)[^/@]*@", "$1");
}
