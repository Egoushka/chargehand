using System.Diagnostics;
using System.Text;

namespace Chargehand.Bench;

/// <summary>The checkouts a benchmark works in: one clone per repository at its pinned commit, where the server can read it, with <c>origin</c> pointing at a place the server may push to.</summary>
public interface IBenchWorkspace
{
    /// <summary>The clone the server reads, made or checked. An existing one must already be at the pin and clean.</summary>
    Task<string> PrepareAsync(BenchRepository repository, string remoteUrl, CancellationToken ct);

    /// <summary>The directory that holds <paramref name="branch"/> as a local branch, for the acceptance container; null when the remote has no such branch.</summary>
    Task<string?> FetchBranchAsync(BenchRepository repository, string branch, CancellationToken ct);

    /// <summary>A branch whose tree is the pinned commit's, one empty commit on top: what the baseline run checks without a session. Returns the directory that holds it.</summary>
    Task<string> BaselineBranchAsync(BenchRepository repository, string branch, CancellationToken ct);

    /// <summary>Whether the upstream has the pinned commit; null: it has, otherwise what went wrong. Needs the network.</summary>
    Task<string?> CheckPinAsync(BenchRepository repository, CancellationToken ct);

    /// <summary>What is in <see cref="PrepareAsync"/>'s clone now, for the dry run: null when there is none yet, else the commit it is at.</summary>
    Task<string?> ExistingCommitAsync(BenchRepository repository, CancellationToken ct);
}

/// <summary>Git on the machine that runs the benchmark. A remote that is a <c>file://</c> URL is made here: a bare repository holding the pin as <c>main</c> that takes branches under
/// <c>chargehand/</c> and nothing else. Any other remote must already exist and hold the pinned commit (a fork of the upstream does); nothing is pushed to it from here.</summary>
public sealed class GitBenchWorkspace(string workDirectory, string? acceptanceRoot = null) : IBenchWorkspace
{
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(15);

    /// <summary>Leading options of every git call: the repository's hooks never run.</summary>
    private static readonly string[] Quiet = ["-c", "core.hooksPath=/dev/null"];

    public string ClonePath(BenchRepository repository) => Path.Combine(Path.GetFullPath(workDirectory), "repos", repository.Name);

    private string AcceptancePath(BenchRepository repository) =>
        acceptanceRoot is null ? ClonePath(repository) : Path.Combine(Path.GetFullPath(acceptanceRoot), repository.Name);

    public async Task<string?> ExistingCommitAsync(BenchRepository repository, CancellationToken ct)
    {
        var path = ClonePath(repository);
        if (!Directory.Exists(Path.Combine(path, ".git")))
            return null;
        var head = await Git(path, ["rev-parse", "--verify", "HEAD"], ct);
        return head.ExitCode == 0 ? head.Output.Trim() : "";
    }

    public async Task<string> PrepareAsync(BenchRepository repository, string remoteUrl, CancellationToken ct)
    {
        var path = ClonePath(repository);
        if (Directory.Exists(Path.Combine(path, ".git")))
        {
            var head = await Git(path, ["rev-parse", "--verify", "HEAD"], ct);
            var status = await Git(path, ["status", "--porcelain"], ct);
            if (head.ExitCode != 0 || head.Output.Trim() != repository.Commit || status.Output.Trim().Length > 0)
                throw new BenchException($"{path} is not the clean checkout of {repository.Commit[..12]}; remove it and run again.");
            await Required(path, ["remote", "set-url", "origin", remoteUrl], ct);
        }
        else
        {
            Directory.CreateDirectory(path);
            try
            {
                await Required(path, ["init", "-q", "-b", "main"], ct);
                await FetchPinAsync(path, repository, ct);
                await Required(path, ["checkout", "-q", "--detach", repository.Commit], ct);
                await Required(path, ["branch", "-f", "main", repository.Commit], ct);
                await Required(path, ["checkout", "-q", "main"], ct);
                await Required(path, ["remote", "add", "origin", remoteUrl], ct);
            }
            catch
            {
                // A half-made clone would be refused next time as not the pin; take it away.
                Directory.Delete(path, true);
                throw;
            }
        }
        // The server reads the remote and the default branch from these: origin's URL and origin/HEAD.
        await Required(path, ["update-ref", "refs/remotes/origin/main", repository.Commit], ct);
        await Required(path, ["symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main"], ct);
        if (remoteUrl.StartsWith("file://", StringComparison.Ordinal))
            await SeedLocalRemoteAsync(path, new Uri(remoteUrl).LocalPath, ct);
        return path;
    }

