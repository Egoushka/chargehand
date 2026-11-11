using System.Diagnostics;
using Chargehand.Contracts;
using Chargehand.Modules.Handover.Api;

namespace Chargehand.Modules.Handover.Tests;

using Handover = Chargehand.Modules.Handover.Api.Handover;

/// <summary>Issue #737: before the push, the branch is merged with the remote's base branch by <c>git merge-tree --write-tree</c>; a branch that conflicts with it is never pushed.</summary>
public class HandoverMergeCheckTests
{
    private static string G(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {output}");
        return output;
    }

    /// <summary>The remote's main moves on from the base with a commit that writes <paramref name="file"/>; a fetch into the bare remote runs no pre-receive hook.</summary>
    private static void MainMoves(HandoverTests.Fixture f, string file, string content)
    {
        G(f.Source, "checkout", "-q", "-b", "elsewhere", f.BaseCommit);
        File.WriteAllText(Path.Combine(f.Source, file), content);
        G(f.Source, "add", "-A");
        G(f.Source, "-c", "user.name=t", "-c", "user.email=t@localhost", "commit", "-q", "-m", "elsewhere");
        G(f.Source, "checkout", "-q", "main");
        G(f.Remote, "fetch", "-q", f.Source, "elsewhere:main");
    }

    [Fact]
    public async Task A_branch_that_conflicts_with_main_on_the_remote_is_never_pushed()
    {
        using var f = new HandoverTests.Fixture();
        MainMoves(f, "retry.txt", "retry three times\n");
        var prs = new HandoverTests.FakePullRequests();

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), prs).RunAsync(f.Input(), default);

        Assert.Equal((HandoverStatus.NotPushed, ErrorCode.MergeConflict), (outcome.Status, outcome.Error));
        Assert.Contains("conflicts with main", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("retry.txt", outcome.Reason, StringComparison.Ordinal);
        Assert.Equal("", f.RemoteRef("refs/heads/chargehand/run1"));
        Assert.Empty(prs.Created);
    }

    [Fact]
    public async Task A_branch_that_merges_cleanly_with_a_main_that_moved_on_is_pushed()
    {
        using var f = new HandoverTests.Fixture();
        MainMoves(f, "other.txt", "unrelated\n");
        var prs = new HandoverTests.FakePullRequests();

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), prs).RunAsync(f.Input(), default);

        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        Assert.Equal(outcome.Commit, f.RemoteRef("refs/heads/chargehand/run1"));
        Assert.Single(prs.Created);
    }
}
