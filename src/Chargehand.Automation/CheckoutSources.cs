using Chargehand.Contracts;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Automation;

/// <summary>Where a batch or a review reads a repository from: the profile's checkout, fetched now. A checkout git cannot write (a read-only mount) is not fetched into: the repository's managed
/// clone is, cloned the first time, and later reads go to it directly. Without a clone, a batch starts from the commit the checkout has. <paramref name="source"/> prefixes the lines it writes.</summary>
internal sealed class CheckoutSources(IRepositoryHeads heads, ManagedClones? clones, string source, Action<string> warn, Action<string> info)
{
    private readonly Dictionary<string, string> _cloned = new(StringComparer.Ordinal);

    /// <summary>The checkout and commit a batch starts from. <paramref name="name"/> is the repository's <c>owner/repo</c>; null takes it from the checkout's origin.</summary>
    /// <exception cref="IntakeException">The checkout could not be fetched and has no default branch.</exception>
    public async Task<RepositoryRef> CurrentAsync(string? name, string path, CancellationToken ct)
    {
        var from = Taken(path);
        try
        {
            return new RepositoryRef(from, await heads.CurrentCommitAsync(from, ct));
        }
        catch (ReadOnlyCheckoutException e)
        {
            string? why = null;
            if (clones is not null)
                try
                {
                    var clone = await CloneAsync(name, path, e, ct);
                    return new RepositoryRef(clone, await heads.CurrentCommitAsync(clone, ct));
                }
                catch (IntakeException failed) when (e.Stale is not null)
                {
                    why = failed.Message;
                }
            if (e.Stale is not { } stale)
                throw new IntakeException($"{path} cannot be fetched and has no origin/HEAD: {e.Message}");
            warn($"{source}: {name ?? Path.GetFileName(path)}: {path} cannot be written and {why ?? "intake has no clones"}: the batch starts from the commit the checkout has, {stale}");
            return new RepositoryRef(path, stale);
        }
    }

    /// <summary>The checkout and commit of pull request <paramref name="number"/>'s head. A read-only checkout whose host job already fetched <paramref name="expected"/> (the head GitHub lists) is used as it is;
    /// otherwise the head is fetched into the managed clone.</summary>
    /// <exception cref="IntakeException">The head could not be fetched.</exception>
    public async Task<RepositoryRef> PullHeadAsync(string? name, string path, int number, string expected, CancellationToken ct)
    {
        var from = Taken(path);
        try
        {
            return new RepositoryRef(from, await heads.FetchPullHeadAsync(from, number, ct));
        }
        catch (ReadOnlyCheckoutException e) when (clones is not null)
        {
            if (e.Stale == expected)
                return new RepositoryRef(path, expected);
            var clone = await CloneAsync(name, path, e, ct);
            return new RepositoryRef(clone, await heads.FetchPullHeadAsync(clone, number, ct));
        }
    }

    private string Taken(string path) => _cloned.GetValueOrDefault(path, path);

    private async Task<string> CloneAsync(string? name, string path, ReadOnlyCheckoutException e, CancellationToken ct)
    {
        name ??= RemoteRepository.Parse(e.OriginUrl) is { } remote ? $"{remote.Owner}/{remote.Name}" : throw new IntakeException($"{path}: its origin is not a repository intake can clone");
        var clone = await clones!.EnsureAsync(name, e.OriginUrl, ct);
        _cloned[path] = clone;
        info($"{source}: {name}: {path} cannot be written, so intake fetches its own clone {clone} instead");
        return clone;
    }
}
