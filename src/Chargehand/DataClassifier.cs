using Chargehand.Config;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Economics.Api;

namespace Chargehand;

/// <summary>The data class of a run (ADR 0095), from the repository it works on, identified the way the free-model pool does it (ADR 0093): by the server's own mapping of its checkouts, never by the caller's text.
/// A checkout under a restricted directory (<c>work</c>) is <see cref="DataClass.Restricted"/>; a repository GitHub reports as public is <see cref="DataClass.Public"/>; one the profile's <c>owner_private_repos</c> names
/// is <see cref="DataClass.OwnerPrivate"/>; everything else, a missing repository and a failed lookup included, is <see cref="DataClass.Restricted"/>.</summary>
/// <param name="visibility">Reads GitHub's answer; null without a <c>public_free</c> block, and then no repository is known to be public.</param>
public sealed class DataClassifier(Profile profile, PublicFreePool? visibility) : IDataClassifier
{
    private const string PeerClonePrefix = "gh:";

    public async Task<DataClass> ClassifyAsync(string? repositoryPath, CancellationToken ct)
    {
        if (profile.Pools is not { } pools || string.IsNullOrWhiteSpace(repositoryPath))
            return DataClass.Restricted;
        // A peer clone of another person's repository: public only if GitHub says so.
        if (repositoryPath.StartsWith(PeerClonePrefix, StringComparison.Ordinal))
            return visibility is not null && await visibility.IsPublicAsync(repositoryPath[PeerClonePrefix.Length..], ct) ? DataClass.Public : DataClass.Restricted;
        var (forge, path) = await RepositoryIdentity.IdentifyAsync(profile, repositoryPath, ct);
        if (forge is null || path is null || Denied(path, pools))
            return DataClass.Restricted;
        if (visibility is not null && await visibility.IsPublicAsync(forge, ct))
            return DataClass.Public;
        return pools.NamesOwnerPrivate(forge) ? DataClass.OwnerPrivate : DataClass.Restricted;
    }

    private static bool Denied(string path, PoolsSettings pools) =>
        path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(segment => pools.RestrictedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));
}
