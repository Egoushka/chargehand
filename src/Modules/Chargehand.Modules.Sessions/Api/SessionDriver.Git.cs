using System.Diagnostics;
using System.Globalization;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Modules.Sessions.Api;

/// <summary>The git work of <see cref="SessionDriver"/>: the seed, the diff measure, the stop snapshot, the handoff's facts and the bundle.</summary>
public static partial class SessionDriver
{
    /// <summary>Feeds the governor the branch's added plus removed lines against the commit the workspace started at, tracked files only. <c>--no-optional-locks</c> keeps the
    /// measure from taking the index lock the session's own git may need; a git that fails or hangs only skips this measure.</summary>
    private static async Task MeasureAsync(string work, string start, Func<long, IReadOnlyList<GovernorDecision>> onDiff, Func<IReadOnlyList<GovernorDecision>, Task> govern,
        CancellationToken ct)
    {
        var diff = await Git(work, ["--no-optional-locks", "diff", "--numstat", start], ct, TimeSpan.FromSeconds(20));
        if (diff.Exit != 0)
            return;
        long lines = 0;
        foreach (var row in diff.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var cells = row.Split('\t');
            if (cells.Length >= 2 && long.TryParse(cells[0], NumberStyles.None, CultureInfo.InvariantCulture, out var added) && long.TryParse(cells[1], NumberStyles.None, CultureInfo.InvariantCulture, out var removed))
                lines += added + removed;
        }
        await govern(onDiff(lines));
    }

