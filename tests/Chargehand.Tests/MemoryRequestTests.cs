using Chargehand.Config;
using Chargehand.Driven;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0045, admission: what the host must have free for a task is the 90th percentile of the peak memory of recorded sessions, not the container's limit. The records are synthetic;
/// nothing starts a container.</summary>
public class MemoryRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private const string Repo = "o/r";
    private const int Limit = 3072;

    private static LearningRecord Peak(int? mb, string repository = Repo, int daysAgo = 1, string outcome = LearningOutcome.Completed) =>
        LearningTests.Record($"run-{Guid.NewGuid():N}", repository, outcome: outcome, at: Now.AddDays(-daysAgo)) with { MemoryPeakMb = mb };

    private static List<LearningRecord> Peaks(string repository, params int[] mb) => [.. mb.Select(m => Peak(m, repository))];

    private static MemoryRequestChoice Choose(IReadOnlyList<LearningRecord> records, DrivenAdmission? settings = null, int limit = Limit, string repository = Repo) =>
        MemoryRequest.Choose(settings ?? new DrivenAdmission(), limit, repository, records, Now);

    [Fact]
    public void The_request_is_the_90th_percentile_of_the_repositorys_recorded_peaks()
    {
        var choice = Choose(Peaks(Repo, 600, 700, 800, 900, 1000, 1100, 1200, 1300, 1400, 2000));   // nearest rank: the 9th of 10

        Assert.Equal(new MemoryRequestChoice(1400, "history", 10, 90, "repository"), choice);
    }

    [Fact]
    public void A_repository_with_too_few_peaks_falls_back_to_all_repositories()
    {
        List<LearningRecord> records = [.. Peaks(Repo, 5000, 5000, 5000), .. Peaks("o/other", 700, 700, 700, 700, 700, 700, 700, 700, 700, 800)];

        var choice = Choose(records);

        Assert.Equal("all", choice.Scope);
        Assert.Equal(13, choice.N);
        Assert.Equal(Limit, choice.Mb);   // the three big sessions of this repository count in the pool, and its 90th percentile is above the limit
    }

    [Fact]
    public void A_repository_with_enough_peaks_is_not_judged_by_the_others()
    {
        List<LearningRecord> records = [.. Peaks(Repo, 600, 600, 600, 600, 600, 600, 600, 600, 600, 700), .. Peaks("o/other", 3000, 3000, 3000, 3000, 3000)];

        Assert.Equal(new MemoryRequestChoice(600, "history", 10, 90, "repository"), Choose(records));
    }

    [Fact]
    public void Below_the_minimum_everywhere_the_profiles_default_applies()
    {
        var choice = Choose(Peaks(Repo, 800, 900, 1000, 1100, 1200, 1300, 1400, 1500, 1600));   // nine peaks

        Assert.Equal(new MemoryRequestChoice(1536, "default"), choice);
        Assert.Equal(new MemoryRequestChoice(2000, "default"), Choose([], new DrivenAdmission(DefaultRequestMb: 2000)));
    }

    [Fact]
    public void The_request_is_never_below_512_and_never_above_the_containers_limit()
    {
        Assert.Equal(512, Choose(Peaks(Repo, 200, 210, 220, 230, 240, 250, 260, 270, 280, 290)).Mb);
        Assert.Equal(Limit, Choose(Peaks(Repo, 4000, 4000, 4000, 4000, 4000, 4000, 4000, 4000, 4000, 4000)).Mb);
        Assert.Equal(Limit, Choose([], new DrivenAdmission(DefaultRequestMb: 8000)).Mb);
        Assert.Equal(1024, Choose([], limit: 1024).Mb);   // the default 1536 is cut to a small limit
        Assert.Equal(256, Choose([], limit: 256).Mb);     // the limit wins over the floor: a container cannot use more than it is given
    }

    [Fact]
    public void A_record_without_a_peak_is_left_out()
    {
        List<LearningRecord> records = [.. Peaks(Repo, 900, 900, 900, 900, 900, 900, 900, 900, 900, 900), Peak(null), Peak(0), Peak(null, daysAgo: 2)];

        var choice = Choose(records);

        Assert.Equal((900, 10), (choice.Mb, choice.N));
    }

    [Fact]
    public void Peaks_older_than_the_window_are_left_out()
    {
        List<LearningRecord> records = [.. Peaks(Repo, 900, 900, 900, 900, 900), .. Enumerable.Range(0, 5).Select(_ => Peak(2500, daysAgo: MemoryRequest.WindowDays + 1))];

        Assert.Equal("default", Choose(records).Source);   // five recent peaks are too few and the old ones do not count
        records.AddRange(Peaks(Repo, 900, 900, 900, 900, 900));
        Assert.Equal(900, Choose(records).Mb);
    }

    [Fact]
    public void A_task_that_failed_counts_too_so_a_session_the_kernel_killed_raises_the_request()
    {
        List<LearningRecord> records = [.. Peaks(Repo, 700, 700, 700, 700, 700, 700, 700, 700), Peak(Limit, outcome: LearningOutcome.Failed), Peak(Limit, outcome: LearningOutcome.Failed)];

        Assert.Equal(Limit, Choose(records).Mb);   // two of ten at the limit: the 90th percentile is the limit
    }

    [Fact]
    public void The_choice_describes_itself_for_the_server_log()
    {
        Assert.Equal("1400 MiB, the 90th percentile of 10 recorded session peaks (repository)", new MemoryRequestChoice(1400, "history", 10, 90, "repository").Describe());
        Assert.Equal("1536 MiB, the default (too few recorded session peaks)", new MemoryRequestChoice(1536, "default").Describe());
    }
}
