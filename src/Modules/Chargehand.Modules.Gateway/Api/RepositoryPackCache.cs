using System.Collections.Concurrent;

namespace Chargehand.Modules.Gateway.Api;

/// <summary>
/// Keeps one <see cref="RepositoryPack"/> per repository and commit (issue #435), rebuilt only when the commit changes: a
/// repository whose checkout moves to a new commit gets a fresh pack; a request on a commit already seen gets the one
/// already built, even while another run of the same repository sits on a different commit (a review's diff is applied
/// in its own clone, committed under a commit hash of its own). One instance is shared by every run of an orchestrator's
/// lifetime, so the first research or review of a commit pays the build and later ones do not.
/// </summary>
public sealed class RepositoryPackCache
{
    private readonly ConcurrentDictionary<(string Repository, string Commit), RepositoryPack> _entries = new();

    public RepositoryPack GetOrBuild(string repository, string directory, string commit) =>
        _entries.GetOrAdd((repository, commit), _ => RepositoryPack.Build(directory));
}
