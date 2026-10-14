using Chargehand.Automation;
using Chargehand.Config;
using Chargehand.Driven;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Tests;

/// <summary>CHARGEHAND-214: intake keeps what it did with each trigger and when, so the console can say why nothing started. The comment text is never kept, only the key, the login and the outcome.</summary>
public class IntakeTriggerLogTests
{
    private static AutomationGitHub Settings(int concurrent = 2, int perDay = 10, int rounds = 3) =>
        new(new Dictionary<string, AutomationRepository> { [IntakeRig.Repo] = new("/srv/repos/repo") }, [IntakeRig.Author], MaxConcurrent: concurrent, MaxStartsPerDay: perDay, MaxReviewRounds: rounds);

    [Fact]
    public async Task A_started_task_is_logged_with_the_time_and_the_trigger()
    {
        using var rig = new IntakeRig();
        rig.Labeled(7, "alice", 41);
        await rig.Poll();

        var trigger = Assert.Single(rig.State.Triggers);
        Assert.Equal(("label:octo/repo#7:41", "started", null), (trigger.Key, trigger.Outcome, trigger.Reason));
        Assert.Equal(rig.Now, trigger.At);
    }

    [Fact]
    public async Task A_label_set_by_someone_who_is_not_allowed_is_ignored_and_the_log_says_who_and_why()
    {
        using var rig = new IntakeRig();
        rig.Labeled(7, "mallory", 41);
        await rig.Poll();

        Assert.Empty(rig.Starter.Started);
        var trigger = Assert.Single(rig.State.Triggers);
        Assert.Equal(("label:octo/repo#7:41", "ignored", "author_not_allowed", "mallory"), (trigger.Key, trigger.Outcome, trigger.Reason, trigger.Author));
    }

    [Fact]
    public async Task A_mention_by_someone_who_is_not_allowed_is_ignored_and_a_comment_that_mentions_nobody_is_not_a_trigger()
    {
        using var rig = new IntakeRig();
        await rig.Poll();
        rig.Plain(7);
        var mention = rig.Comment(7, "mallory", "@chargehand please do this");
        rig.Comment(7, "mallory", "just a thought");
        await rig.Poll();

        Assert.Empty(rig.Starter.Started);
        var trigger = Assert.Single(rig.State.Triggers);
        Assert.Equal(($"comment:octo/repo:{mention}", "ignored", "author_not_allowed", "mallory"), (trigger.Key, trigger.Outcome, trigger.Reason, trigger.Author));
    }

    [Fact]
    public async Task A_second_trigger_on_a_running_issue_and_a_trigger_past_the_daily_cap_are_ignored_with_their_reasons()
    {
        using var rig = new IntakeRig(Settings(perDay: 1));
        rig.Labeled(7, "alice", 41);
        await rig.Poll();
        rig.Comment(7, "alice", "@chargehand again");
        rig.Labeled(8, "alice", 42);
        await rig.Poll();

        Assert.Equal(["started:", "ignored:cap", "ignored:active"], rig.State.Triggers.Select(t => $"{t.Outcome}:{t.Reason}"));
    }

    [Fact]
    public async Task An_answer_from_someone_else_is_ignored_and_the_allowed_authors_answer_is_logged_as_continued()
    {
        using var rig = new IntakeRig();
        rig.Labeled(7, "alice", 41);
        await rig.Poll();
        rig.Comment(7, "alice", "maybe use a library?");
        rig.Log.Asked("task-run", "Which retry policy do you want?");
        rig.Log.End("run-1", new TaskOutcome("issue-7", TaskState.NeedsInput, "task-run", "the session asked"));
        await rig.Poll();
        rig.Comment(7, "mallory", "use exponential backoff");
        await rig.Poll();
        rig.Comment(7, "alice", "Use exponential backoff.");
        await rig.Poll();

        Assert.Equal(["started:", "ignored:author_not_allowed", "continued:"], rig.State.Triggers.Select(t => $"{t.Outcome}:{t.Reason}"));
    }

    [Fact]
    public async Task A_mention_on_a_pull_request_that_is_not_chargehands_is_ignored_and_one_on_its_own_is_queued()
    {
        using var rig = new IntakeRig();
        await rig.Poll();
        rig.Pull(20, "feature/mine");
        rig.Pull(21, "chargehand/task-run");
        var foreign = rig.Comment(20, "alice", "@chargehand take a look");
        var own = rig.Comment(21, "alice", "@chargehand rename this");
        await rig.Poll();

        Assert.Contains(rig.State.Triggers, t => t.Key == $"comment:octo/repo:{foreign}" && t is { Outcome: "ignored", Reason: "not_chargehand_branch", Author: "alice" });
        Assert.Contains(rig.State.Triggers, t => t.Key == $"comment:octo/repo:{own}" && t.Outcome == "queued");
    }

    [Fact]
    public async Task A_mention_on_a_pull_request_with_review_rounds_off_is_ignored_with_that_reason()
    {
        using var rig = new IntakeRig(Settings(rounds: 0));
        await rig.Poll();
        rig.Pull(21, "chargehand/task-run");
        rig.Comment(21, "alice", "@chargehand rename this");
        await rig.Poll();

        Assert.Equal("review_rounds_off", Assert.Single(rig.State.Triggers).Reason);
    }

    [Fact]
    public async Task An_inline_review_comment_that_is_not_a_request_is_not_logged_at_all_and_a_queued_one_is()
    {
        using var rig = new IntakeRig();
        await rig.Poll();
        rig.Pull(20, "feature/mine");
        rig.Pull(21, "chargehand/task-run");
        rig.Review(20, "alice", "nit");
        rig.Review(21, "mallory", "do this");
        var queued = rig.Review(21, "alice", "rename this");
        await rig.Poll();

        var trigger = Assert.Single(rig.State.Triggers);
        Assert.Equal(($"review:octo/repo:{queued}", "queued", "alice"), (trigger.Key, trigger.Outcome, trigger.Author));
    }

    [Fact]
    public async Task The_log_survives_a_restart_and_keeps_the_latest_200_only_and_never_the_text_of_a_comment()
    {
        using var rig = new IntakeRig();
        await rig.Poll();
        rig.Plain(7);
        for (var i = 0; i < IntakeState.MaxTriggers + 30; i++)
            rig.Comment(7, "mallory", $"@chargehand secret-looking text {i}");
        await rig.Poll();
        rig.Restart();

        var triggers = rig.State.Triggers;
        Assert.Equal(IntakeState.MaxTriggers, triggers.Count);
        Assert.Equal("ignored", triggers[^1].Outcome);
        Assert.True(triggers.Zip(triggers.Skip(1)).All(p => p.First.At <= p.Second.At));
        Assert.DoesNotContain("secret-looking", File.ReadAllText(rig.StateFile), StringComparison.Ordinal);
    }
}
