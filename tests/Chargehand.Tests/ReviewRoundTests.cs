using System.Text.Json;
using Chargehand.Automation;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0051: a review comment, or a comment that mentions the bot, by an allowed author on a chargehand pull request starts a continuation of the task that opened it, at most three times per pull
/// request together with the CI fixer, never twice for the same comment. GitHub, the batch starter, the run log and the clock are fakes.</summary>
public class ReviewRoundTests
{
    private const string Branch = "chargehand/task-run";

    private static bool Valid(RunRequest request) => ContractSchemas.Validate(ContractSchemas.Request, JsonSerializer.SerializeToElement(request, ContractJson.Options)).Count == 0;

    private static AutomationGitHub Settings(int concurrent = 2, int perDay = 10, int rounds = 3) =>
        new(new Dictionary<string, AutomationRepository> { [IntakeRig.Repo] = new("/srv/repos/repo") }, [IntakeRig.Author], MaxConcurrent: concurrent, MaxStartsPerDay: perDay, MaxReviewRounds: rounds);

    /// <summary>An issue's task ended in pull request 9 on <see cref="Branch"/>: the first poll fixes the cursors, the batch starts, ends and is reported.</summary>
    private static async Task<IntakeRig> WithPullRequest(AutomationGitHub? settings = null)
    {
        var rig = new IntakeRig(settings ?? Settings());
        await rig.Poll();
        rig.Labeled(7, "alice", 41);
        await rig.Poll();
        rig.Log.TaskRun("task-run", "run-1", Branch);
        rig.Log.End("run-1", IntakeRig.Pr());
        rig.Pull(9);
        await rig.Poll();
        Assert.Equal(IntakePhase.Done, rig.State.Tasks.Single().Phase);
        return rig;
    }

    private static string Round(IntakeRig rig, int n = 0) => rig.GitHub.Posted.Where(p => p.Issue == 9 && p.Body.Contains("chargehand-review-round", StringComparison.Ordinal)).ElementAt(n).Body;

    [Fact]
    public async Task An_inline_review_comment_by_an_allowed_author_continues_the_task_that_opened_the_pull_request()
    {
        using var rig = await WithPullRequest();
        rig.Review(9, "alice", "Use a constant here.\nNot a literal.", "src/Retry.cs", 12);
        await rig.Poll();

        Assert.Equal(2, rig.Starter.Started.Count);
        var request = rig.Starter.Started[1];
        Assert.True(Valid(request));
        var task = Assert.Single(request.Driven!.Tasks);
        Assert.Equal(("pr-9", new ContinueFrom("run-1", "issue-7"), null), (task.Id, task.ContinueFrom, task.Goal));
        Assert.Equal(["review comment by alice on src/Retry.cs line 12: Use a constant here.\n  Not a literal."], task.Answers);
        // A round's number is the pull request's: it closes nothing, and the pull request keeps the body that closes the issue.
        Assert.DoesNotContain("Closes", request.Text);
        // It starts at the commit the first batch started at, whatever the default branch is now.
        Assert.Equal(rig.Starter.Started[0].Context.Repository, request.Context.Repository);
        var announced = Round(rig);
        Assert.Contains("<!-- chargehand-review-round attempt=1 comments=r", announced);
        Assert.Contains("starting review round 1 of 3 (run `run-2`)", announced);
        var round = rig.State.Tasks[1];
        Assert.Equal((9, "run-2", Branch, 1, IntakePhase.Running), (round.Issue, round.BatchRunId, round.Branch, round.Round, round.Phase));
    }

    [Fact]
    public async Task Comments_that_arrive_together_are_one_round_and_a_mention_in_the_conversation_counts()
    {
        using var rig = await WithPullRequest();
        rig.Review(9, "alice", "Rename this.", "src/A.cs", 3);
        rig.Review(9, "alice", "Add a test.", "tests/ATests.cs", null);
        rig.Comment(9, "alice", "@chargehand also update the README");
        await rig.Poll();

        Assert.Equal(2, rig.Starter.Started.Count);
        Assert.Equal(["review comment by alice on src/A.cs line 3: Rename this.", "review comment by alice on tests/ATests.cs: Add a test.", "comment by alice: @chargehand also update the README"],
            rig.Starter.Started[1].Driven!.Tasks.Single().Answers);
        Assert.Equal(3, Round(rig).Split(["comments="], StringSplitOptions.None)[1].Split(' ')[0].Split(',').Length);
    }

