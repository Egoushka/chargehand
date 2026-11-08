using System.Diagnostics;
using Chargehand.Modules.Watch.Api;

namespace Chargehand.Modules.Watch.Tests;

/// <summary>Issue 790: the bisect agent finds the commit behind a regression in a fixture repository with one seeded, and opens an issue naming it.</summary>
public sealed class BisectAgentTests : IDisposable
{
    private const string Check = "test \"$(sh sum.sh)\" = 5";

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("chargehand-bisect-");

    private string Repo => _dir.FullName;

    public void Dispose() => _dir.Delete(recursive: true);

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])["-c", "user.name=writer", "-c", "user.email=writer@example.invalid", "-c", "commit.gpgsign=false", .. args])
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {stdout}{stderr}");
        return stdout.Trim();
    }

    private string Commit(string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(Repo, file), content);
        Git("add", "-A");
        Git("commit", "-q", "-m", message);
        return Git("rev-parse", "HEAD");
    }

    /// <summary>Twelve commits: sum.sh prints 5 until the seeded one makes it print 6; notes before and after it touch other files.</summary>
    private (string Good, string Regression, string Bad) Fixture(int seededAt = 7)
    {
        Git("init", "-q", "-b", "main");
        var good = Commit("sum.sh", "echo $((2 + 3))\n", "feat: add sum");
        string? regression = null;
        for (var i = 1; i < 12; i++)
        {
            var sha = i == seededAt
                ? Commit("sum.sh", "echo $((2 + 4))\n", "refactor: tidy sum")
                : Commit($"note{i}.txt", $"note {i}\n", $"docs: note {i}");
            if (i == seededAt)
                regression = sha;
        }
        return (good, regression!, Git("rev-parse", "HEAD"));
    }

    private sealed class RecordingTracker : IIssueTracker
    {
        public List<BisectIssue> Opened { get; } = [];

        public Task<string> OpenAsync(BisectIssue issue, CancellationToken ct)
        {
            Opened.Add(issue);
            return Task.FromResult("https://forge.example.invalid/issues/1");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(11)]
    public async Task Bisecting_a_fixture_with_a_seeded_regression_names_the_commit_that_introduced_it(int seededAt)
    {
        var (good, regression, bad) = Fixture(seededAt);

        var outcome = await BisectAgent.RunAsync(new BisectRequest(Repo, good, bad, Check), default);

        Assert.True(outcome.Found, outcome.Problem);
        Assert.Equal(regression, outcome.Commit);
        Assert.Equal("refactor: tidy sum", outcome.Subject);
        Assert.Equal("writer", outcome.Author);
        Assert.InRange(outcome.Steps, 1, 5);
    }

    [Fact]
    public async Task The_agent_opens_an_issue_naming_the_commit()
    {
        var (good, regression, bad) = Fixture();
        var tracker = new RecordingTracker();

        var (outcome, issue, url) = await BisectAgent.RunAndReportAsync(new BisectRequest(Repo, good, "main", Check), tracker, default);

        Assert.True(outcome.Found, outcome.Problem);
        Assert.Equal("https://forge.example.invalid/issues/1", url);
        var opened = Assert.Single(tracker.Opened);
        Assert.Same(issue, opened);
        Assert.Contains(regression[..12], opened.Title, StringComparison.Ordinal);
        Assert.Contains("refactor: tidy sum", opened.Title, StringComparison.Ordinal);
        Assert.Contains(regression, opened.Body, StringComparison.Ordinal);
        Assert.Contains("test \"$(sh sum.sh)\" = 5", opened.Body, StringComparison.Ordinal);
        Assert.Equal(bad, Git("rev-parse", "HEAD"));
    }

    [Fact]
    public async Task The_callers_checkout_is_left_as_it_was()
    {
        var (good, _, bad) = Fixture();
        File.WriteAllText(Path.Combine(Repo, "wip.txt"), "uncommitted\n");

        await BisectAgent.RunAsync(new BisectRequest(Repo, good, bad, Check), default);

        Assert.Equal(bad, Git("rev-parse", "HEAD"));
        Assert.Equal("main", Git("branch", "--show-current"));
        Assert.Equal("?? wip.txt", Git("status", "--porcelain"));
        Assert.Single(Git("worktree", "list", "--porcelain").Split('\n'), l => l.StartsWith("worktree ", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(Repo, ".git", "BISECT_LOG")));
    }

    [Fact]
    public async Task A_check_that_fails_on_the_good_commit_is_reported_and_no_issue_is_opened()
    {
        var (good, _, bad) = Fixture();
        var tracker = new RecordingTracker();

        var (outcome, issue, url) = await BisectAgent.RunAndReportAsync(new BisectRequest(Repo, good, bad, "exit 1"), tracker, default);

        Assert.False(outcome.Found);
        Assert.Contains("fails on the good commit", outcome.Problem, StringComparison.Ordinal);
        Assert.Null(issue);
        Assert.Null(url);
        Assert.Empty(tracker.Opened);
    }

    [Fact]
    public async Task A_check_that_passes_on_the_bad_commit_is_reported()
    {
        var (good, _, bad) = Fixture();

        var outcome = await BisectAgent.RunAsync(new BisectRequest(Repo, good, bad, "true"), default);

        Assert.False(outcome.Found);
        Assert.Contains("passes on the bad commit", outcome.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ref_that_is_not_a_commit_or_looks_like_an_option_is_refused()
    {
        var (good, _, _) = Fixture();

        Assert.Contains("is not a commit", (await BisectAgent.RunAsync(new BisectRequest(Repo, good, "no-such-ref", Check), default)).Problem, StringComparison.Ordinal);
        Assert.Contains("cannot start with '-'", (await BisectAgent.RunAsync(new BisectRequest(Repo, "--all", "main", Check), default)).Problem, StringComparison.Ordinal);
    }
}
