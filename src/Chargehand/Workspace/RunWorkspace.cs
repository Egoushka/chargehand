using System.Diagnostics;
using System.Text;

namespace Chargehand.Workspace;

/// <summary>A temporary copy of the workspace's content for one verification run; deleted when disposed.</summary>
public sealed class ExportedTree(string directory) : IDisposable
{
    public string Directory => directory;

    public void Dispose()
    {
        if (System.IO.Directory.Exists(directory))
            System.IO.Directory.Delete(directory, recursive: true);
    }
}

/// <param name="Repository">Where the branch lives: the run clone's path. A caller fetches from it; nothing is pushed (ADR 0035).</param>
public sealed record BranchInfo(string Repository, string Branch, string Commit, string Base);

/// <summary>
/// A writing node's own clone (ADR 0035): made from the cached checkout of the pinned commit under
/// <c>&lt;worker_root&gt;/.runs/&lt;run&gt;/&lt;node&gt;</c>, on branch <c>chargehand/&lt;run&gt;/&lt;node&gt;</c>. The source repository and the shared
/// checkout are never written (ADR 0023). Chargehand commits for the worker with the repository's hooks off.
/// </summary>
public sealed class RunWorkspace
{
    internal const int MaxDiffBytes = 65536;

    private RunWorkspace(string directory, string branch, string baseCommit)
    {
        Directory = directory;
        Branch = branch;
        Base = baseCommit;
    }

    public string Directory { get; }

    public string Branch { get; }

    public string Base { get; }

    /// <param name="checkout">The cached checkout the orchestrator made at <paramref name="commit"/>.</param>
    public static async Task<RunWorkspace> CreateAsync(string checkout, string commit, string workerRoot, string runId, string nodeId, CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetFullPath(workerRoot), ".runs", runId, nodeId);
        var branch = $"chargehand/{runId}/{nodeId}";
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        // --local hard-links objects, which fails across mount points; git removes a failed clone and the copy is the fallback.
        if (await Git(Path.GetDirectoryName(directory)!, ct, "clone", "-q", "--local", "--no-checkout", "--", checkout, directory) is null
            && await Git(Path.GetDirectoryName(directory)!, ct, "clone", "-q", "--no-hardlinks", "--no-checkout", "--", checkout, directory) is null)
            throw new InvalidOperationException($"could not clone {checkout} for the run workspace");
        // The files the checkout leaves out (Orchestrator.Exclude) stay out of this clone too.
        var sparse = Path.Combine(checkout, ".git", "info", "sparse-checkout");
        if (File.Exists(sparse))
        {
            System.IO.Directory.CreateDirectory(Path.Combine(directory, ".git", "info"));
            File.Copy(sparse, Path.Combine(directory, ".git", "info", "sparse-checkout"), overwrite: true);
            if (await Git(directory, ct, "config", "core.sparseCheckout", "true") is null)
                throw new InvalidOperationException("could not set a sparse checkout in the run workspace");
        }
        if (await Git(directory, ct, "checkout", "-q", "-b", branch, commit) is null)
            throw new InvalidOperationException($"could not check out {commit} on {branch}");
        return new RunWorkspace(directory, branch, commit);
    }

    /// <summary>
    /// A copy of what a commit would hold (tracked files and the worker's new, non-ignored files), in a temporary directory beside
    /// the workspace. The verification command runs there, so what it writes (caches, build output) never reaches the branch.
    /// </summary>
    public async Task<ExportedTree> ExportAsync(CancellationToken ct)
    {
        await Git(Directory, ct, "add", "-A");
        var path = Path.Combine(Path.GetDirectoryName(Directory)!, $"{Path.GetFileName(Directory)}.verify-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(path);
        var tree = new ExportedTree(path);
        // checkout-index writes the index's files under the prefix and needs no working-tree state of its own.
        if (await Git(Directory, ct, "checkout-index", "-a", "-f", $"--prefix={path}/") is null)
        {
            tree.Dispose();
            throw new InvalidOperationException("git checkout-index failed in the run workspace");
        }
        return tree;
    }

    /// <summary>Commits everything the worker changed; null when the tree is unchanged. <paramref name="authorName"/> and <paramref name="authorEmail"/> are the identity chargehand commits as
    /// (<see cref="Chargehand.Modules.Handover.Api.DrivenIntegrator.CommitIdentity"/>): the default <c>chargehand &lt;chargehand@localhost&gt;</c> unless the caller passes the configured GitHub
    /// App's bot identity.</summary>
    public async Task<BranchInfo?> CommitAsync(string message, CancellationToken ct, string authorName = "chargehand", string authorEmail = "chargehand@localhost")
    {
        await Git(Directory, ct, "add", "-A");
        if (await Git(Directory, ct, "status", "--porcelain") is not { Length: > 0 })
            return null;
        // Hooks stay off and signing is not attempted: a repository's own hooks are code the worker may have changed.
        var commit = await Git(Directory, ct, "-c", "core.hooksPath=/dev/null", "-c", "commit.gpgsign=false", "-c", $"user.name={authorName}", "-c", $"user.email={authorEmail}",
            "commit", "-q", "-m", message) is null
            ? throw new InvalidOperationException("git commit failed in the run workspace")
            : await Git(Directory, ct, "rev-parse", "HEAD");
        return new BranchInfo(Directory, Branch, commit!, Base);
    }

    /// <summary>
    /// Fetches the branch into <paramref name="source"/> (the caller's repository) as a new local branch of the same name (ADR 0069): objects and one new ref.
    /// No <c>+</c>, so an existing ref is never moved; the working tree, the index and <c>HEAD</c> are not touched, and a branch name carrying the run id is new.
    /// </summary>
    /// <returns>Null when the branch landed, else what git said.</returns>
    public async Task<string?> LandAsync(string source, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", ["-C", source, "fetch", "--quiet", "--no-tags", "--no-write-fetch-head", "--no-recurse-submodules", "--", Directory, $"refs/heads/{Branch}:refs/heads/{Branch}"])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEndAsync(ct);
        var error = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        await output;
        var text = (await error).Trim();
        return p.ExitCode == 0 ? null : text.Length == 0 ? $"git fetch exited {p.ExitCode}" : text;
    }

    /// <summary><c>git diff base..HEAD</c>, cut to <see cref="MaxDiffBytes"/> with a marker (an inline artifact holds at most 64 KiB).</summary>
    public async Task<string> DiffAsync(CancellationToken ct)
    {
        var diff = await GitRaw(Directory, ct, "diff", "--no-color", "--no-ext-diff", "--no-textconv", $"{Base}..HEAD") ?? "";
        if (Encoding.UTF8.GetByteCount(diff) <= MaxDiffBytes)
            return diff;
        var marker = $"\n[diff truncated at {MaxDiffBytes} bytes]";
        var bytes = Encoding.UTF8.GetBytes(diff)[..(MaxDiffBytes - Encoding.UTF8.GetByteCount(marker))];
        return Encoding.UTF8.GetString(bytes).TrimEnd('�') + marker;
    }

    public async Task<IReadOnlyList<string>> ChangedPathsAsync(CancellationToken ct) =>
        (await GitRaw(Directory, ct, "diff", "--name-only", "-z", $"{Base}..HEAD") ?? "").Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static async Task<string?> Git(string directory, CancellationToken ct, params string[] args) => (await GitRaw(directory, ct, args))?.Trim();

    private static async Task<string?> GitRaw(string directory, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git", ["-C", directory, .. args])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEndAsync(ct);
        var error = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        await error;
        return p.ExitCode == 0 ? await output : null;
    }
}
