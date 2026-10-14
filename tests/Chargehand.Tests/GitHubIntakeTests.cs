using System.Text.Json;
using Chargehand.Automation;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0051: an issue label or an @-mention by an allowed author starts a driven batch of one task; a reply to its questions continues it. GitHub, the batch starter, the run log and the clock are fakes.</summary>
public class GitHubIntakeTests
{
    private static AutomationGitHub Settings(int concurrent = 2, int perDay = 10) =>
        new(new Dictionary<string, AutomationRepository> { [IntakeRig.Repo] = new("/srv/repos/repo") }, [IntakeRig.Author], MaxConcurrent: concurrent, MaxStartsPerDay: perDay);

    private static bool Valid(RunRequest request) => ContractSchemas.Validate(ContractSchemas.Request, JsonSerializer.SerializeToElement(request, ContractJson.Options)).Count == 0;

    [Fact]
    public async Task A_label_applied_by_an_allowed_author_starts_one_task_from_the_issue_and_says_so_on_the_issue()
    {
        using var rig = new IntakeRig();
        rig.Labeled(7, "alice", 41, "size:L");
        await rig.Poll();

        var request = Assert.Single(rig.Starter.Started);
        Assert.True(Valid(request));
        Assert.Equal(("driven", new RepositoryRef("/srv/repos/repo", rig.Heads.Commit)), (request.Context.Preset, request.Context.Repository));
        var driven = request.Driven!;
        Assert.Equal((1, 2_000_000L, 5m), (driven.MaxParallel, driven.MaxTokensTotal, driven.MaxUsdTotal));
        var task = Assert.Single(driven.Tasks);
        Assert.Equal(("issue-7", "L"), (task.Id, task.Size));
        // The first line is what the pull request's title is cut from; the issue's text is fenced as untrusted.
        Assert.StartsWith("Add a retry\n\nResolve GitHub issue octo/repo#7.", task.Goal);
        Assert.Contains("<untrusted-github-text>\nIssue title: Add a retry", task.Goal);
        Assert.Contains("The client gives up on the first error.", task.Goal);
        var (number, comment) = Assert.Single(rig.GitHub.Posted);
        Assert.Equal(7, number);
        Assert.StartsWith(GitHubIntake.Marker, comment);
        Assert.Contains("started run `run-1` (task `issue-7`)", comment);
    }

    [Fact]
    public async Task Without_a_size_label_the_profile_budget_class_applies_and_two_size_labels_are_not_a_size()
    {
        using var rig = new IntakeRig(Settings(concurrent: 5));
        rig.Labeled(1);
        rig.Labeled(2, "alice", 5, "size:S", "size:L");
        rig.Labeled(3, "alice", 6, "size:S");
        await rig.Poll();

        Assert.Equal(["M", "M", "S"], rig.Starter.Started.Select(r => r.Driven!.Tasks.Single().Size));
    }

    [Fact]
    public async Task A_label_applied_by_someone_who_may_not_trigger_starts_nothing_and_is_not_read_again()
    {
        using var rig = new IntakeRig();
        rig.Labeled(7, "mallory", 41);
        await rig.Poll();
        await rig.Poll();

        Assert.Empty(rig.Starter.Started);
        Assert.Empty(rig.GitHub.Posted);
        // GitHub reports the issue unchanged, so its events are read once.
        Assert.Equal(1, rig.GitHub.EventReads);
    }

    [Fact]
    public async Task A_label_the_same_event_applied_starts_once_and_applying_it_again_starts_again()
    {
        using var rig = new IntakeRig();
        rig.Labeled(7, "alice", 41);
        await rig.Poll();
        await rig.Poll();
        rig.Log.End("run-1", IntakeRig.Pr());
        await rig.Poll();
        Assert.Single(rig.Starter.Started);

        rig.Time.Advance(TimeSpan.FromMinutes(1));
        rig.Labeled(7, "alice", 42);
        await rig.Poll();
        Assert.Equal(2, rig.Starter.Started.Count);
    }