    [Fact]
    public async Task A_reply_in_a_thread_needs_the_mention_and_a_conversation_comment_without_it_is_not_a_request()
    {
        using var rig = await WithPullRequest();
        rig.Review(9, "alice", "Thanks, makes sense.", reply: true);
        rig.Comment(9, "alice", "Looks good to me");
        await rig.Poll();
        Assert.Single(rig.Starter.Started);

        rig.Review(9, "alice", "@chargehand please also do the other file", reply: true);
        await rig.Poll();
        Assert.Equal(2, rig.Starter.Started.Count);
    }

    [Fact]
    public async Task Comments_of_anyone_else_a_foreign_branch_a_fork_or_a_closed_pull_request_start_nothing()
    {
        using var rig = await WithPullRequest();
        rig.Pull(10, "feature/retry");
        rig.Pull(11, "chargehand/forked", headRepository: "mallory/repo");
        rig.Pull(12, "chargehand/../main");
        rig.Pull(13, "chargehand/closed", open: false);
        rig.Review(9, "mallory", "Do this instead.");
        rig.Review(10, "alice", "Fix this.");
        rig.Review(11, "alice", "Fix this.");
        rig.Review(12, "alice", "Fix this.");
        rig.Review(13, "alice", "Fix this.");
        await rig.Poll();

        Assert.Single(rig.Starter.Started);
        Assert.Empty(rig.State.Pending);
    }

    [Fact]
    public async Task A_comment_on_a_pull_request_that_closed_while_it_waited_is_dropped()
    {
        using var rig = await WithPullRequest(Settings(concurrent: 1));
        rig.Labeled(8, "alice", 42);
        await rig.Poll();
        rig.Review(9, "alice", "Fix this.");
        await rig.Poll();
        Assert.Equal(2, rig.Starter.Started.Count);
        Assert.Single(rig.State.Pending);

        rig.Pull(9, open: false);
        rig.Log.End("run-2", IntakeRig.Pr("issue-8", run: "other"));
        await rig.Poll();

        Assert.Equal(2, rig.Starter.Started.Count);
        Assert.Empty(rig.State.Pending);
    }

    [Fact]
    public async Task A_comment_a_round_was_already_started_for_is_never_taken_again_even_with_the_state_gone()
    {
        using var rig = await WithPullRequest();
        var id = rig.Review(9, "alice", "Fix this.");
        rig.GitHub.Add(9, "chargehand[bot]", $"{FixMarker.ReviewRound(1, [$"r{id}"])}\nchargehand is starting review round 1", rig.Now);
        await rig.Poll();

        Assert.Single(rig.Starter.Started);
        Assert.Empty(rig.State.Pending);
    }

    [Fact]
    public async Task A_restart_does_not_start_a_round_twice()
    {
        using var rig = await WithPullRequest();
        rig.Review(9, "alice", "Fix this.");
        await rig.Poll();
        rig.Restart();
        await rig.Poll();
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await rig.Poll();

        Assert.Equal(2, rig.Starter.Started.Count);
    }

    [Fact]
    public async Task A_comment_from_before_the_first_poll_is_not_a_request()
    {
        using var rig = new IntakeRig(Settings());
        rig.Pull(9);
        rig.Review(9, "alice", "Fix this.");
        rig.Time.Advance(TimeSpan.FromMinutes(10));
        await rig.Poll();

        Assert.Empty(rig.Starter.Started);
        Assert.Empty(rig.State.Pending);
    }

