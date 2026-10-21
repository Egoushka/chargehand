using System.Net;
using Chargehand.Modules.Forge.Api;

namespace Chargehand.Modules.Forge.Tests;

/// <summary>CHARGEHAND-197: flaky jobs from CI history. A job is a flake when an attempt failed and a later one passed on the same commit; the readers list the attempts of GitHub Actions and GitLab CI.</summary>
public class CiHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private static CiJob Job(long id, CheckOutcome outcome, int minutes, string sha = "abc", string name = "test", string workflow = "ci") =>
        new(id, workflow, name, sha, "main", null, outcome, T0.AddMinutes(minutes), null);

    [Fact]
    public void A_job_that_failed_and_then_passed_on_the_same_commit_is_a_flake()
    {
        var flake = Assert.Single(CiFlakes.Find([Job(1, CheckOutcome.Failed, 0), Job(2, CheckOutcome.Passed, 5), Job(3, CheckOutcome.Passed, 0, name: "lint")]));
        Assert.Equal((1L, 2L), (flake.Failed.Id, flake.Passed.Id));
    }

    [Fact]
    public void A_pass_before_the_failure_a_failure_on_another_commit_or_a_cancelled_attempt_is_not()
    {
        Assert.Empty(CiFlakes.Find([Job(1, CheckOutcome.Passed, 0), Job(2, CheckOutcome.Failed, 5)]));
        Assert.Empty(CiFlakes.Find([Job(1, CheckOutcome.Failed, 0, sha: "abc"), Job(2, CheckOutcome.Passed, 5, sha: "def")]));
        Assert.Empty(CiFlakes.Find([Job(1, CheckOutcome.Other, 0), Job(2, CheckOutcome.Passed, 5)]));
    }

    [Fact]
    public void The_last_failure_before_the_pass_is_kept_and_flakes_come_newest_first()
    {
        var flakes = CiFlakes.Find([Job(1, CheckOutcome.Failed, 0), Job(2, CheckOutcome.Failed, 3), Job(3, CheckOutcome.Passed, 6), Job(4, CheckOutcome.Failed, 60, sha: "def"), Job(5, CheckOutcome.Passed, 70, sha: "def")]);
        Assert.Equal([(4L, 5L), (2L, 3L)], flakes.Select(f => (f.Failed.Id, f.Passed.Id)).ToArray());
    }

    [Theory]
    [InlineData("  Failed Chargehand.Tests.RetryTests.Retries_twice [12 ms]", "Chargehand.Tests.RetryTests.Retries_twice")]
    [InlineData("--- FAIL: TestRetry (0.01s)", "TestRetry")]
    [InlineData("FAILED tests/test_retry.py::test_twice - AssertionError", "tests/test_retry.py::test_twice")]
    [InlineData("  ● Retry › retries twice", "Retry › retries twice")]
    [InlineData("[ERROR]   RetryTest.retriesTwice:42 expected:<2> but was:<1>", "RetryTest.retriesTwice")]
    [InlineData("test retry::twice ... FAILED", "retry::twice")]
    public void The_failed_test_names_of_the_common_runners_are_read_from_a_log(string line, string name) =>
        Assert.Equal([name], CiFlakes.FailedTests($"build ok\n{line}\nmore output"));

    [Fact]
    public void A_log_without_a_test_failure_names_none_and_names_repeat_once() =>
        Assert.Equal((0, 1), (CiFlakes.FailedTests("Error: the runner lost its connection").Count, CiFlakes.FailedTests("--- FAIL: TestA\n--- FAIL: TestA").Count));

    private sealed class FixedToken : IInstallationTokens
    {
        public Task<string> GetAsync(string owner, string repository, CancellationToken ct) => Task.FromResult("ghs_secrettoken");
    }

    private sealed class Routes(Func<string, string> answer) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(request.RequestUri.PathAndQuery)) });
        }
    }

    // Run 11 was re-run once (attempt 1 failed, attempt 2 passed); 12 and 13 are two runs of one workflow on one commit that ended differently; 14 passed on its own and is not read further.
    private const string GitHubRuns = """
        {"workflow_runs":[
          {"id":11,"workflow_id":1,"name":"ci","head_sha":"aaa","head_branch":"main","status":"completed","conclusion":"success","run_attempt":2,"pull_requests":[]},
          {"id":12,"workflow_id":1,"name":"ci","head_sha":"bbb","head_branch":"fix","status":"completed","conclusion":"failure","run_attempt":1,"pull_requests":[{"number":7}]},
          {"id":13,"workflow_id":1,"name":"ci","head_sha":"bbb","head_branch":"fix","status":"completed","conclusion":"success","run_attempt":1,"pull_requests":[{"number":7}]},
          {"id":14,"workflow_id":1,"name":"ci","head_sha":"ccc","head_branch":"main","status":"completed","conclusion":"success","run_attempt":1,"pull_requests":[]},
          {"id":15,"workflow_id":1,"name":"ci","head_sha":"ddd","head_branch":"main","status":"in_progress","conclusion":null,"run_attempt":3,"pull_requests":[]}]}
        """;

    private static string GitHubJobs(string path) => path switch
    {
        _ when path.Contains("/runs/11/attempts/1/") => """{"jobs":[{"id":101,"name":"test","head_sha":"aaa","status":"completed","conclusion":"failure","completed_at":"2026-10-01T10:00:00Z","html_url":"https://example.test/j/101"}]}""",
        _ when path.Contains("/runs/11/attempts/2/") => """{"jobs":[{"id":102,"name":"test","head_sha":"aaa","status":"completed","conclusion":"success","completed_at":"2026-10-01T10:30:00Z"}]}""",
        _ when path.Contains("/runs/12/") => """{"jobs":[{"id":121,"name":"test","head_sha":"bbb","status":"completed","conclusion":"failure","completed_at":"2026-10-02T10:00:00Z"}]}""",
        _ when path.Contains("/runs/13/") => """{"jobs":[{"id":131,"name":"test","head_sha":"bbb","status":"completed","conclusion":"success","completed_at":"2026-10-02T11:00:00Z"},{"id":132,"name":"lint","status":"in_progress","conclusion":null}]}""",
        _ => GitHubRuns,
    };

    [Fact]
    public async Task GitHub_reads_the_attempts_of_rerun_runs_and_of_commits_whose_runs_disagree_and_nothing_else()
    {
        var handler = new Routes(GitHubJobs);
        var host = new GitHubIntegrationHost(new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") }, new FixedToken());

        var jobs = await host.RecentJobsAsync(new RemoteRepository("o", "r"), T0.AddDays(-3), default);

        Assert.Equal("/repos/o/r/actions/runs?per_page=100&created=%3E%3D2026-09-28", handler.Paths[0]);
        Assert.Equal(["/repos/o/r/actions/runs/13/attempts/1/jobs?per_page=100", "/repos/o/r/actions/runs/12/attempts/1/jobs?per_page=100",
            "/repos/o/r/actions/runs/11/attempts/1/jobs?per_page=100", "/repos/o/r/actions/runs/11/attempts/2/jobs?per_page=100"], handler.Paths.Skip(1).ToArray());
        var flakes = CiFlakes.Find(jobs);
        Assert.Equal([(121L, 131L, 7), (101L, 102L, (int?)null)], flakes.Select(f => (f.Failed.Id, f.Passed.Id, f.Failed.PullNumber)).ToArray());
        Assert.Equal(("ci", "main", "https://example.test/j/101"), (flakes[1].Failed.Workflow, flakes[1].Failed.Ref, flakes[1].Failed.Url));
    }

    [Fact]
    public async Task GitLab_reads_each_pipelines_jobs_with_the_retried_ones_and_names_a_merge_request_from_its_ref()
    {
        var handler = new Routes(path => path.Contains("/pipelines/9/jobs", StringComparison.Ordinal)
            ? """[{"id":91,"name":"unit","stage":"test","status":"failed","finished_at":"2026-10-01T10:00:00Z","web_url":"https://gitlab.example.test/j/91"},{"id":92,"name":"unit","stage":"test","status":"success","finished_at":"2026-10-01T10:20:00Z"},{"id":93,"name":"deploy","stage":"ship","status":"manual","finished_at":null}]"""
            : path.Contains("/jobs", StringComparison.Ordinal) ? "[]"
            : """[{"id":9,"sha":"aaa","ref":"refs/merge-requests/12/head"},{"id":8,"sha":"bbb","ref":"main"}]""");
        var forge = new GitLabForge(new HttpClient(handler) { BaseAddress = new Uri("https://gitlab.example.test/api/v4/") }, "glpat-secret");

        var jobs = await ((IIntegrationHost)forge).RecentJobsAsync(new RemoteRepository("group", "project", ForgeKind.GitLab, "gitlab.example.test"), T0, default);

        Assert.Equal("/api/v4/projects/group%2Fproject/pipelines?updated_after=2026-10-01T10%3A00%3A00Z&order_by=id&sort=desc&per_page=30", handler.Paths[0]);
        Assert.Contains("/api/v4/projects/group%2Fproject/pipelines/9/jobs?include_retried=true&per_page=100", handler.Paths);
        var flake = Assert.Single(CiFlakes.Find(jobs));
        Assert.Equal((91L, 92L, "test", 12, (string?)null), (flake.Failed.Id, flake.Passed.Id, flake.Failed.Workflow, flake.Failed.PullNumber, flake.Failed.Ref));
    }

    [Fact]
    public async Task A_host_without_history_says_so()
    {
        IIntegrationHost host = new NoHistory();
        var e = await Assert.ThrowsAsync<IntegrationException>(() => host.RecentJobsAsync(new RemoteRepository("o", "r"), T0, default));
        Assert.Contains("cannot read CI history", e.Message);
    }

    private sealed class NoHistory : IIntegrationHost
    {
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