    private static IntegratorPolicy Policy(string mode) => new(new DrivenIntegrator(1, 2, "k", new Dictionary<string, string> { [IntakeRig.Repo] = mode }));

    [Fact]
    public async Task A_repository_the_integrator_merges_into_takes_no_work_from_an_issue_an_allowed_author_did_not_write()
    {
        using var rig = new IntakeRig(Settings(concurrent: 5), Policy("merge"));
        rig.Labeled(7, "alice", 41);
        rig.GitHub.Issues.Add(rig.GitHub.Issues.Single() with { Number = 8, Author = "alice" });
        rig.GitHub.LabelEvents[8] = new LabelEvent(42, "alice");
        await rig.Poll();
        await rig.Poll();

        Assert.Equal("issue-8", Assert.Single(rig.Starter.Started).Driven!.Tasks.Single().Id);
        Assert.Single(rig.GitHub.Posted, p => p.Issue == 7 && p.Body.Contains("written by bob", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Where_the_integrator_only_asks_an_issue_from_anyone_is_taken()
    {
        using var rig = new IntakeRig(null, Policy("ask"));
        rig.Labeled(7, "alice", 41);
        await rig.Poll();

        Assert.Single(rig.Starter.Started);
    }

    [Fact]
    public async Task A_pull_request_with_the_label_is_not_an_issue()
    {
        using var rig = new IntakeRig();
        rig.GitHub.Issues.Add(new IntakeIssue(5, "A change", "", "bob", true, ["chargehand"], rig.Now));
        rig.GitHub.LabelEvents[5] = new LabelEvent(1, "alice");
        await rig.Poll();

        Assert.Empty(rig.Starter.Started);
        Assert.Equal(0, rig.GitHub.EventReads);
    }

    [Fact]
    public async Task A_comment_that_mentions_the_bot_by_an_allowed_author_starts_a_task_with_the_comment_in_the_goal()
    {
        using var rig = new IntakeRig();
        await rig.Poll();
        rig.Plain(7);
        rig.Comment(7, "alice", "@Chargehand please handle this, start with the client.");
        await rig.Poll();

        var task = Assert.Single(Assert.Single(rig.Starter.Started).Driven!.Tasks);
        Assert.Equal("issue-7", task.Id);
        Assert.Contains("The comment that asked for this work, by alice:", task.Goal);
        Assert.Contains("start with the client.", task.Goal);
        Assert.Contains("started run `run-1`", Assert.Single(rig.GitHub.Posted).Body);
    }

    [Fact]
    public async Task Only_a_mention_by_an_allowed_author_in_plain_text_starts_a_task()
    {
        using var rig = new IntakeRig();
        await rig.Poll();
        rig.Plain(7);
        rig.Comment(7, "mallory", "@chargehand do it");
        rig.Comment(7, "alice", "thanks @chargehand-bot");
        rig.Comment(7, "alice", "write to me@chargehand.example.test");
        rig.Comment(7, "alice", "> @chargehand do it\nthanks");
        rig.Comment(7, "alice", "no mention here");
        rig.Comment(7, "alice", $"{GitHubIntake.Marker}\n@chargehand quoted from the bot");
        await rig.Poll();

        Assert.Empty(rig.Starter.Started);
        Assert.Empty(rig.GitHub.Posted);
    }

    [Fact]
    public async Task A_comment_from_before_the_first_poll_is_not_a_request()
    {
        using var rig = new IntakeRig();
        rig.Plain(7);
        rig.Comment(7, "alice", "@chargehand go");
        rig.Time.Advance(TimeSpan.FromMinutes(10));
        await rig.Poll();

        Assert.Empty(rig.Starter.Started);
    }

    [Fact]
    public async Task A_mention_on_a_pull_request_starts_no_batch_here()
    {
        using var rig = new IntakeRig();
        await rig.Poll();
        rig.GitHub.Issues.Add(new IntakeIssue(5, "A change", "", "bob", true, [], rig.Now));
        rig.Comment(5, "alice", "@chargehand look at this");
        await rig.Poll();

        Assert.Empty(rig.Starter.Started);
    }

    [Fact]
    public async Task A_restart_does_not_start_a_trigger_it_already_started()
    {
        using var rig = new IntakeRig();
        await rig.Poll();
        rig.Labeled(7, "alice", 41);
        rig.Plain(8);
        rig.Comment(8, "alice", "@chargehand go");
        await rig.Poll();
        Assert.Equal(2, rig.Starter.Started.Count);

        // GitHub moves an issue's update time when chargehand comments on it, so the new process reads its events again; the comment is still inside the cursor.
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        rig.GitHub.Issues[0] = rig.GitHub.Issues[0] with { UpdatedAt = rig.Now };
        var reads = rig.GitHub.EventReads;
        rig.Restart();
        await rig.Poll();

        Assert.Equal(reads + 1, rig.GitHub.EventReads);
        Assert.Equal(2, rig.Starter.Started.Count);
        Assert.Equal(2, rig.State.Tasks.Count);
    }

    [Fact]
    public async Task A_batch_started_before_a_restart_is_reported_after_it()
    {
        using var rig = new IntakeRig();
        rig.Labeled(7, "alice", 41);
        await rig.Poll();
        rig.Restart();
        rig.Log.End("run-1", IntakeRig.Pr());
        await rig.Poll();

        Assert.Contains("draft pull request https://github.example.test/octo/repo/pull/9", rig.GitHub.Posted.Last().Body);
        var task = Assert.Single(rig.State.Tasks);
        Assert.Equal((IntakePhase.Done, "https://github.example.test/octo/repo/pull/9", "chargehand/task-run"), (task.Phase, task.PrUrl, task.Branch));
    }

    [Fact]
    public async Task A_task_that_asks_is_answered_by_a_reply_of_an_allowed_author_and_continues_from_it()
    {
        using var rig = new IntakeRig();
        rig.Labeled(7, "alice", 41);
        await rig.Poll();
        rig.Comment(7, "alice", "maybe use a library?");
        rig.Log.Asked("task-run", "Which retry policy do you want?", "Should it be configurable?");
        rig.Log.End("run-1", new TaskOutcome("issue-7", TaskState.NeedsInput, "task-run", "the session asked"));
        await rig.Poll();

        var asked = rig.GitHub.Posted.Last().Body;
        Assert.Contains("needs an answer", asked);
        Assert.Contains("- Which retry policy do you want?", asked);
        Assert.Contains("- Should it be configurable?", asked);
        Assert.Single(rig.Starter.Started);

        // A comment before the questions, or from someone else, answers nothing.
        rig.Heads.Commit = "fffffff0000000fffffff0000000fffffff00000";
        rig.Comment(7, "mallory", "use exponential backoff");
        await rig.Poll();
        Assert.Single(rig.Starter.Started);

        rig.Comment(7, "alice", "Use exponential backoff, not configurable.");
        await rig.Poll();

        Assert.Equal(2, rig.Starter.Started.Count);
        var request = rig.Starter.Started[1];
        Assert.True(Valid(request));
        var task = Assert.Single(request.Driven!.Tasks);
        Assert.Equal(new ContinueFrom("run-1", "issue-7"), task.ContinueFrom);
        Assert.Equal(["Use exponential backoff, not configurable."], task.Answers);
        Assert.Null(task.Goal);
        // Its kept work is a branch from the commit the first run started at, whatever the default branch is now.
        Assert.Equal(rig.Starter.Started[0].Context.Repository, request.Context.Repository);
        Assert.Contains("continues with your answer: run `run-2`", rig.GitHub.Posted.Last().Body);

        // The reply is taken once, and the issue's latest task is the continuation.
        await rig.Poll();
        Assert.Equal(2, rig.Starter.Started.Count);
        Assert.Equal(["run-1", "run-2"], rig.State.Tasks.Select(t => t.BatchRunId));
        Assert.Equal(IntakePhase.Running, rig.State.Tasks[1].Phase);
    }

    [Fact]
    public async Task A_reply_with_nothing_asked_is_just_a_comment()
    {
        using var rig = new IntakeRig();
        rig.Labeled(7, "alice", 41);
        await rig.Poll();
        rig.Comment(7, "alice", "any news?");
        await rig.Poll();

        Assert.Single(rig.Starter.Started);
    }

    [Fact]
    public async Task A_task_that_failed_is_reported_with_what_it_says_and_asks_nothing()
    {
        using var rig = new IntakeRig();
        rig.Labeled(7, "alice", 41);
        await rig.Poll();
        rig.Log.End("run-1", new TaskOutcome("issue-7", TaskState.Failed, "task-run", "the tests fail on the branch", ErrorCode.SessionFailed));
        await rig.Poll();

        var report = rig.GitHub.Posted.Last().Body;
        Assert.Contains("ended without a finished pull request (failed): the tests fail on the branch", report);
        Assert.Equal(IntakePhase.Done, rig.State.Tasks.Single().Phase);
    }

    [Fact]
    public async Task A_batch_that_never_started_its_task_is_reported_with_the_reason()
    {
        using var rig = new IntakeRig();
        rig.Labeled(7, "alice", 41);
        await rig.Poll();
        rig.Log.End("run-1", new TaskOutcome("other", TaskState.Completed, "x", null, null, 0, 0, "b", "https://github.example.test/o/r/pull/1"));
        await rig.Poll();

        Assert.Contains("ended without starting the task", rig.GitHub.Posted.Last().Body);
        Assert.Equal(IntakePhase.Done, rig.State.Tasks.Single().Phase);
    }

    [Fact]
    public async Task A_batch_that_is_gone_frees_its_slot_after_the_grace_and_not_before()
    {
        using var rig = new IntakeRig(Settings(concurrent: 1));
        rig.Labeled(7, "alice", 41);
        rig.Labeled(8, "alice", 42);
        await rig.Poll();
        rig.Log.Forget("run-1");

        await rig.Poll();
        Assert.Single(rig.Starter.Started);

        rig.Time.Advance(GitHubIntake.LostAfter + TimeSpan.FromMinutes(1));
        await rig.Poll();
        Assert.Single(rig.GitHub.Posted, p => p.Issue == 7 && p.Body.Contains("lost run `run-1`", StringComparison.Ordinal));
        Assert.Equal(2, rig.Starter.Started.Count);
    }

    [Fact]
    public async Task Triggers_over_the_concurrency_cap_wait_with_one_comment_and_start_when_a_slot_frees()
    {
        using var rig = new IntakeRig(Settings(concurrent: 1));
        rig.Labeled(1);
        rig.Labeled(2, "alice", 5);
        await rig.Poll();
        await rig.Poll();

        Assert.Equal("issue-1", Assert.Single(rig.Starter.Started).Driven!.Tasks.Single().Id);
        Assert.Single(rig.GitHub.Posted, p => p.Issue == 2 && p.Body.Contains("waiting for a free slot", StringComparison.Ordinal));

        rig.Log.End("run-1", IntakeRig.Pr("issue-1"));
        await rig.Poll();

        Assert.Equal(["issue-1", "issue-2"], rig.Starter.Started.Select(r => r.Driven!.Tasks.Single().Id));
        Assert.Single(rig.GitHub.Posted, p => p.Issue == 2 && p.Body.Contains("waiting for a free slot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_comment_waiting_for_a_slot_is_read_again_and_starts_later()
    {
        using var rig = new IntakeRig(Settings(concurrent: 1));
        await rig.Poll();
        rig.Labeled(1);
        rig.Plain(2);
        rig.Comment(2, "alice", "@chargehand go");
        await rig.Poll();
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await rig.Poll();
        Assert.Single(rig.Starter.Started);

        rig.Log.End("run-1", IntakeRig.Pr("issue-1"));
        await rig.Poll();

        Assert.Equal(["issue-1", "issue-2"], rig.Starter.Started.Select(r => r.Driven!.Tasks.Single().Id));
    }

    [Fact]
    public async Task Triggers_over_the_daily_cap_are_refused_once_and_not_started_later_but_a_new_trigger_after_a_day_is()
    {
        using var rig = new IntakeRig(Settings(perDay: 1));
        rig.Labeled(1);
        await rig.Poll();
        rig.Log.End("run-1", IntakeRig.Pr("issue-1"));
        rig.Labeled(2, "alice", 5);
        await rig.Poll();
        await rig.Poll();

        Assert.Single(rig.Starter.Started);
        Assert.Single(rig.GitHub.Posted, p => p.Issue == 2 && p.Body.Contains("did not start", StringComparison.Ordinal));

        rig.Time.Advance(TimeSpan.FromHours(25));
        await rig.Poll();
        Assert.Single(rig.Starter.Started);

        rig.Labeled(3, "alice", 6);
        await rig.Poll();
        Assert.Equal(["issue-1", "issue-3"], rig.Starter.Started.Select(r => r.Driven!.Tasks.Single().Id));
    }

    [Fact]
    public async Task An_answer_counts_against_the_daily_cap_like_any_start()
    {
        using var rig = new IntakeRig(Settings(perDay: 1));
        rig.Labeled(7, "alice", 41);
        await rig.Poll();
        rig.Log.Asked("task-run", "Which policy?");
        rig.Log.End("run-1", new TaskOutcome("issue-7", TaskState.NeedsInput, "task-run", "asked"));
        await rig.Poll();
        rig.Comment(7, "alice", "exponential");
        await rig.Poll();

        Assert.Single(rig.Starter.Started);
        Assert.Contains("did not start", rig.GitHub.Posted.Last().Body);
    }

    [Fact]
    public async Task A_second_trigger_on_an_issue_with_a_running_task_starts_nothing_and_says_so_once()
    {
        using var rig = new IntakeRig();
        rig.Labeled(7, "alice", 41);
        await rig.Poll();
        rig.Comment(7, "alice", "@chargehand again");
        await rig.Poll();
        await rig.Poll();

        Assert.Single(rig.Starter.Started);
        Assert.Single(rig.GitHub.Posted, p => p.Body.Contains("already working", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_server_that_will_not_take_the_batch_leaves_the_trigger_for_the_next_poll()
    {
        using var rig = new IntakeRig();
        rig.Labeled(7, "alice", 41);
        rig.Starter.Refuse = "the server is halted";
        await rig.Poll();

        Assert.Empty(rig.Starter.Started);
        Assert.Empty(rig.GitHub.Posted);
        Assert.Empty(rig.State.Tasks);
        Assert.Contains(rig.Warnings, w => w.Contains("the server is halted", StringComparison.Ordinal));

        rig.Starter.Refuse = null;
        await rig.Poll();
        Assert.Single(rig.Starter.Started);
    }

    [Fact]
    public async Task A_comment_that_cannot_be_posted_does_not_start_the_batch_again()
    {
        using var rig = new IntakeRig();
        rig.Labeled(7, "alice", 41);
        rig.GitHub.CommentsFail = true;
        await rig.Poll();
        await rig.Poll();

        Assert.Single(rig.Starter.Started);
        Assert.Contains(rig.Warnings, w => w.Contains("was not posted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_repository_that_cannot_be_read_does_not_stop_the_others()
    {
        using var rig = new IntakeRig(new AutomationGitHub(new Dictionary<string, AutomationRepository>
        {
            ["octo/broken"] = new("/srv/repos/broken"),
            [IntakeRig.Repo] = new("/srv/repos/repo"),
        }, [IntakeRig.Author]));
        rig.GitHub.FailFor = "octo/broken";
        rig.Labeled(7, "alice", 41);
        await rig.Poll();

        Assert.Single(rig.Starter.Started);
        Assert.Contains(rig.Warnings, w => w.Contains("octo/broken", StringComparison.Ordinal));
    }
}
