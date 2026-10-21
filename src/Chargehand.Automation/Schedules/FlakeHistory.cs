using System.Globalization;
using System.Text;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Automation;

/// <param name="FailedTests">The test names the failed attempt's log reports; empty when none was found.</param>
/// <param name="LogTail">The end of the failed attempt's log when no test name was found in it; null otherwise or when it could not be read.</param>
public sealed record FlakeReport(CiFlake Flake, IReadOnlyList<string> FailedTests, string? LogTail);

/// <summary>What the forge's CI history says about one repository (CHARGEHAND-197).</summary>
/// <param name="Jobs">The job attempts read.</param>
public sealed record FlakeHistory(int Days, int Jobs, IReadOnlyList<FlakeReport> Flakes);

/// <summary>The CI history the <c>flaky-tests</c> template is given.</summary>
public interface IFlakeHistory
{
    /// <returns>Null when the history cannot be read (no forge credential, an origin that is no forge's, the forge refused): the template then reads the test code only.</returns>
    Task<FlakeHistory?> ReadAsync(string checkoutPath, CancellationToken ct);
}

/// <summary>Reads the history through the forge of the checkout's <c>origin</c>, read-only: on GitHub as the App, on GitLab with the forge token (<paramref name="hostFor"/>, the driven run's
/// <c>ReadHost</c>). The failed attempt's log of each flake is read for its test names.</summary>
public sealed class ForgeFlakeHistory(Func<RemoteRepository, IIntegrationHost?> hostFor, DrivenForge? forge, TimeProvider? clock = null, Action<string>? warn = null) : IFlakeHistory
{
    public const int Days = 14;
    public const int MaxFlakes = 10;
    public const int LogChars = 20000;
    public const int TailChars = 600;

    public async Task<FlakeHistory?> ReadAsync(string checkoutPath, CancellationToken ct)
    {
        var origin = await GitRepositoryHeads.GitAsync(checkoutPath, ["remote", "get-url", "origin"], ct);
        if (origin.ExitCode != 0 || RemoteRepository.Parse(origin.Output, forge) is not { } remote)
            return Skip($"{Path.GetFileName(Path.GetFullPath(checkoutPath))} has no origin on a forge");
        if (hostFor(remote) is not { } host)
            return Skip($"no credential reads {remote.Owner}/{remote.Name}'s CI");
        try
        {
            var jobs = await host.RecentJobsAsync(remote, (clock ?? TimeProvider.System).GetUtcNow().AddDays(-Days), ct);
            List<FlakeReport> reports = [];
            foreach (var flake in CiFlakes.Find(jobs).Take(MaxFlakes))
                reports.Add(await ReportAsync(host, remote, flake, ct));
            return new FlakeHistory(Days, jobs.Count, reports);
        }
        catch (Exception e) when (e is IntegrationException or HttpRequestException || e is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Skip($"the CI history of {remote.Owner}/{remote.Name} could not be read: {e.Message}");
        }
    }

    private static async Task<FlakeReport> ReportAsync(IIntegrationHost host, RemoteRepository remote, CiFlake flake, CancellationToken ct)
    {
        string log;
        try
        {
            log = await host.JobLogTailAsync(remote, flake.Failed.Id, LogChars, ct);
        }
        catch (IntegrationException)
        {
            // Logs expire (90 days on GitHub by default); the flake still counts.
            return new FlakeReport(flake, [], null);
        }
        var tests = CiFlakes.FailedTests(log);
        return new FlakeReport(flake, tests, tests.Count > 0 ? null : GitHubIntegrationHost.TailOf(log, TailChars));
    }

    private FlakeHistory? Skip(string why)
    {
        warn?.Invoke($"schedules: flaky-tests: {why}; the template reads the test code only");
        return null;
    }
}

/// <summary>The <c>flaky-tests</c> goal with the measured flakes appended (CHARGEHAND-197).</summary>
public static class FlakyTestsGoal
{
    public static string Compose(FlakeHistory history)
    {
        var text = new StringBuilder(ScheduleTemplates.FlakyTestsMeasured).Append("\n\nCI history of the last ")
            .Append(history.Days.ToString(CultureInfo.InvariantCulture)).Append(" days, read from the forge: ").Append(history.Jobs.ToString(CultureInfo.InvariantCulture))
            .Append(" job attempts of the runs that could hold a flake. ");
        if (history.Flakes.Count == 0)
            return text.Append("None failed and then passed on the same commit.").ToString();
        text.Append(history.Flakes.Count.ToString(CultureInfo.InvariantCulture)).Append(" jobs failed and then passed on the same commit:");
        foreach (var report in history.Flakes)
            text.Append("\n- ").Append(Line(report));
        return text.ToString();
    }

    private static string Line(FlakeReport report)
    {
        var (failed, passed) = (report.Flake.Failed, report.Flake.Passed);
        var where = failed.PullNumber is { } pull ? $"pull request #{pull.ToString(CultureInfo.InvariantCulture)}" : failed.Ref is { } branch ? $"branch {branch}" : "an unnamed ref";
        var line = $"{failed.Workflow} / {failed.Name}, commit {Short(failed.HeadSha)} on {where}: failed {Stamp(failed.At)}{(failed.Url is { } url ? $" ({url})" : "")}, passed {Stamp(passed.At)}. ";
        return line + (report.FailedTests.Count > 0 ? $"Failed tests in the log: {string.Join(", ", report.FailedTests)}."
            : report.LogTail is { } tail ? $"No test name found in the log; its last lines:\n  > {tail.ReplaceLineEndings("\n  > ")}"
            : "The log could not be read.");
    }

    private static string Short(string sha) => sha.Length > 12 ? sha[..12] : sha;

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
