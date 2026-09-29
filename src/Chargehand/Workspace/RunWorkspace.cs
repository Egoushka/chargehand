using System.Diagnostics;
using System.Text;

namespace Chargehand.Workspace;

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
        if (await Git(directory, ct, "checkout", "-q", "-b", branch, commit) is null)
            throw new InvalidOperationException($"could not check out {commit} on {branch}");
        return new RunWorkspace(directory, branch, commit);
    }

    /// <summary>Commits everything the worker changed; null when the tree is unchanged.</summary>
    public async Task<BranchInfo?> CommitAsync(string message, CancellationToken ct)
    {
        await Git(Directory, ct, "add", "-A");
        if (await Git(Directory, ct, "status", "--porcelain") is not { Length: > 0 })
            return null;
        // Hooks stay off and signing is not attempted: a repository's own hooks are code the worker may have changed.
        var commit = await Git(Directory, ct, "-c", "core.hooksPath=/dev/null", "-c", "commit.gpgsign=false", "-c", "user.name=chargehand", "-c", "user.email=chargehand@localhost",
            "commit", "-q", "-m", message) is null
            ? throw new InvalidOperationException("git commit failed in the run workspace")
            : await Git(Directory, ct, "rev-parse", "HEAD");
        return new BranchInfo(Directory, Branch, commit!, Base);
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
