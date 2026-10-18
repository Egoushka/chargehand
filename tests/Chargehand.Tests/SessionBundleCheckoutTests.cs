using System.Diagnostics;
using Chargehand.Contracts;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0065: a driven session's own review call is checked out from its live bundle, so it sees the branch's commits instead of
/// needing them to exist in the task's base checkout; a call that carries no session signal is still pinned to the base commit, exactly
/// as before (RunScope's pinning, tested in RunTokenTests, is untouched by this).</summary>
public class SessionBundleCheckoutTests
{
    private static RunRequest ReviewRequest(RepositoryRef repo) =>
        new("request/v1", "Review the change against its goal.", new RequestContext(false, "review", Repository: repo));

    /// <summary>Serves a fixed bundle file (or none) as a session's live <c>chargehand.bundle</c>; every other member is unused by these tests.</summary>
    private sealed class BundleVolumes(string? bundlePath) : ISessionVolumes
    {
        public Task WriteTaskAsync(string runId, SessionTask task, CancellationToken ct) => throw new NotSupportedException();

        public Task WriteSeedAsync(string runId, string path, CancellationToken ct) => throw new NotSupportedException();

        public Task FetchOutputAsync(string runId, string directory, CancellationToken ct) => throw new NotSupportedException();

        public Task<TaskUsage?> ReadUsageAsync(string runId, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> FetchBundleAsync(string runId, string path, CancellationToken ct)
        {
            if (bundlePath is null)
                return Task.FromResult(false);
            File.Copy(bundlePath, path, overwrite: true);
            return Task.FromResult(true);
        }
    }

    private static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}{stdout}");
    }

    /// <summary>Commits a second file on a new branch off <paramref name="repo"/>'s commit, bundles that branch to a file under
    /// <paramref name="root"/>, then returns the checkout to <paramref name="repo"/>'s commit, as a real base checkout stays.</summary>
    private static string BranchAndBundle(string root, RepositoryRef repo, string branch)
    {
        Git(repo.Path, "checkout", "-q", "-b", branch);
        File.WriteAllText(Path.Combine(repo.Path, "branch-only.txt"), "from the session\n");
        Git(repo.Path, "add", "branch-only.txt");
        Git(repo.Path, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "session work");
        var bundle = Path.Combine(root, "session.bundle");
        Git(repo.Path, "bundle", "create", bundle, branch);
        Git(repo.Path, "checkout", "-q", repo.Commit);
        return bundle;
    }

    [Fact]
    public async Task A_sessions_review_call_sees_the_branch_through_its_bundle()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var bundle = BranchAndBundle(root.Path, repo, "change/session-task");
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var orchestrator = Runs.Orchestrator(runtime, root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")));
        orchestrator.SessionVolumes = new BundleVolumes(bundle);

        var r = await orchestrator.RunAsync(ReviewRequest(repo), CancellationToken.None, sessionBundleRunId: "sess-1");

        Assert.NotEqual(ResultStatus.Failed, r.Status);
        var directory = Assert.Single(runtime.Created).Directory;
        Assert.True(File.Exists(Path.Combine(directory, "branch-only.txt")));
        Assert.StartsWith(Path.Combine(root.Path, ".checkouts"), directory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_session_call_is_still_limited_to_the_pinned_commit()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        BranchAndBundle(root.Path, repo, "change/session-task");
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var orchestrator = Runs.Orchestrator(runtime, root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")));
        // Driven is wired up (SessionVolumes is set), but this call carries no session signal: sessionBundleRunId is left at its default (null).
        orchestrator.SessionVolumes = new BundleVolumes(null);

        var r = await orchestrator.RunAsync(ReviewRequest(repo), CancellationToken.None);

        Assert.NotEqual(ResultStatus.Failed, r.Status);
        var directory = Assert.Single(runtime.Created).Directory;
        Assert.False(File.Exists(Path.Combine(directory, "branch-only.txt")));
    }

    [Fact]
    public async Task A_sessions_review_call_falls_back_to_the_pinned_commit_before_the_branch_has_a_bundle()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var orchestrator = Runs.Orchestrator(runtime, root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")));
        // The session has made no commit yet, so its volume has nothing to fetch.
        orchestrator.SessionVolumes = new BundleVolumes(null);

        var r = await orchestrator.RunAsync(ReviewRequest(repo), CancellationToken.None, sessionBundleRunId: "sess-1");

        Assert.NotEqual(ResultStatus.Failed, r.Status);
        Assert.StartsWith(Path.Combine(root.Path, ".checkouts"), Assert.Single(runtime.Created).Directory, StringComparison.Ordinal);
    }
}
