using System.Text.RegularExpressions;

namespace Chargehand.Modules.Forge.Api;

/// <summary>One finished attempt of a CI job, as the forge's history lists it (CHARGEHAND-197).</summary>
/// <param name="Workflow">The workflow on GitHub, the stage on GitLab.</param>
/// <param name="Ref">The branch the run was for; null when the forge names none.</param>
/// <param name="PullNumber">The pull or merge request the run was for; null for a branch run.</param>
/// <param name="At">When the attempt finished.</param>
public sealed record CiJob(long Id, string Workflow, string Name, string HeadSha, string? Ref, int? PullNumber, CheckOutcome Outcome, DateTimeOffset At, string? Url);

/// <summary>A job that failed and then passed on the same commit: a measured flake, unless the job reads something outside the commit.</summary>
public sealed record CiFlake(CiJob Failed, CiJob Passed);

/// <summary>A forge's CI history, read-only. A forge without one says so.</summary>
public interface ICiHistory
{
    /// <summary>The finished job attempts of the runs since <paramref name="since"/>, on every branch and pull request. A forge may leave out the runs that cannot hold a flake (no failure, no re-run) to save calls.</summary>
    Task<IReadOnlyList<CiJob>> RecentJobsAsync(RemoteRepository repository, DateTimeOffset since, CancellationToken ct);
}

public static partial class CiFlakes
{
    /// <summary>One flake per job and commit: its last failed attempt before a pass, and that pass. Newest first.</summary>
    public static IReadOnlyList<CiFlake> Find(IEnumerable<CiJob> jobs) =>
        [.. jobs.GroupBy(j => (j.Workflow, j.Name, j.HeadSha)).Select(Flake).OfType<CiFlake>().OrderByDescending(f => f.Failed.At).ThenBy(f => f.Failed.Id)];

    private static CiFlake? Flake(IEnumerable<CiJob> attempts)
    {
        CiJob? failed = null;
        foreach (var job in attempts.OrderBy(j => j.At).ThenBy(j => j.Id))
        {
            if (job.Outcome == CheckOutcome.Failed)
                failed = job;
            else if (job.Outcome == CheckOutcome.Passed && failed is not null)
                return new CiFlake(failed, job);
        }
        return null;
    }

    /// <summary>The names of the tests a job log reports as failed, in the forms of the common runners (dotnet test, go test, pytest, jest, Maven Surefire, cargo test); at most <paramref name="max"/>.</summary>
    public static IReadOnlyList<string> FailedTests(string log, int max = 10) =>
        [.. FailedTest().Matches(log).Select(m => m.Groups.Values.Skip(1).First(g => g.Success).Value.Trim()).Where(n => n.Length > 0).Distinct().Take(max)];

    [GeneratedRegex(@"^\s*Failed (\S[^\r\n\[]*?) \[|--- FAIL: (\S+)|^FAILED (\S+)|^\s*● (\S[^\r\n]*?)\s*$|^\[ERROR\]\s+(\w[\w.$]*\.\w+)[:(]|^test (\S+) \.\.\. FAILED", RegexOptions.Multiline)]
    private static partial Regex FailedTest();
}
