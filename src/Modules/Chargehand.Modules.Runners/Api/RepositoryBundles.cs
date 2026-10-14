using System.Collections.Concurrent;
using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Modules.Runners.Api;

/// <summary>The repository at the pinned commit as one git bundle, for a runner that cannot see the server's checkouts (ADR 0058). The bundle holds the commit's whole history under <c>HEAD</c>, which is what
/// a clone would fetch and what <c>git log</c> in the session shows. It is made from the server's own checkout, which sits detached at the commit (<c>git bundle create</c> refuses a bare commit and a shallow
/// repository, so there is no shallower option). One file per commit, made once for all the tasks and sessions of a batch that need it and kept for <paramref name="keepFor"/>, then removed by the next
/// bundle made. It carries objects and refs, no configuration and no hooks.</summary>
/// <param name="directory">Where the bundles are kept; created when needed.</param>
/// <param name="maxBytes">A bundle larger than this is not sent: the runner refuses what is over its own limit (<c>--max-bundle-mb</c>), and this keeps the server from making a file nobody will take.</param>
public sealed class RepositoryBundles(string directory, long maxBytes = RepositoryBundles.DefaultMaxBytes, TimeSpan? keepFor = null)
{
    public const long DefaultMaxBytes = 4096L * 1024 * 1024;

    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _making = new(StringComparer.Ordinal);

    /// <returns>The path of the bundle file; it exists and is complete.</returns>
    /// <exception cref="ChargehandException"><see cref="ErrorCode.SessionFailed"/>: the checkout is not at the commit, git failed, or the bundle is over the limit.</exception>
    public async Task<string> BundleAsync(string sourcePath, string commit, CancellationToken ct)
    {
        // One caller's cancel must not fail the others that wait for the same file: the work belongs to the bundle, each caller only stops waiting.
        var lazy = _making.GetOrAdd(commit, _ => new Lazy<Task<string>>(() => MakeAsync(sourcePath, commit, CancellationToken.None)));
        try
        {
            return await lazy.Value.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A failure is not kept: the next ask tries again.
            _making.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(commit, lazy));
            throw;
        }
    }

    private async Task<string> MakeAsync(string sourcePath, string commit, CancellationToken ct)
    {
        var final = Path.Combine(directory, $"{commit}.bundle");
        if (File.Exists(final) && new FileInfo(final) is { Length: > 0 } existing && DateTime.UtcNow - existing.LastWriteTimeUtc < (keepFor ?? TimeSpan.FromDays(1)))
            return final;
        Directory.CreateDirectory(directory);
        var env = GitProcess.Environment(directory);
        // The checkout is made detached at the pinned commit; anything else would send a different repository state than the one the batch is pinned to.
        var head = await GitProcess.RunAsync(sourcePath, env, ["rev-parse", "--verify", "HEAD^{commit}"], GitTimeout, ct);
        var wanted = await GitProcess.RunAsync(sourcePath, env, ["rev-parse", "--verify", "--end-of-options", $"{commit}^{{commit}}"], GitTimeout, ct);
        if (head.ExitCode != 0 || wanted.ExitCode != 0 || head.Stdout.Trim() != wanted.Stdout.Trim())
            throw Failed($"chargehand's checkout is not at commit {commit[..Math.Min(12, commit.Length)]}, so it cannot be bundled for a runner on another machine");
        var temp = Path.Combine(directory, $"{commit}.{Guid.NewGuid():N}.tmp");
        try
        {
            var made = await GitProcess.RunAsync(sourcePath, env, ["bundle", "create", temp, "HEAD"], GitTimeout, ct);
            if (made.ExitCode != 0)
                throw Failed($"could not bundle the repository: {made.Tail}");
            var length = new FileInfo(temp).Length;
            if (length > maxBytes)
                throw Failed($"the repository's bundle is {length / (1024 * 1024)} MiB, above the {maxBytes / (1024 * 1024)} MiB chargehand will send to a runner");
            File.Move(temp, final, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
        Prune(final);
        return final;
    }

    /// <summary>Removes bundles of other commits that nothing has asked for in the keep time.</summary>
    private void Prune(string keep)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.bundle"))
            try
            {
                if (file != keep && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > (keepFor ?? TimeSpan.FromDays(1)))
                    File.Delete(file);
            }
            catch (IOException)
            {
                // Another batch has it open; the next bundle made removes it.
            }
    }

    private static ChargehandException Failed(string message) =>
        new(ErrorCode.SessionFailed, message, "Check that the checkout under worker_root is intact, or run this runner with workspace shared.");
}
