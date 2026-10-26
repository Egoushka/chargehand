using Chargehand.Contracts;
using Chargehand.Modules.Economics.Api;
using Chargehand.Modules.Ledger.Api;
using static Chargehand.Modules.Economics.Tests.Fixtures;

namespace Chargehand.Modules.Economics.Tests;

public class CostReportTests
{
    private static FakeGitHub Hub() => new() { Pulls = { [1] = (true, false), [2] = (false, true), [3] = (false, false) } };

    private static Task<CostReport> BuildAsync(RunLogData data, FakeGitHub hub, Func<StartRecord, bool>? include = null) =>
        CostReportBuilder.BuildAsync(data, new ForgePullRequestStates(_ => hub), Now, include);

    [Fact]
    public async Task Dollars_per_merged_pull_request_and_the_spend_that_left_nothing()
    {
        var log = Log(
            Run("a", "driven", "o/r", 4m, 2, [PullRequest(Pull("o/r", 1))]),   // merged
            Run("b", "driven", "o/r", 2m, 3, [PullRequest(Pull("o/r", 2))]),   // closed unmerged
            Run("c", "driven", "o/r", 1m, 3),                                   // no pull request
            Run("d", "driven", "o/r", 3m, 5, [PullRequest(Pull("o/r", 3))]));   // still open
        var window = (await BuildAsync(log, Hub())).Windows[0];

        var row = Assert.Single(window.Rows);
        Assert.Equal(7, window.Days);
        Assert.Equal(("o/r", "driven", 4, 10m), (row.Repository, row.Preset, row.Runs, row.Usd));
        Assert.Equal((3, 1, 1, 1, 0), (row.PullRequests, row.Merged, row.ClosedUnmerged, row.Open, row.Unknown));
        Assert.Equal(10m, row.UsdPerMerged);
        Assert.Equal((1m, 2m, 3m), (row.NoPullRequestUsd, row.ClosedUsd, row.WastedUsd));
        Assert.Equal(row with { Repository = "*", Preset = "*" }, window.Total);
    }

    [Fact]
    public async Task Seven_and_thirty_day_windows_hold_runs_by_their_finish_time()
    {
        var log = Log(Run("a", "driven", "o/r", 4m, 2, [PullRequest(Pull("o/r", 1))]), Run("b", "default", "o/r", 6m, 20), Run("c", "default", "o/r", 9m, 40));
        var report = await BuildAsync(log, Hub());

        Assert.Equal([7, 30], report.Windows.Select(w => w.Days));
        Assert.Equal(4m, report.Windows[0].Total.Usd);
        Assert.Equal(10m, report.Windows[1].Total.Usd);
        Assert.Equal(["default", "driven"], report.Windows[1].Rows.Select(r => r.Preset).Order());
        Assert.Null(report.Windows[1].Rows.Single(r => r.Preset == "default").UsdPerMerged);
    }

    [Fact]
    public async Task A_review_run_counts_toward_the_pull_request_it_was_sent()
    {
        var review = Run("r", "review", "o/r", 1.5m, 1, inputs: [new CallerInput("pr", "pull-request", "diff", Pull("o/r", 1))]);
        var log = Log(Run("a", "driven", "o/r", 4m, 2, [PullRequest(Pull("o/r", 1))]), review, Run("q", "review", "o/r", 0.5m, 1));
        var window = (await BuildAsync(log, Hub())).Windows[0];

        var reviewRow = window.Rows.Single(r => r.Preset == "review");
        Assert.Equal((2, 2m, 1, 1), (reviewRow.Runs, reviewRow.Usd, reviewRow.PullRequests, reviewRow.Merged));
        Assert.Equal(0.5m, reviewRow.NoPullRequestUsd);
        Assert.Equal(1, window.Total.PullRequests);   // the same pull request is counted once across presets
        Assert.Equal(6m, window.Total.UsdPerMerged);
    }

    [Fact]
    public async Task A_batch_splits_by_its_task_rows_and_its_overhead_counts_as_no_pull_request()
    {
        var batch = Batch((Pull("o/r", 1), 3m), (Pull("o/r", 2), 2m), (null, 1m));
        var log = Log(Run("b", "driven", "o/r", 7m, 1, [batch]));
        var row = (await BuildAsync(log, Hub())).Windows[0].Rows.Single();

        Assert.Equal((2, 1, 1), (row.PullRequests, row.Merged, row.ClosedUnmerged));
        Assert.Equal(2m, row.ClosedUsd);
        Assert.Equal(2m + 2m, row.WastedUsd);   // the closed task, plus the task with no pull request (1) and the batch's own overhead (1)
        Assert.Equal(7m, row.UsdPerMerged);
    }