    [Fact]
    public async Task The_cap_of_three_is_shared_with_the_ci_fixer_and_the_pull_request_holds_the_count()
    {
        using var rig = await WithPullRequest();
        rig.GitHub.Add(9, "chargehand[bot]", FixMarker.Attempt(new FixAttempt(1, CiFixPolicy.JobsKey(["build"]))), rig.Now);
        rig.GitHub.Add(9, "chargehand[bot]", FixMarker.Attempt(new FixAttempt(2, CiFixPolicy.JobsKey(["lint"]))), rig.Now);

        rig.Review(9, "alice", "First.");
        await rig.Poll();
        Assert.Contains("attempt=3", Round(rig));
        Assert.Contains("starting review round 3 of 3", Round(rig));
        rig.Log.End("run-2", IntakeRig.Pr("pr-9", run: "round-run"));
        await rig.Poll();

        rig.Review(9, "alice", "Second.");
        await rig.Poll();
        await rig.Poll();

        Assert.Equal(2, rig.Starter.Started.Count);
        Assert.Single(rig.GitHub.Posted, p => p.Issue == 9 && p.Body.Contains("not starting another round", StringComparison.Ordinal));
        Assert.Empty(rig.State.Pending);
    }

    [Fact]
    public async Task Three_rounds_are_made_and_a_fourth_never_is()
    {
        using var rig = await WithPullRequest();
        for (var n = 1; n <= 3; n++)
        {
            rig.Review(9, "alice", $"Change {n}.");
            await rig.Poll();
            Assert.Equal(n + 1, rig.Starter.Started.Count);
            Assert.Contains($"attempt={n}", Round(rig, n - 1));
            rig.Log.TaskRun($"round-run-{n}", $"run-{n + 1}", Branch);
            rig.Log.End($"run-{n + 1}", IntakeRig.Pr("pr-9", run: $"round-run-{n}"));
            await rig.Poll();
        }

        rig.Review(9, "alice", "Change 4.");
        await rig.Poll();

        Assert.Equal(4, rig.Starter.Started.Count);
        Assert.Contains("3 of 3 attempts", rig.GitHub.Posted.Last().Body);
    }

    [Fact]
    public async Task The_profile_can_lower_the_rounds_or_turn_the_loop_off()
    {
        using (var rig = await WithPullRequest(Settings(rounds: 1)))
        {
            rig.Review(9, "alice", "One.");
            await rig.Poll();
            rig.Log.TaskRun("round-run", "run-2", Branch);
            rig.Log.End("run-2", IntakeRig.Pr("pr-9", run: "round-run"));
            await rig.Poll();
            rig.Review(9, "alice", "Two.");
            await rig.Poll();
            Assert.Equal(2, rig.Starter.Started.Count);
        }

        using var off = await WithPullRequest(Settings(rounds: 0));
        off.Review(9, "alice", "Fix this.");
        off.Comment(9, "alice", "@chargehand fix this");
        await off.Poll();
        Assert.Single(off.Starter.Started);
        Assert.Equal(0, off.GitHub.PullReads);
    }

    [Fact]
    public async Task While_the_batch_of_the_pull_request_is_still_running_the_comments_wait_with_one_comment_and_go_in_one_round_after()
    {
        using var rig = new IntakeRig(Settings());
        await rig.Poll();
        rig.Labeled(7, "alice", 41);
        await rig.Poll();
        // The task's own run is written when its session ends; the batch goes on (the CI fixer) and has no result yet.
        rig.Log.TaskRun("task-run", "run-1", Branch);
        rig.Pull(9);
        rig.Review(9, "alice", "First.");
        await rig.Poll();
        rig.Review(9, "alice", "Second.");
        await rig.Poll();

        Assert.Single(rig.Starter.Started);
        Assert.Single(rig.GitHub.Posted, p => p.Issue == 9 && p.Body.Contains("still working on", StringComparison.Ordinal));

        rig.Log.End("run-1", IntakeRig.Pr());
        await rig.Poll();

        Assert.Equal(2, rig.Starter.Started.Count);
        Assert.Equal(2, rig.Starter.Started[1].Driven!.Tasks.Single().Answers!.Count);
    }

