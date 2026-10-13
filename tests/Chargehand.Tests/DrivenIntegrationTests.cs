using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary>ADR 0046 in a batch: a finished task's pull request is marked ready and merged by policy on an opted-in repository, a CI failure starts a fix session on the same branch, and a repository
/// that is not opted in is not touched. The sessions are scripted by the fake output volume and GitHub by <see cref="FakeIntegrationHost"/>; nothing starts a container or reaches a network.</summary>
public class DrivenIntegrationTests
{
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";
    private const string BatchId = "run-batch-i";

    private static string Token(TaskGrant g) => $"tok-{g.RunId}";

    private static RunRequest One(string goal = "Add a retry") =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([new DrivenTask("t1", Goal: goal)], MaxParallel: 1, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

    private static DrivenIntegrator Config(string mode = "merge", string[]? alwaysAsk = null, string repository = "o/r") =>
        new(1, 2, "app-key", new Dictionary<string, string> { [repository] = mode }, alwaysAsk);

    private static SessionOutcome Done(int commits) => new(SessionStatus.Completed, "", 30, 1000, 200, 4000, null, 0, [], true, Commits: commits);

    private static JsonElement Row(ResultContract result) =>
        JsonDocument.Parse(result.Artifacts.Single(a => a.Kind == "driven-batch").Content!).RootElement.GetProperty("tasks").EnumerateArray().Single();

    private static FakeIntegrationHost Host(bool mergedOnceEnabled = true)
    {
        var host = new FakeIntegrationHost();
        host.Script = h => h.Calls.Any(c => c.StartsWith("auto-merge", StringComparison.Ordinal)) && mergedOnceEnabled ? h.Current with { Merged = true } : h.Current;
        return host;
    }

    [Fact]
    public async Task A_finished_task_on_a_merge_repository_is_marked_ready_and_auto_merged_and_the_run_says_so()
    {
        var host = Host();
        using var rig = new DrivenRunTests.Rig(integrator: Config(), integration: host);
        List<RunStatus> events = [];

        var result = await rig.Run.RunAsync(One(), BatchId, Token, default, events.Add);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal(["get", "behind main " + rig.Handover.Inputs[0].Branch, "ready", "get", "auto-merge aaaaaaa1", "get"], host.Calls);
        var told = events.Where(e => e.Event == RunEventKind.Integrated).Select(e => e.Detail).ToList();
        Assert.Equal("task t1: marked ready for review", told[0]);
        Assert.Contains(told, d => d!.StartsWith("task t1: auto-merge (squash) enabled for aaaaaaa;", StringComparison.Ordinal));
        Assert.Equal("task t1: merged", told[^1]);
        Assert.All(events.Where(e => e.Event == RunEventKind.Integrated), e => Assert.Equal("t1", e.TaskId));
        var row = Row(result);
        Assert.Equal("completed", row.GetProperty("status").GetString());
        Assert.StartsWith("integration: auto-merge (squash) enabled", row.GetProperty("detail").GetString());
        Assert.EndsWith("; merged", row.GetProperty("detail").GetString());
        // The pull request body says what happens to it.
        Assert.Contains("enable auto-merge", rig.Handover.Inputs[0].PolicyNote);
        // A finished task replaces the title and body of a pull request an earlier, partial one left for the branch.
        Assert.True(rig.Handover.Inputs[0].UpdateReused);
    }

    [Fact]
    public async Task A_repository_that_is_not_opted_in_is_not_touched_and_its_body_stays_draft_only()
    {
        var host = Host();
        using var rig = new DrivenRunTests.Rig(integrator: Config(repository: "o/other"), integration: host);

        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Empty(host.Calls);
        Assert.Null(rig.Handover.Inputs[0].PolicyNote);
        Assert.Equal(JsonValueKind.Null, Row(result).GetProperty("detail").ValueKind);
    }

    [Fact]
    public async Task With_no_integrator_block_nothing_changes_even_if_a_host_is_supplied()
    {
        var host = Host();
        using var rig = new DrivenRunTests.Rig(integration: host);

        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Empty(host.Calls);
        Assert.Null(rig.Handover.Inputs[0].PolicyNote);
        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.DoesNotContain(rig.Engine.Calls, c => c.Contains("fix", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_partial_draft_is_never_touched_even_on_a_merge_repository()
    {
        var host = Host();
        using var rig = new DrivenRunTests.Rig(integrator: Config(), integration: host,
            outcomes: (_, _) => new SessionOutcome(SessionStatus.TokenCap, "token_cap", 80, 1000, 200, 4000, null, 0, [], true, Commits: 3));

        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.True(Assert.Single(rig.Handover.Inputs).Partial);
        Assert.False(rig.Handover.Inputs[0].UpdateReused);
        Assert.Null(rig.Handover.Inputs[0].PolicyNote);
        Assert.Empty(host.Calls);
        Assert.Equal("failed", Row(result).GetProperty("status").GetString());
        Assert.Contains("integration: partial result: the pull request stays a draft and is never merged", Row(result).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task An_always_ask_repository_is_marked_ready_and_commented_on_and_never_merged()
    {
        var host = new FakeIntegrationHost();
        host.Script = h => h.Current with { Checks = [new CheckStatus("build", true, CheckOutcome.Passed, 900)] };
        using var rig = new DrivenRunTests.Rig(integrator: Config("merge", ["o/r"]), integration: host);

        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Contains("ready", host.Calls);
        Assert.Contains("comment", host.Calls);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("auto-merge", StringComparison.Ordinal) || c.StartsWith("merge ", StringComparison.Ordinal));
        Assert.Contains("left for a person", Row(result).GetProperty("detail").GetString());
        Assert.Contains("asks first", rig.Handover.Inputs[0].PolicyNote);
    }

    [Fact]
    public async Task A_GitHub_error_does_not_fail_a_task_whose_pull_request_is_open()
    {
        var host = new FakeIntegrationHost { ReadFails = new IntegrationException("GitHub answered 502") };
        using var rig = new DrivenRunTests.Rig(integrator: Config(), integration: host);

        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Contains("integration stopped: GitHub answered 502", Row(result).GetProperty("detail").GetString());
    }

    /// <summary>GitHub answers 404 to every installation lookup: the App is installed on no owner.</summary>
    private sealed class NoInstallation : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"Not Found"}""") });
        }
    }

    [Fact]
    public async Task A_repository_the_app_is_not_installed_on_is_said_so_and_the_task_stays_completed_with_its_draft()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var handler = new NoInstallation();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        using var tokens = new GitHubInstallationTokens(http, 1, null, () => rsa.ExportRSAPrivateKeyPem(), TimeProvider.System);
        using var rig = new DrivenRunTests.Rig(integrator: Config(), integration: new GitHubIntegrationHost(http, tokens));

        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Equal(ResultStatus.Completed, result.Status);
        var detail = Row(result).GetProperty("detail").GetString();
        Assert.Contains("integration stopped: the GitHub App is not installed on o", detail);
        Assert.DoesNotContain("Resource not accessible", detail);
        Assert.Equal(["/repos/o/r/installation"], handler.Paths);
        Assert.Single(rig.Handover.Inputs);
    }

    [Fact]
    public async Task A_failing_required_check_starts_a_fix_session_on_the_same_branch_seeded_from_the_last_bundle_and_its_push_is_the_second_handover()
    {
        var host = new FakeIntegrationHost();
        host.Logs[900] = "FAILED Retry.Tests: expected 3 but got 2";
        using var rig = new DrivenRunTests.Rig(integrator: Config(), integration: host,
            outcomes: (_, goal) => goal.StartsWith("Fix the failing CI checks", StringComparison.Ordinal) ? Done(5) : Done(4));
        // The fix is "pushed" when the second handover has run; the pretend CI then passes and GitHub merges.
        host.Script = h =>
        {
            var fixedOnce = rig.Handover.Inputs.Count >= 2;
            return h.Current with { HeadSha = fixedOnce ? "head1" : "head0", Merged = fixedOnce, Checks = [new CheckStatus("build", true, fixedOnce ? CheckOutcome.Passed : CheckOutcome.Failed, 900)] };
        };
        List<RunStatus> events = [];

        var result = await rig.Run.RunAsync(One(), BatchId, Token, default, events.Add);

        Assert.Equal(ResultStatus.Completed, result.Status);
        var (first, second) = (rig.Engine.Started[0].RunId, rig.Engine.Started[1].RunId);
        Assert.Equal(2, rig.Engine.Started.Count);
        var task = rig.Volumes.Tasks[1];
        Assert.Equal($"chargehand/{first}", task.Branch);
        Assert.StartsWith($"Fix the failing CI checks on branch chargehand/{first}. This is fix attempt 1 of 3", task.Goal);
        Assert.Contains("Add a retry", task.Goal);
        Assert.Contains("- build\n```text\nFAILED Retry.Tests: expected 3 but got 2\n```", task.Goal);
        Assert.Equal((20, 15), (task.MaxTurns, task.MaxMinutes));
        var seed = Assert.Single(rig.Volumes.Seeds);
        Assert.Equal(second, seed.RunId);
        Assert.EndsWith(Path.Combine("out", first, "chargehand.bundle"), seed.Path);
        // The fix goes through the same handover: its own tests, the same branch, not partial, the work of the fix session.
        Assert.Equal(2, rig.Handover.Inputs.Count);
        Assert.Equal((rig.Handover.Inputs[0].Branch, second, false), (rig.Handover.Inputs[1].Branch, rig.Handover.Inputs[1].RunId, rig.Handover.Inputs[1].Partial));
        // The attempt is on the pull request, and the fix session's tokens are the task's.
        Assert.Contains(host.Comments, c => c.Contains("chargehand-ci-fix attempt=1", StringComparison.Ordinal));
        var row = Row(result);
        Assert.Equal(2400, row.GetProperty("tokens").GetInt64());
        Assert.Contains("(1 CI fix attempt(s) pushed)", row.GetProperty("detail").GetString());
        Assert.Contains(events, e => e.Event == RunEventKind.ContainerStarted && e.Detail == "task t1: session container started (CI fix attempt 1 of 3)");
        Assert.Single(events, e => e.Event == RunEventKind.PrOpened);
        // Both sessions' containers are cleaned up.
        Assert.Contains($"rm cid-{first}", rig.Engine.Calls);
        Assert.Contains($"rm cid-{second}", rig.Engine.Calls);
    }

    [Fact]
    public async Task A_fix_session_that_ends_without_a_commit_pushes_nothing_and_the_task_still_completes()
    {
        var host = new FakeIntegrationHost();
        host.Script = h => h.Current with { Checks = [new CheckStatus("build", true, CheckOutcome.Failed, 900)] };
        using var rig = new DrivenRunTests.Rig(integrator: Config(), integration: host,
            outcomes: (_, _) => Done(4));

        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal(2, rig.Engine.Started.Count);
        Assert.Single(rig.Handover.Inputs);
        Assert.Contains("pushed nothing: the session made no new commit", Row(result).GetProperty("detail").GetString());
    }

    /// <summary>A GitLab as the batch sees it: one object for opening and integrating, backed by the fake host.</summary>
    private sealed class FakeForge(FakeIntegrationHost inner) : IForge
    {
        public string Name => "GitLab";

        public Task<PullRequestRef> CreateDraftAsync(RemoteRepository repo, string head, string @base, string title, string body, CancellationToken ct) => throw new NotSupportedException("the handover is faked");

        public Task EditAsync(RemoteRepository repo, int number, string title, string body, CancellationToken ct) => throw new NotSupportedException("the handover is faked");

        public Task<PullRequestRef?> FindByHeadAsync(RemoteRepository repo, string head, CancellationToken ct) => throw new NotSupportedException("the handover is faked");

        public Task<PrSnapshot> GetAsync(RemoteRepository repository, int number, CancellationToken ct) => inner.GetAsync(repository, number, ct);

        public Task<int> BehindByAsync(RemoteRepository repository, string baseBranch, string branch, CancellationToken ct) => inner.BehindByAsync(repository, baseBranch, branch, ct);

        public Task UpdateBranchAsync(RemoteRepository repository, int number, string expectedHeadSha, CancellationToken ct) => inner.UpdateBranchAsync(repository, number, expectedHeadSha, ct);

        public Task MarkReadyAsync(RemoteRepository repository, string nodeId, CancellationToken ct) => inner.MarkReadyAsync(repository, nodeId, ct);

        public Task EnableAutoMergeAsync(RemoteRepository repository, string nodeId, string headSha, CancellationToken ct) => inner.EnableAutoMergeAsync(repository, nodeId, headSha, ct);

        public Task MergeNowAsync(RemoteRepository repository, int number, string headSha, CancellationToken ct) => inner.MergeNowAsync(repository, number, headSha, ct);

        public Task CommentAsync(RemoteRepository repository, int number, string body, CancellationToken ct) => inner.CommentAsync(repository, number, body, ct);

        public Task<string> JobLogTailAsync(RemoteRepository repository, long jobId, int maxChars, CancellationToken ct) => inner.JobLogTailAsync(repository, jobId, maxChars, ct);
    }

    private const string GitLabRemote = "git@gitlab.example.test:group/sub/project.git";

    [Fact]
    public async Task A_gitlab_remote_is_integrated_through_the_forge_the_server_builds_for_it_with_the_push_credential()
    {
        var host = Host();
        List<(RemoteRepository Remote, string Token)> built = [];
        using var rig = new DrivenRunTests.Rig(integrator: Config(repository: "group/sub/project"), remote: GitLabRemote, forgeFor: (remote, token) =>
        {
            built.Add((remote, token));
            return new FakeForge(host);
        });

        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal([(new RemoteRepository("group/sub", "project", ForgeKind.GitLab, "gitlab.example.test"), "value-of-secret-push")], built);
        Assert.Contains("ready", host.Calls);
        Assert.Contains("auto-merge aaaaaaa1", host.Calls);
        Assert.Contains("GitLab merges it only once every required check and rule passes", rig.Handover.Inputs[0].PolicyNote);
        Assert.EndsWith("; merged", Row(result).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task The_forge_token_secret_replaces_the_push_credential_for_the_forge()
    {
        List<string> tokens = [];
        using var rig = new DrivenRunTests.Rig(integrator: Config(repository: "o/r"), forge: new DrivenForge("gitlab", TokenSecret: "gitlab-api-token"), forgeFor: (_, token) =>
        {
            tokens.Add(token);
            return new FakeForge(Host());
        });

        await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Equal(["value-of-gitlab-api-token"], tokens);
    }

    [Fact]
    public async Task A_gitlab_remote_with_no_forge_set_up_is_refused_before_any_container_exists()
    {
        using var rig = new DrivenRunTests.Rig(remote: GitLabRemote);

        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Equal(ErrorCode.InvalidRequest, result.Error!.Code);
        Assert.Contains("GitLab", result.Error.Message);
        Assert.Empty(rig.Engine.Calls);
    }

    [Fact]
    public async Task A_github_remote_never_asks_for_another_forge()
    {
        var host = Host();
        using var rig = new DrivenRunTests.Rig(integrator: Config(), integration: host, forgeFor: (_, _) => throw new InvalidOperationException("not for GitHub"));

        var result = await rig.Run.RunAsync(One(), BatchId, Token, default);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Contains("auto-merge aaaaaaa1", host.Calls);
        Assert.Contains("GitHub merges it only once", rig.Handover.Inputs[0].PolicyNote);
    }
}
