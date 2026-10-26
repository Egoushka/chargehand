using Chargehand.Automation;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Tests;

/// <summary>CHARGEHAND-197: the <c>flaky-tests</c> template is given the jobs that failed and then passed on the same commit, read from the forge's CI history, when that history can be read.</summary>
public class ScheduleFlakeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private static ScheduleEntry Flaky() => new("flaky", "daily 04:00", Template: ScheduleTemplates.FlakyTests, Repository: new AutomationRepository("/srv/repos/repo"));

    private static CiJob Job(long id, CheckOutcome outcome, int minutes, int? pull = 7) =>
        new(id, "ci", "test", "abc1234def5678abc1234", "fix", pull, outcome, T0.AddMinutes(minutes), $"https://example.test/j/{id}");

    private static async Task<string> StartedGoal(ScheduleRig rig)
    {
        await rig.Tick();
        await rig.Wake(TimeSpan.FromHours(4));
        // The trigger marker the start adds for crash recovery is not part of the goal.
        return System.Text.RegularExpressions.Regex.Replace(Assert.Single(rig.Starter.Started).Text, @"\n\n\[trigger [^\]]*\]$", "");
    }

    [Fact]
    public async Task With_history_the_goal_lists_each_measured_flake_with_its_failed_tests()
    {
        using var rig = new ScheduleRig(Flaky());
        rig.Flakes.History = new FlakeHistory(14, 40, [
            new FlakeReport(new CiFlake(Job(1, CheckOutcome.Failed, 0), Job(2, CheckOutcome.Passed, 30)), ["RetryTests.Twice", "ClockTests.Wraps"], null),
            new FlakeReport(new CiFlake(Job(3, CheckOutcome.Failed, 0, pull: null), Job(4, CheckOutcome.Passed, 30, pull: null)), [], "Error: connect ECONNRESET\nnpm ERR!")]);

        var goal = await StartedGoal(rig);

        Assert.Equal(["/srv/repos/repo"], rig.Flakes.Read);
        Assert.StartsWith(ScheduleTemplates.FlakyTestsMeasured, goal);
        Assert.Contains("40 job attempts", goal);
        Assert.Contains("ci / test, commit abc1234def56 on pull request #7: failed 2026-10-01 10:00 UTC (https://example.test/j/1), passed 2026-10-01 10:30 UTC. Failed tests in the log: RetryTests.Twice, ClockTests.Wraps.", goal);
        Assert.Contains("on branch fix", goal);
        Assert.Contains("No test name found in the log; its last lines:\n  > Error: connect ECONNRESET\n  > npm ERR!", goal);
    }

    [Fact]
    public async Task History_without_a_flake_says_so()
    {
        using var rig = new ScheduleRig(Flaky());
        rig.Flakes.History = new FlakeHistory(14, 12, []);
        Assert.EndsWith("None failed and then passed on the same commit.", await StartedGoal(rig));
    }

    [Fact]
    public async Task Without_history_the_template_reads_the_test_code_and_says_it_could_not_see_ci()
    {
        using var rig = new ScheduleRig(Flaky());
        var goal = await StartedGoal(rig);
        Assert.Equal(ScheduleTemplates.Find(ScheduleTemplates.FlakyTests)!.Goal, goal);
        Assert.Contains("could not be read", goal);
    }

    [Fact]
    public async Task Another_template_never_reads_history()
    {
        using var rig = new ScheduleRig(ScheduleRig.Docs());
        await StartedGoal(rig);
        Assert.Empty(rig.Flakes.Read);
    }

    private sealed class HistoryHost(IReadOnlyList<CiJob> jobs, string? log = null) : IIntegrationHost
    {
        public List<(string Owner, string Name, DateTimeOffset Since)> Asked { get; } = [];

        public Task<IReadOnlyList<CiJob>> RecentJobsAsync(RemoteRepository repository, DateTimeOffset since, CancellationToken ct)
        {
            Asked.Add((repository.Owner, repository.Name, since));
            return Task.FromResult(jobs);
        }

        public Task<string> JobLogTailAsync(RemoteRepository repository, long jobId, int maxChars, CancellationToken ct) =>
            log is null ? throw new IntegrationException("GitHub answered 410 to the job log") : Task.FromResult(log);

        public Task<PrSnapshot> GetAsync(RemoteRepository repository, int number, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> BehindByAsync(RemoteRepository repository, string baseBranch, string branch, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateBranchAsync(RemoteRepository repository, int number, string expectedHeadSha, CancellationToken ct) => throw new NotSupportedException();
        public Task MarkReadyAsync(RemoteRepository repository, string nodeId, CancellationToken ct) => throw new NotSupportedException();
        public Task EnableAutoMergeAsync(RemoteRepository repository, string nodeId, string headSha, CancellationToken ct) => throw new NotSupportedException();
        public Task MergeNowAsync(RemoteRepository repository, int number, string headSha, CancellationToken ct) => throw new NotSupportedException();
        public Task CommentAsync(RemoteRepository repository, int number, string body, CancellationToken ct) => throw new NotSupportedException();
    }

    private static ManualTimeProvider Clock()
    {
        var time = new ManualTimeProvider();
        time.Advance(T0 - time.GetUtcNow());
        return time;
    }

    [Fact]
    public async Task The_forge_of_the_origin_is_asked_for_two_weeks_and_the_failed_logs_give_the_test_names()
    {
        using var dir = new TempDir();
        var repo = Runs.GitRepo(dir.Path);
        Runs.SetOrigin(repo, "https://github.com/o/r.git");
        var host = new HistoryHost([Job(1, CheckOutcome.Failed, 0), Job(2, CheckOutcome.Passed, 30)], "  Failed RetryTests.Twice [3 ms]");

        var history = await new ForgeFlakeHistory(_ => host, null, Clock()).ReadAsync(repo.Path, default);

        Assert.Equal([("o", "r", T0.AddDays(-14))], host.Asked);
        var report = Assert.Single(history!.Flakes);
        Assert.Equal(["RetryTests.Twice"], report.FailedTests);
        Assert.Null(report.LogTail);
    }

    [Fact]
    public async Task An_expired_log_still_counts_the_flake_and_no_credential_or_a_refusal_means_no_history()
    {
        using var dir = new TempDir();
        var repo = Runs.GitRepo(dir.Path);
        Runs.SetOrigin(repo, "https://github.com/o/r.git");
        List<string> warnings = [];

        var expired = await new ForgeFlakeHistory(_ => new HistoryHost([Job(1, CheckOutcome.Failed, 0), Job(2, CheckOutcome.Passed, 30)]), null, Clock()).ReadAsync(repo.Path, default);
        Assert.Equal((0, (string?)null), (Assert.Single(expired!.Flakes).FailedTests.Count, expired.Flakes[0].LogTail));

        Assert.Null(await new ForgeFlakeHistory(_ => null, null, Clock(), warnings.Add).ReadAsync(repo.Path, default));
        Assert.Null(await new ForgeFlakeHistory(_ => new RefusingHost(), null, Clock(), warnings.Add).ReadAsync(repo.Path, default));
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Contains("reads the test code only", w));
        Assert.Contains("Actions permission", warnings[1]);
    }

    private sealed class RefusingHost : IIntegrationHost
    {
        public Task<IReadOnlyList<CiJob>> RecentJobsAsync(RemoteRepository repository, DateTimeOffset since, CancellationToken ct) =>
            throw new IntegrationException("GitHub answered 403 to the CI history (the App needs the Actions permission to read)");

        public Task<PrSnapshot> GetAsync(RemoteRepository repository, int number, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> BehindByAsync(RemoteRepository repository, string baseBranch, string branch, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateBranchAsync(RemoteRepository repository, int number, string expectedHeadSha, CancellationToken ct) => throw new NotSupportedException();
        public Task MarkReadyAsync(RemoteRepository repository, string nodeId, CancellationToken ct) => throw new NotSupportedException();
        public Task EnableAutoMergeAsync(RemoteRepository repository, string nodeId, string headSha, CancellationToken ct) => throw new NotSupportedException();
        public Task MergeNowAsync(RemoteRepository repository, int number, string headSha, CancellationToken ct) => throw new NotSupportedException();
        public Task CommentAsync(RemoteRepository repository, int number, string body, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> JobLogTailAsync(RemoteRepository repository, long jobId, int maxChars, CancellationToken ct) => throw new NotSupportedException();
    }
}