    [Fact]
    public async Task While_a_round_runs_new_comments_wait_for_the_next_round()
    {
        using var rig = await WithPullRequest();
        rig.Review(9, "alice", "First.");
        await rig.Poll();
        rig.Review(9, "alice", "Second.");
        await rig.Poll();
        Assert.Equal(2, rig.Starter.Started.Count);

        rig.Log.TaskRun("round-run", "run-2", Branch);
        rig.Log.End("run-2", IntakeRig.Pr("pr-9", run: "round-run"));
        await rig.Poll();

        Assert.Equal(3, rig.Starter.Started.Count);
        Assert.Equal(["review comment by alice on src/Retry.cs line 12: Second."], rig.Starter.Started[2].Driven!.Tasks.Single().Answers);
        Assert.Equal(("run-2", "pr-9"), (rig.Starter.Started[2].Driven!.Tasks.Single().ContinueFrom!.RunId, rig.Starter.Started[2].Driven!.Tasks.Single().ContinueFrom!.TaskId));
        Assert.Contains("attempt=2", Round(rig, 1));
    }

    [Fact]
    public async Task A_finished_round_says_what_it_did_on_the_pull_request()
    {
        using var rig = await WithPullRequest();
        rig.Review(9, "alice", "Fix this.");
        await rig.Poll();
        rig.Log.TaskRun("round-run", "run-2", Branch, summary: "Replaced the literal with a constant.");
        rig.Log.End("run-2", IntakeRig.Pr("pr-9", run: "round-run"));
        await rig.Poll();

        var report = rig.GitHub.Posted.Last();
        Assert.Equal(9, report.Issue);
        Assert.Contains("finished review round 1 (run `run-2`)", report.Body);
        Assert.Contains("Replaced the literal with a constant.", report.Body);
        Assert.Equal(IntakePhase.Done, rig.State.Tasks[1].Phase);
    }

    [Fact]
    public async Task A_round_that_failed_says_so_on_the_pull_request()
    {
        using var rig = await WithPullRequest();
        rig.Review(9, "alice", "Fix this.");
        await rig.Poll();
        rig.Log.End("run-2", new TaskOutcome("pr-9", TaskState.Failed, "round-run", "could not start from the pushed branch: the remote has no usable chargehand/task-run", ErrorCode.SessionFailed));
        await rig.Poll();

        Assert.Contains("review round 1 (run `run-2`) ended without pushing changes (failed): could not start from the pushed branch", rig.GitHub.Posted.Last().Body);
        Assert.Equal(IntakePhase.Done, rig.State.Tasks[1].Phase);
    }

    [Fact]
    public async Task A_pull_request_that_cannot_be_read_does_not_hold_up_the_others()
    {
        using var rig = await WithPullRequest();
        rig.Pull(10, "chargehand/other-run");
        rig.Review(10, "alice", "Fix this.");
        rig.Review(9, "alice", "And this.");
        // Queued while GitHub answered; gone when the round is looked at.
        rig.GitHub.PullFails = 10;
        await rig.Poll();

        Assert.Equal(2, rig.Starter.Started.Count);
        Assert.Contains(rig.Warnings, w => w.Contains("octo/repo#10", StringComparison.Ordinal));
        Assert.Equal(10, Assert.Single(rig.State.Pending).Pull);
    }

    [Fact]
    public async Task A_round_that_asks_is_answered_by_a_reply_on_the_pull_request()
    {
        using var rig = await WithPullRequest();
        rig.Review(9, "alice", "Make it configurable.");
        await rig.Poll();
        rig.Log.Asked("round-run", "Where should the setting live?");
        rig.Log.End("run-2", new TaskOutcome("pr-9", TaskState.NeedsInput, "round-run", "asked"));
        await rig.Poll();
        Assert.Contains("- Where should the setting live?", rig.GitHub.Posted.Last().Body);

        rig.Comment(9, "alice", "In appsettings.");
        await rig.Poll();

        Assert.Equal(3, rig.Starter.Started.Count);
        var task = rig.Starter.Started[2].Driven!.Tasks.Single();
        Assert.Equal((new ContinueFrom("run-2", "pr-9"), "In appsettings."), (task.ContinueFrom, Assert.Single(task.Answers!)));
        Assert.Equal(1, rig.State.Tasks[^1].Round);
        Assert.DoesNotContain("Closes", rig.Starter.Started[2].Text);
    }