    [Fact]
    public async Task A_run_without_task_costs_splits_evenly_over_its_pull_requests()
    {
        var log = Log(Run("b", "driven", "o/r", 6m, 1, [Batch((Pull("o/r", 1), null), (Pull("o/r", 2), null))]));
        var row = (await BuildAsync(log, Hub())).Windows[0].Rows.Single();
        Assert.Equal(3m, row.ClosedUsd);
    }

    [Fact]
    public async Task Only_finished_top_level_runs_count_and_an_account_sees_its_own()
    {
        var child = Run("c", "driven", "o/r", 5m, 1, parent: "a");
        var log = Log(Run("a", "driven", "o/r", 4m, 1), child, Run("z", "driven", "x/y", 8m, 1));
        var report = await BuildAsync(log, Hub(), include: s => s.Request.Context.Repository!.Path == "o/r");

        Assert.Equal(4m, report.Windows[0].Total.Usd);
        Assert.Equal(1, report.Windows[0].Total.Runs);
    }

    [Fact]
    public async Task A_forge_that_cannot_be_read_leaves_the_state_unknown_and_is_asked_again_next_time()
    {
        var hub = new FakeGitHub { Down = new HttpRequestException("down") };
        var states = new ForgePullRequestStates(_ => hub);
        var log = Log(Run("a", "driven", "o/r", 4m, 1, [PullRequest(Pull("o/r", 1))]));

        var row = (await CostReportBuilder.BuildAsync(log, states, Now)).Windows[0].Rows.Single();
        Assert.Equal((1, 1, 0), (row.PullRequests, row.Unknown, row.Merged));
        Assert.Null(row.UsdPerMerged);

        hub.Down = null;
        hub.Pulls[1] = (true, false);
        Assert.Equal(1, (await CostReportBuilder.BuildAsync(log, states, Now)).Windows[0].Total.Merged);
    }

    [Fact]
    public async Task A_resolved_pull_request_is_read_once_and_an_open_one_again_after_its_time()
    {
        var hub = Hub();
        var clock = new MovableClock(Now);
        var states = new ForgePullRequestStates(_ => hub, openFor: TimeSpan.FromMinutes(10), clock: clock);
        var log = Log(Run("a", "driven", "o/r", 1m, 1, [PullRequest(Pull("o/r", 1))]), Run("b", "driven", "o/r", 1m, 1, [PullRequest(Pull("o/r", 3))]));

        await CostReportBuilder.BuildAsync(log, states, Now);
        await CostReportBuilder.BuildAsync(log, states, Now);
        Assert.Equal(2, hub.Reads.Count);

        clock.Now = Now.AddMinutes(11);
        hub.Pulls[3] = (true, false);
        Assert.Equal(2, (await CostReportBuilder.BuildAsync(log, states, Now)).Windows[0].Total.Merged);
        Assert.Equal(3, hub.Reads.Count);
    }

    [Fact]
    public async Task Recorded_outcomes_stand_in_when_there_is_no_forge()
    {
        var recorded = new FakeRecorded { [Key(1)] = PullRequestState.Merged };
        var states = new ForgePullRequestStates(_ => null, recorded);
        var log = Log(Run("a", "driven", "o/r", 2m, 1, [PullRequest(Pull("o/r", 1))]));
        Assert.Equal(2m, (await CostReportBuilder.BuildAsync(log, states, Now)).Windows[0].Total.UsdPerMerged);
    }

    [Theory]
    [InlineData("https://github.com/o/r/pull/12", "o", "r", 12)]
    [InlineData("https://github.com/o/r/pull/12/files?x=1", "o", "r", 12)]
    public void Pull_request_urls_are_read(string url, string owner, string name, int number) =>
        Assert.Equal(new PullRequestKey(new(owner, name), number), PullRequestKey.Parse(url));

    [Theory]
    [InlineData("https://github.com/o/r/issues/12")]
    [InlineData("https://example.com/o/r/pull/1")]
    [InlineData(null)]
    public void Other_urls_name_no_pull_request(string? url) => Assert.Null(PullRequestKey.Parse(url));

    [Fact]
    public void A_gitlab_merge_request_url_names_its_project_and_host() =>
        Assert.Equal(new PullRequestKey(new("g/sub", "p", Chargehand.Modules.Forge.Api.ForgeKind.GitLab, "gitlab.example.com"), 4), PullRequestKey.Parse("https://gitlab.example.com/g/sub/p/-/merge_requests/4"));

    private static PullRequestKey Key(int n) => new(new("o", "r"), n);

    private sealed class FakeRecorded : Dictionary<PullRequestKey, PullRequestState>, IPullRequestStates
    {
        public Task<PullRequestState> StateOfAsync(PullRequestKey pull, CancellationToken ct) => Task.FromResult(TryGetValue(pull, out var s) ? s : PullRequestState.Unknown);
    }

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