    private static async Task FetchPinAsync(string path, BenchRepository repository, CancellationToken ct)
    {
        var direct = await Git(path, ["fetch", "-q", "--no-tags", repository.Url, repository.Commit], ct, Slow);
        if (direct.ExitCode == 0)
            return;
        // A host that will not serve a commit by id: take its branches and look for the commit in them.
        var all = await Git(path, ["fetch", "-q", "--no-tags", repository.Url, "+refs/heads/*:refs/bench/heads/*"], ct, Slow);
        if (all.ExitCode != 0 || (await Git(path, ["cat-file", "-e", $"{repository.Commit}^{{commit}}"], ct)).ExitCode != 0)
            throw new BenchException($"{repository.Url} does not serve commit {repository.Commit[..12]}: {direct.Output.Trim()}");
    }

    private static async Task SeedLocalRemoteAsync(string clone, string bare, CancellationToken ct)
    {
        if (!Directory.Exists(bare))
        {
            Directory.CreateDirectory(bare);
            await Required(bare, ["init", "-q", "--bare", "-b", "main"], ct);
            await Required(clone, ["push", "-q", bare, "main"], ct);
            var hook = Path.Combine(bare, "hooks", "pre-receive");
            await File.WriteAllTextAsync(hook, "#!/bin/sh\nwhile read -r _old _new ref; do\n  case $ref in refs/heads/chargehand/*) ;; *) echo \"bench remote: only chargehand/* may be pushed\" >&2; exit 1 ;; esac\ndone\n", ct);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public async Task<string?> FetchBranchAsync(BenchRepository repository, string branch, CancellationToken ct)
    {
        var clone = ClonePath(repository);
        var fetch = await Git(clone, ["fetch", "-q", "--no-tags", "origin", $"+refs/heads/{branch}:refs/heads/{branch}"], ct, Slow);
        if (fetch.ExitCode != 0)
            return null;
        return await AcceptanceCloneAsync(repository, branch, ct);
    }

    public async Task<string> BaselineBranchAsync(BenchRepository repository, string branch, CancellationToken ct)
    {
        var clone = ClonePath(repository);
        var commit = await Required(clone, ["-c", "user.name=chargehand-bench", "-c", "user.email=bench@example.invalid", "commit-tree", $"{repository.Commit}^{{tree}}", "-p", repository.Commit, "-m", "baseline"], ct);
        await Required(clone, ["update-ref", $"refs/heads/{branch}", commit.Trim()], ct);
        return await AcceptanceCloneAsync(repository, branch, ct) ?? throw new BenchException($"could not make the baseline branch for {repository.Name}.");
    }

    /// <summary>The directory the acceptance container's workspace is cloned from: the clone itself, or a copy under the acceptance root (a runner reads sources only there).</summary>
    private async Task<string?> AcceptanceCloneAsync(BenchRepository repository, string branch, CancellationToken ct)
    {
        var clone = ClonePath(repository);
        var target = AcceptancePath(repository);
        if (target == clone)
            return clone;
        if (!Directory.Exists(Path.Combine(target, ".git")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await Required(Path.GetDirectoryName(target)!, ["clone", "-q", "--no-hardlinks", "--no-checkout", clone, target], ct);
        }
        var fetch = await Git(target, ["fetch", "-q", "--no-tags", clone, $"+refs/heads/{branch}:refs/heads/{branch}"], ct, Slow);
        return fetch.ExitCode == 0 ? target : null;
    }

    public async Task<string?> CheckPinAsync(BenchRepository repository, CancellationToken ct)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"chargehand-bench-pin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            await Required(scratch, ["init", "-q"], ct);
            var fetch = await Git(scratch, ["fetch", "-q", "--no-tags", "--depth", "1", repository.Url, repository.Commit], ct, Slow);
            return fetch.ExitCode == 0 ? null : fetch.Output.Trim().Split('\n').LastOrDefault() ?? "the fetch failed";
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, true);
            }
            catch (IOException)
            {
                // A scratch directory in the temp folder; the system clears it.
            }
        }
    }

    private static async Task<string> Required(string directory, IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await Git(directory, args, ct);
        return result.ExitCode == 0 ? result.Output : throw new BenchException($"git {args.First(a => !a.StartsWith('-') && !a.Contains('=', StringComparison.Ordinal))} failed in {directory}: {result.Output.Trim()}");
    }

    /// <summary>Git with prompts and system configuration off; hooks of the repository never run.</summary>
    internal static async Task<(int ExitCode, string Output)> Git(string directory, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in Quiet.Concat(args))
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            ct.ThrowIfCancellationRequested();
            return (-1, "git timed out");
        }
        return (process.ExitCode, new StringBuilder().Append(await stdout).Append(await stderr).ToString());
    }
}