    private static async Task<bool> BundleAsync(SessionOptions options, CancellationToken ct)
    {
        var branch = options.Task.Branch;
        if ((await Git(options.WorkDirectory, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], ct)).Exit != 0)
            return false;
        return (await Git(options.WorkDirectory, ["bundle", "create", Path.Combine(options.OutDirectory, "chargehand.bundle"), branch, "--not", "--remotes"], ct)).Exit == 0;
    }

    /// <summary>The branch's head, or null when the branch does not exist yet (nothing to bundle). Used to tell a mid-session poll that the branch moved, without
    /// re-bundling on every tick.</summary>
    private static async Task<string?> BranchHeadAsync(SessionOptions options, CancellationToken ct) =>
        await Git(options.WorkDirectory, ["rev-parse", "--verify", "--quiet", $"refs/heads/{options.Task.Branch}"], ct) is { Exit: 0 } head ? head.Stdout.Trim() : null;

    private static async Task<int> CommitsAsync(SessionOptions options, string start, CancellationToken ct)
    {
        var counted = await Git(options.WorkDirectory, ["rev-list", "--count", $"{start}..{options.Task.Branch}"], ct);
        return counted.Exit == 0 && int.TryParse(counted.Stdout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    /// <summary>Writes <see cref="ContainerTemplate.HandoffFile"/> and <see cref="ContainerTemplate.ProgressFile"/> after a stop (ADR 0045), from git and the stream with no model call: the
    /// commit the workspace started at and the branch's head, the commit subjects and changed paths between them, and what <paramref name="trace"/> kept. A git call that fails
    /// leaves its facts out; a file that cannot be written only means the next session starts without it.</summary>
    private static async Task WriteHandoffAsync(SessionOptions options, string? start, string reason, int turns, SessionTrace trace, CancellationToken ct)
    {
        var timeout = options.GitTimeout ?? SnapshotGitTimeout;
        async Task<string?> Read(params string[] args) => await Git(options.WorkDirectory, args, ct, timeout) is { Exit: 0 } r ? r.Stdout : null;
        var head = (await Read("rev-parse", "--verify", "--quiet", $"refs/heads/{options.Task.Branch}") ?? await Read("rev-parse", "--verify", "--quiet", "HEAD"))?.Trim();
        List<string> commits = [];
        List<ChangedPath> changes = [];
        if (start is not null && head is not null)
        {
            commits = [.. (await Read("log", "--reverse", "--format=%s", $"{start}..{head}") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            foreach (var row in (await Read("diff", "--numstat", "--no-renames", "-z", start, head) ?? "").Split('\0', StringSplitOptions.RemoveEmptyEntries))
                if (row.Split('\t', 3) is [var added, var removed, var path])
                    changes.Add(new ChangedPath(path, long.TryParse(added, NumberStyles.None, CultureInfo.InvariantCulture, out var a) ? a : 0,
                        long.TryParse(removed, NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : 0));
        }
        var files = HandoffBuilder.Build(new HandoffFacts(reason, turns, start, head, commits, changes, trace.LastTest, trace.Steps, trace.Todos, trace.Calls), options.Secrets);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(options.OutDirectory, ContainerTemplate.HandoffFile), Redact(files.Json, options.Secrets), ct);
            await File.WriteAllTextAsync(Path.Combine(options.OutDirectory, ContainerTemplate.ProgressFile), Redact(files.Markdown, options.Secrets), ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A continuation session's workspace is the base commit on the task branch; the previous session's branch replaces it.</summary>
    private static async Task SeedAsync(SessionOptions options, CancellationToken ct)
    {
        var seed = Path.Combine(options.OutDirectory, ContainerTemplate.SeedFile);
        if (!File.Exists(seed))
            return;
        var fetch = await Git(options.WorkDirectory, ["-c", "fetch.fsckObjects=true", "-c", "core.hooksPath=/dev/null", "fetch", "--quiet", seed, $"refs/heads/{options.Task.Branch}"], ct);
        if (fetch.Exit != 0 || (await Git(options.WorkDirectory, ["reset", "--quiet", "--hard", "FETCH_HEAD"], ct)).Exit != 0)
            throw new InvalidOperationException($"the seed bundle does not apply to the workspace: {fetch.Stderr.Trim()}");
    }

    /// <summary>A file the stop snapshot leaves out: bigger than this is a build output or a download, not the session's work.</summary>
    public const long SnapshotMaxFileBytes = 5L * 1024 * 1024;

    private static readonly TimeSpan SnapshotGitTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Commits the working tree to the task branch (made if the session never made it) so a stopped session's files reach the bundle: the files over
    /// <see cref="SnapshotMaxFileBytes"/> stay out and the commit message names them. Nothing to commit leaves the branch as it is. A git that hangs or fails, or an
    /// <c>index.lock</c> a live git holds, skips the snapshot. A lock no git process holds is a killed session's leftover and is removed. The handover scans every commit's patch.</summary>
    /// <returns>What was not done and why, for the handover's text; null when there is nothing to say.</returns>
    private static async Task<string?> SnapshotAsync(SessionOptions options, string reason, int turns, CancellationToken ct)
    {
        var dir = options.WorkDirectory;
        var branch = options.Task.Branch;
        var timeout = options.GitTimeout ?? SnapshotGitTimeout;
        Task<(int Exit, string Stdout, string Stderr)> G(params string[] args) => Git(dir, args, ct, timeout);

        if (await ClearLockAsync(options, G) is { } locked)
            return locked;
        var exists = await G("rev-parse", "--verify", "--quiet", $"refs/heads/{branch}");
        if (exists.Exit == GitTimedOut)
            return Skipped("rev-parse", exists);
        if (exists.Exit != 0 && await G("checkout", "--quiet", "-b", branch) is { Exit: not 0 } made)
            return Skipped("checkout", made);
        var status = await G("status", "--porcelain");
        if (status.Exit != 0)
            return Skipped("status", status);
        if (status.Stdout.Length == 0)
            return null;
        var added = await G("add", "-A");
        if (added.Exit != 0)
            return Skipped("add", added);
        return await CommitWorkAsync(dir, reason, turns, G);
    }

    private static string Skipped(string step, (int Exit, string Stdout, string Stderr) r) =>
        $"the working tree was not committed: git {step} {(r.Exit == GitTimedOut ? "timed out" : "failed")}";

    /// <summary>An <c>index.lock</c> a live git holds skips the snapshot; one no git process holds is a killed session's leftover and is removed.</summary>
    private static async Task<string?> ClearLockAsync(SessionOptions options, Func<string[], Task<(int Exit, string Stdout, string Stderr)>> g)
    {
        var gitLock = await g(["rev-parse", "--git-path", "index.lock"]);
        if (gitLock.Exit != 0)
            return Skipped("rev-parse", gitLock);
        var lockFile = Path.GetFullPath(gitLock.Stdout.Trim(), options.WorkDirectory);
        if (File.Exists(lockFile))
        {
            if ((options.GitRunning ?? AnyGitProcess)())
                return "the working tree was not committed: .git/index.lock exists and a git process is running";
            try
            {
                File.Delete(lockFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return "the working tree was not committed: .git/index.lock exists and could not be removed";
            }
        }
        return null;
    }

    /// <summary>Takes the staged files over <see cref="SnapshotMaxFileBytes"/> out again and commits the rest as the <c>WIP:</c> commit.</summary>
    private static async Task<string?> CommitWorkAsync(string dir, string reason, int turns, Func<string[], Task<(int Exit, string Stdout, string Stderr)>> g)
    {
        var staged = await g(["diff", "--cached", "--name-only", "-z"]);
        if (staged.Exit != 0)
            return Skipped("diff", staged);
        var large = staged.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(path => new FileInfo(Path.Combine(dir, path)) is { Exists: true } file && file.Length > SnapshotMaxFileBytes).ToList();
        for (var i = 0; i < large.Count; i += 100)
            if (await g(["--literal-pathspecs", "reset", "--quiet", "HEAD", "--", .. large.Skip(i).Take(100)]) is { Exit: not 0 } unstaged)
                return Skipped("reset", unstaged);
        var note = large.Count == 0 ? null : $"not committed (over {SnapshotMaxFileBytes / 1024 / 1024} MiB): {string.Join(", ", large)}";
        if ((await g(["diff", "--cached", "--quiet"])).Exit == 0)
            return note;
        var message = new List<string> { "-m", $"WIP: session stopped by {reason} after {turns.ToString(CultureInfo.InvariantCulture)} turns" };
        if (note is not null)
            message.AddRange(["-m", note]);
        var commit = await g(["-c", "core.hooksPath=/dev/null", "commit", "--quiet", "--no-verify", .. message]);
        return commit.Exit == 0 ? note : Skipped("commit", commit);
    }

    private const int GitTimedOut = -1;

    private static bool AnyGitProcess()
    {
        var running = Process.GetProcessesByName("git");
        try
        {
            return running.Length > 0;
        }
        finally
        {
            foreach (var p in running)
                p.Dispose();
        }
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> Git(string directory, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } limit)
            deadline.CancelAfter(limit);
        try
        {
            await p.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            p.Kill(entireProcessTree: true);
            return (GitTimedOut, "", "git timed out");
        }
        return (p.ExitCode, await stdout, await stderr);
    }
}
