using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Handover.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0051: a task that finished with its branch pushed is continued by a later batch with the reviewers' comments as its answers. The branch is read from the remote, not from an output
/// volume; the sessions are scripted by the fake output volume and the branch seeder is faked here (<see cref="GitBranchSeederTests"/> runs git).</summary>
public class DrivenReviewRoundTests
{
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";
    private const string First = "run-batch-first";
    private const string Round = "run-batch-round";

    internal sealed class FakeBrancher : IBranchSeeder
    {
        public List<BranchSeedRequest> Requests { get; } = [];

        public Func<BranchSeedRequest, BranchSeedResult> Script { get; set; } = _ => BranchSeedResult.Ok(3);

        public Task<BranchSeedResult> BuildAsync(BranchSeedRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            var made = Script(request);
            if (made.Made)
                File.WriteAllText(request.OutBundle, "branch");
            return Task.FromResult(made);
        }
    }

    private static string Token(TaskGrant g) => $"tok-{g.RunId}";

    private static RunRequest Request(DrivenTask task) =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([task], MaxParallel: 1, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

    private static RunRequest Review(params string[] comments) =>
        Request(new DrivenTask("pr-9", Answers: comments.Length == 0 ? null : comments, ContinueFrom: new ContinueFrom(First, "t1")));

    private static JsonElement Row(ResultContract result) =>
        JsonDocument.Parse(result.Artifacts.Single(a => a.Kind == "driven-batch").Content!).RootElement.GetProperty("tasks").EnumerateArray().Single();

    private static async Task<DrivenRunTests.Rig> Finished(FakeBrancher brancher, Func<string, string, SessionOutcome?>? outcomes = null)
    {
        var rig = new DrivenRunTests.Rig(brancher: brancher, outcomes: outcomes);
        var first = await rig.Run.RunAsync(Request(new DrivenTask("t1", Goal: "Add a retry")), First, Token, default);
        Assert.Equal(ResultStatus.Completed, first.Status);
        return rig;
    }

    [Fact]
    public async Task A_finished_task_is_continued_from_its_pushed_branch_with_the_comments_as_the_answers_and_its_pull_request_is_left_as_it_is()
    {
        var brancher = new FakeBrancher();
        using var rig = await Finished(brancher);
        var firstRun = rig.Engine.Started[0].RunId;

        var result = await rig.Run.RunAsync(Review("src/Retry.cs line 12, alice: use a constant", "bob: add a test"), Round, Token, default);

        Assert.Equal(ResultStatus.Completed, result.Status);
        var secondRun = rig.Engine.Started[1].RunId;
        // The seed is made from the remote, on the first task's branch, at the commit the first task started at.
        var seed = Assert.Single(brancher.Requests);
        Assert.Equal(($"chargehand/{firstRun}", Commit, "https://github.com/o/r.git", false), (seed.Branch, seed.BaseCommit, seed.RemoteUrl, seed.AllowLocalRemote));
        Assert.Equal("value-of-secret-push", seed.Credential!.Value);
        Assert.Equal(seed.OutBundle, Assert.Single(rig.Volumes.Seeds).Path);
        Assert.Equal(secondRun, rig.Volumes.Seeds[0].RunId);
        var task = rig.Volumes.Tasks[1];
        Assert.Equal($"chargehand/{firstRun}", task.Branch);
        Assert.StartsWith("Add a retry\n\nThis task is finished: its branch is pushed and a pull request is open for it. A reviewer has asked for changes.\n\nThe review comments to address:\n"
            + "- src/Retry.cs line 12, alice: use a constant\n- bob: add a test\n\nThe finished work is on this branch", task.Goal);
        Assert.Contains($"git diff {Commit}..HEAD", task.Goal);
        // The push goes to the same branch, and the pull request that is there keeps its title and body.
        var handover = Assert.Single(rig.Handover.Inputs, i => i.RunId == secondRun);
        Assert.Equal(($"chargehand/{firstRun}", false, false), (handover.Branch, handover.UpdateReused, handover.Partial));
        Assert.Equal("completed", Row(result).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_continuation_of_a_task_that_asked_still_uses_the_kept_bundle_and_never_the_remote()
    {
        var brancher = new FakeBrancher();
        using var rig = new DrivenRunTests.Rig(brancher: brancher, outcomes: (_, goal) => goal.Contains("The answers from the person", StringComparison.Ordinal)
            ? new SessionOutcome(SessionStatus.Completed, "", 30, 1000, 200, 4000, null, 0, [], true, Commits: 4)
            : new SessionOutcome(SessionStatus.NeedsInput, "", 5, 1000, 200, 0, null, 0, ["Which database?"], true, Commits: 2));
        await rig.Run.RunAsync(Request(new DrivenTask("t1", Goal: "Add a retry")), First, Token, default);
        var result = await rig.Run.RunAsync(Review("Postgres"), Round, Token, default);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Empty(brancher.Requests);
    }

    [Fact]
    public async Task A_finished_task_continued_without_comments_is_refused_before_anything_starts()
    {
        var brancher = new FakeBrancher();
        using var rig = await Finished(brancher);
        var result = await rig.Run.RunAsync(Review(), Round, Token, default);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Equal(ErrorCode.InvalidRequest, result.Error!.Code);
        Assert.Contains("give answers, the review comments to address", result.Error.Message);
        Assert.Single(rig.Engine.Started);
        Assert.Empty(brancher.Requests);
    }

    [Fact]
    public async Task A_round_at_another_commit_than_the_task_started_at_is_refused()
    {
        var brancher = new FakeBrancher();
        using var rig = await Finished(brancher);
        rig.CheckoutCommit = "fffffff0000000fffffff0000000fffffff00000";
        var result = await rig.Run.RunAsync(Review("fix it"), Round, Token, default);

        Assert.Equal(ErrorCode.InvalidRequest, result.Error!.Code);
        Assert.Contains($"started at commit {Commit}, but this request's repository is at fffffff", result.Error.Message);
        Assert.Empty(brancher.Requests);
    }

    [Fact]
    public async Task A_branch_that_cannot_be_fetched_fails_the_task_before_a_container_starts_and_says_why()
    {
        var brancher = new FakeBrancher();
        using var rig = await Finished(brancher);
        brancher.Script = _ => new BranchSeedResult(false, "the remote has no usable chargehand/x: fatal: couldn't find remote ref", 0);
        var result = await rig.Run.RunAsync(Review("fix it"), Round, Token, default);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Equal("failed", Row(result).GetProperty("status").GetString());
        Assert.Contains("could not start from the pushed branch", Row(result).GetProperty("detail").GetString());
        Assert.Single(rig.Engine.Started);
    }

    [Fact]
    public async Task A_task_that_failed_without_keeping_work_still_cannot_be_continued()
    {
        using var rig = new DrivenRunTests.Rig(script: _ => SessionStatus.Failed);
        await rig.Run.RunAsync(Request(new DrivenTask("t1", Goal: "Add a retry")), First, Token, default);
        var result = await rig.Run.RunAsync(Review("fix it"), Round, Token, default);

        Assert.Equal(ErrorCode.InvalidRequest, result.Error!.Code);
        Assert.Contains("a completed one with a pushed branch can continue", result.Error.Message);
    }
}
