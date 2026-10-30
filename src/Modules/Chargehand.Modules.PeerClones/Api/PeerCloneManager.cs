using System.Diagnostics;
using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Modules.PeerClones.Api;

/// <summary>A shallow clone of a public GitHub repository, held until disposed. Disposing deletes it and frees the one slot
/// peer clones share (ADR 0045's admission control: one read at a time).</summary>
public interface IPeerCloneLease : IAsyncDisposable
{
    /// <summary>The clone's working tree, at the commit the caller asked for.</summary>
    string Directory { get; }
}

/// <summary>Resolves <c>gh:owner/name</c> into a shallow clone (<see cref="PeerCloneSettings"/>): a GraphQL size check, a free-space
/// check on the cache's filesystem, then <c>git clone --depth 1 --single-branch</c>, each refusal before anything is cloned so it costs
/// nothing. The caller deletes the clone (<see cref="IPeerCloneLease.DisposeAsync"/>) when the run ends, whatever the outcome.</summary>
public interface IPeerCloneManager
{
    /// <exception cref="ChargehandException">The slug is not <c>owner/name</c>, the repository is too large or not found, the cache's
    /// filesystem is too full, or the clone's default branch is not at <paramref name="commit"/>.</exception>
    Task<IPeerCloneLease> AcquireAsync(string slug, string commit, CancellationToken ct);
}

/// <param name="settings">The cache's root, size and free-space limits (ADR of this change).</param>
/// <param name="size">The GraphQL <c>diskUsage</c> check.</param>
/// <param name="remoteUrl">The URL <c>git clone</c> reads from for <c>owner/name</c> (a slug), with any credential already in it; a test
/// may point this at a local checkout instead of GitHub.</param>
/// <param name="freeBytesOf">Free space of the filesystem holding a directory; the real disk when null.</param>
public sealed class PeerCloneManager(PeerCloneSettings settings, IPeerRepositorySize size, Func<string, CancellationToken, Task<string>> remoteUrl,
    Func<string, long>? freeBytesOf = null) : IPeerCloneManager, IDisposable
{
    private static readonly Regex Slug = new("^(?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)/(?<name>[A-Za-z0-9._-]+)$", RegexOptions.Compiled);

    // One peer read at a time (ADR 0045's admission control applies the same way: a shared resource gated before it is touched).
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IPeerCloneLease> AcquireAsync(string slug, string commit, CancellationToken ct)
    {
        var match = Slug.Match(slug);
        if (!match.Success)
            throw new ChargehandException(ErrorCode.CheckoutInvalid, $"gh:{slug} is not owner/name", "Set context.repository.path to gh:owner/name.");
        var owner = match.Groups["owner"].Value;
        var name = match.Groups["name"].Value;
        await _gate.WaitAsync(ct);
        var acquired = false;
        try
        {
            var usage = await size.DiskUsageBytesAsync(owner, name, ct)
                ?? throw new ChargehandException(ErrorCode.RepositoryNotAllowed, $"gh:{owner}/{name} was not found on GitHub", "Check the owner and the name.");
            if (usage > settings.MaxRepositoryBytes)
                throw new ChargehandException(ErrorCode.RepositoryNotAllowed,
                    $"gh:{owner}/{name} is {usage:N0} bytes, over the peer cache's {settings.MaxRepositoryBytes:N0} byte limit", "Read a smaller repository, or raise peer_clone.max_repository_bytes.");
            var free = (freeBytesOf ?? DefaultFreeBytes)(settings.Root);
            if (free < settings.MinFreeBytes)
                throw new ChargehandException(ErrorCode.RepositoryNotAllowed,
                    $"the peer cache's filesystem has {free:N0} bytes free, under the {settings.MinFreeBytes:N0} byte minimum", "Free space on that filesystem, or lower peer_clone.min_free_bytes.");
            var dir = Path.Combine(Path.GetFullPath(settings.Root), owner, $"{name}@{commit[..Math.Min(commit.Length, 12)]}");
            await CloneAsync(owner, name, commit, dir, ct);
            acquired = true;
            return new PeerCloneLease(dir, _gate);
        }
        finally
        {
            if (!acquired)
                _gate.Release();
        }
    }

    private async Task CloneAsync(string owner, string name, string commit, string dir, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(dir)!;
        Directory.CreateDirectory(parent);
        var temp = $"{dir}.tmp-{Guid.NewGuid():N}";
        var url = await remoteUrl($"{owner}/{name}", ct);
        if (await Git(parent, ct, "clone", "-q", "--depth", "1", "--single-branch", "--", url, temp) is null)
            throw new ChargehandException(ErrorCode.CheckoutInvalid, $"could not clone gh:{owner}/{name} into the peer cache",
                "Check that the repository is public and reachable, and that the peer_clone token is valid.");
        var head = await Git(temp, ct, "rev-parse", "HEAD");
        if (head != commit)
        {
            Directory.Delete(temp, true);
            throw new ChargehandException(ErrorCode.CheckoutInvalid, $"gh:{owner}/{name}'s default branch is at {head ?? "no commit"}, not the requested {commit}",
                "Pin the commit the default branch is at; a shallow clone only reaches that one.");
        }
        try
        {
            Directory.Move(temp, dir);
        }
        catch (IOException)
        {
            Directory.Delete(temp, true);
            throw;
        }
    }

    private static long DefaultFreeBytes(string root)
    {
        Directory.CreateDirectory(root);
        return new DriveInfo(Path.GetFullPath(root)).AvailableFreeSpace;
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>git's trimmed output, or null when it fails.</summary>
    private static async Task<string?> Git(string directory, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git", ["-C", directory, .. args]) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var output = await p.StandardOutput.ReadToEndAsync(ct);
        await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return p.ExitCode == 0 ? output.Trim() : null;
    }
}

/// <summary>Deletes the clone's directory and releases the admission slot, in that order, even when one of a run's many failures
/// is the deletion's own: the slot must free either way, or every peer read after it would wait forever.</summary>
internal sealed class PeerCloneLease(string directory, SemaphoreSlim gate) : IPeerCloneLease
{
    public string Directory => directory;

    public ValueTask DisposeAsync()
    {
        try
        {
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, true);
        }
        finally
        {
            gate.Release();
        }
        return ValueTask.CompletedTask;
    }
}