    [Fact]
    public async Task A_pull_request_whose_task_is_not_in_the_run_log_is_said_so_once_and_the_comments_are_dropped()
    {
        using var rig = new IntakeRig(Settings());
        await rig.Poll();
        rig.Pull(9, "chargehand/unknown-run");
        rig.Review(9, "alice", "Fix this.");
        await rig.Poll();
        rig.Review(9, "alice", "And this.");
        await rig.Poll();

        Assert.Empty(rig.Starter.Started);
        Assert.Single(rig.GitHub.Posted, p => p.Issue == 9 && p.Body.Contains("no finished task for this branch", StringComparison.Ordinal));
        Assert.Empty(rig.State.Pending);
    }

    [Fact]
    public async Task A_pull_request_not_opened_through_intake_is_found_in_the_run_log_by_its_branch()
    {
        using var rig = new IntakeRig(Settings());
        await rig.Poll();
        // A batch somebody sent through the API: its task ran in repository /srv/repos/repo at the base commit and pushed this branch.
        var batch = Runs.DraftRequest() with { Context = new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repos/repo", rig.Heads.Commit)) };
        rig.Log.Start("run-api", batch);
        rig.Log.TaskRun("api-task-run", "run-api", "chargehand/api-task-run");
        rig.Log.End("run-api", new TaskOutcome("t1", TaskState.Completed, "api-task-run", null, null, 0, 1, "chargehand/api-task-run", "https://github.example.test/octo/repo/pull/20"));
        rig.Pull(20, "chargehand/api-task-run");
        rig.Review(20, "alice", "Fix this.");
        await rig.Poll();

        var request = Assert.Single(rig.Starter.Started);
        Assert.Equal(new ContinueFrom("run-api", "t1"), request.Driven!.Tasks.Single().ContinueFrom);
        Assert.Equal(batch.Context.Repository, request.Context.Repository);
    }

    [Fact]
    public async Task A_round_counts_toward_the_concurrency_and_daily_limits_like_any_batch()
    {
        using var busy = await WithPullRequest(Settings(concurrent: 1));
        busy.Labeled(8, "alice", 42);
        await busy.Poll();
        busy.Review(9, "alice", "Fix this.");
        await busy.Poll();
        await busy.Poll();
        Assert.Equal(2, busy.Starter.Started.Count);
        Assert.Single(busy.GitHub.Posted, p => p.Issue == 9 && p.Body.Contains("waiting for a free slot", StringComparison.Ordinal));
        busy.Log.End("run-2", IntakeRig.Pr("issue-8", run: "other"));
        await busy.Poll();
        Assert.Equal(3, busy.Starter.Started.Count);

        using var capped = await WithPullRequest(Settings(perDay: 1));
        capped.Review(9, "alice", "Fix this.");
        await capped.Poll();
        Assert.Single(capped.Starter.Started);
        Assert.Contains("did not start", capped.GitHub.Posted.Last().Body);
        Assert.Empty(capped.State.Pending);
    }

    [Fact]
    public async Task A_server_that_will_not_take_the_round_leaves_the_comments_waiting_and_counts_nothing()
    {
        using var rig = await WithPullRequest();
        rig.Review(9, "alice", "Fix this.");
        rig.Starter.Refuse = "the server is halted";
        await rig.Poll();

        Assert.Single(rig.Starter.Started);
        Assert.Single(rig.State.Pending);
        Assert.DoesNotContain(rig.GitHub.Posted, p => p.Body.Contains("chargehand-review-round", StringComparison.Ordinal));

        rig.Starter.Refuse = null;
        await rig.Poll();
        Assert.Equal(2, rig.Starter.Started.Count);
        Assert.Empty(rig.State.Pending);
    }
}
