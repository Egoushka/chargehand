using System.Diagnostics;
using Chargehand.Kernel;
using Chargehand.Modules.Handover.Api;

namespace Chargehand.Modules.Handover.Tests;

using Handover = Chargehand.Modules.Handover.Api.Handover;

/// <summary>ADR 0074: the pushed commits say which run wrote them, and so does the pull request body, which is what a squash merge keeps.</summary>
public class HandoverProvenanceTests
{
    private static readonly Provenance Run = new("run1", "t-1 (CH-9)", "claude-code 2.1.0", "sonnet", 0.5m, 1200, 40, "2 of 2 checks passed");

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {output}");
        return output.Trim();
    }

    [Fact]
    public async Task Every_pushed_commit_carries_the_trailers_and_keeps_its_tree_author_and_date()
    {
        using var f = new HandoverTests.Fixture(thenEdit: dir => File.WriteAllText(Path.Combine(dir, "more.txt"), "more\n"));
        var prs = new HandoverTests.FakePullRequests();
        var before = Git(f.Work, "log", "--format=%T %an %aI", $"{f.BaseCommit}..chargehand/run1");

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), prs).RunAsync(f.Input(with: i => i with { Provenance = Run }), default);

        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        Assert.Equal(outcome.Commit, f.RemoteRef("refs/heads/chargehand/run1"));
        Assert.Equal(before, Git(f.Remote, "log", "--format=%T %an %aI", $"{f.BaseCommit}..chargehand/run1"));
        var runs = Git(f.Remote, "log", "--format=%(trailers:key=Chargehand-Run,valueonly)", $"{f.BaseCommit}..chargehand/run1").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["run1", "run1"], runs);
        var head = Provenance.Read(Git(f.Remote, "log", "-1", "--format=%B", "chargehand/run1"));
        Assert.Equal("t-1 (CH-9)", head[Provenance.TaskKey]);
        Assert.Equal("$0.50, 1200 tokens, 40% cache", head[Provenance.SpendKey]);
    }

    [Fact]
    public async Task The_pull_request_body_ends_with_the_trailers_so_a_squash_merge_keeps_them()
    {
        using var f = new HandoverTests.Fixture();
        var prs = new HandoverTests.FakePullRequests();

        await new Handover(new HandoverTests.FakeVerifier(), prs).RunAsync(f.Input(with: i => i with { Provenance = Run }), default);

        var body = Assert.Single(prs.Created).Body;
        Assert.EndsWith("\n\n" + Run.Block() + "\n", body, StringComparison.Ordinal);
        Assert.Equal("run1", Provenance.Read("squash title\n\n" + body)[Provenance.RunKey]);
    }

    [Fact]
    public async Task A_repeated_handover_makes_the_same_commits_so_the_push_finds_them_on_the_remote()
    {
        using var f = new HandoverTests.Fixture();
        var input = f.Input(with: i => i with { Provenance = Run });

        var first = await new Handover(new HandoverTests.FakeVerifier(), new HandoverTests.FakePullRequests()).RunAsync(input, default);
        var second = await new Handover(new HandoverTests.FakeVerifier(), new HandoverTests.FakePullRequests()).RunAsync(input, default);

        Assert.Equal(HandoverStatus.Pushed, second.Status);
        Assert.Equal(first.Commit, second.Commit);
    }

    [Fact]
    public async Task Without_provenance_the_commits_are_pushed_as_the_session_made_them()
    {
        using var f = new HandoverTests.Fixture();
        var made = Git(f.Work, "rev-parse", "chargehand/run1");

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), new HandoverTests.FakePullRequests()).RunAsync(f.Input(), default);

        Assert.Equal(made, outcome.Commit);
    }

    /// <summary>CHARGEHAND-426: the session commits as chargehand@localhost (the container's fixed identity), but a repository configured with a GitHub App wants its
    /// pushed commits authored and committed as the App's bot noreply identity instead, since some repositories' secrets/noreply CI check rejects @localhost.</summary>
    [Fact]
    public async Task A_configured_app_identity_replaces_chargehand_localhost_on_every_pushed_commit()
    {
        using var f = new HandoverTests.Fixture(thenEdit: dir => File.WriteAllText(Path.Combine(dir, "more.txt"), "more\n"));
        var input = f.Input(with: i => i with { Provenance = Run, CommitAuthorName = "my-app[bot]", CommitAuthorEmail = "my-app[bot]@users.noreply.github.com" });

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), new HandoverTests.FakePullRequests()).RunAsync(input, default);

        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        var identities = Git(f.Remote, "log", "--format=%an <%ae> %cn <%ce>", $"{f.BaseCommit}..chargehand/run1").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(identities, line => Assert.Equal("my-app[bot] <my-app[bot]@users.noreply.github.com> my-app[bot] <my-app[bot]@users.noreply.github.com>", line));
    }

    [Fact]
    public async Task With_no_app_configured_pushed_commits_keep_the_chargehand_localhost_default()
    {
        using var f = new HandoverTests.Fixture();

        var outcome = await new Handover(new HandoverTests.FakeVerifier(), new HandoverTests.FakePullRequests()).RunAsync(f.Input(with: i => i with { Provenance = Run }), default);

        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        Assert.Equal("chargehand <chargehand@localhost>", Git(f.Remote, "log", "-1", "--format=%an <%ae>", "chargehand/run1"));
    }
}
