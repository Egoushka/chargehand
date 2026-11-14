using System.Collections.Concurrent;

namespace Chargehand.Modules.Gateway.Api;

/// <summary>
/// Keeps one <see cref="RepositoryPack"/> per repository, rebuilt only when the commit changes (issue #435): a repository
/// whose checkout moves to a new commit gets a fresh pack; a request on the same commit gets the one already built. One
/// instance is shared by every run of an orchestrator's lifetime, so the first research or review of a commit pays the
/// build and later ones do not.
/// </summary>
public sealed class RepositoryPackCache
{
    private readonly ConcurrentDictionary<string, (string Commit, RepositoryPack Pack)> _entries = new(StringComparer.Ordinal);

    public RepositoryPack GetOrBuild(string repository, string directory, string commit)
    {
        if (_entries.TryGetValue(repository, out var entry) && entry.Commit == commit)
            return entry.Pack;
        var pack = RepositoryPack.Build(directory);
        _entries[repository] = (commit, pack);
        return pack;
    }
}
