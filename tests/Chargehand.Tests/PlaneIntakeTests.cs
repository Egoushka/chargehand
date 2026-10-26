using System.Text.Json;
using Chargehand.Automation;
using Chargehand.Contracts;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0067: a Plane work item in the trigger state starts a driven batch of one task, moves to the started state, and ends in a comment and the review state. Plane, the starter, the run log and the clock are fakes.</summary>
public class PlaneIntakeTests
{
    private static bool Valid(RunRequest request) => ContractSchemas.Validate(ContractSchemas.Request, JsonSerializer.SerializeToElement(request, ContractJson.Options)).Count == 0;

    [Fact]
    public async Task An_item_in_the_trigger_state_starts_one_task_from_the_item_moves_to_started_and_says_so()
    {
        using var rig = new PlaneRig();
        rig.Item(7);
        await rig.Poll();

        var request = Assert.Single(rig.Starter.Started);
        Assert.True(Valid(request));
        Assert.Equal(("driven", new RepositoryRef("/srv/repos/repo", rig.Heads.Commit)), (request.Context.Preset, request.Context.Repository));
        var driven = request.Driven!;
        Assert.Equal((1, 2_000_000L, 5m), (driven.MaxParallel, driven.MaxTokensTotal, driven.MaxUsdTotal));
        var task = Assert.Single(driven.Tasks);
        Assert.Equal(("plane-abc-7", "M"), (task.Id, task.Size));
        Assert.Equal("Add a retry\n\nThe client gives up on the first error.\n\nTracker item: ABC-7", task.Goal);
        Assert.Equal([("item-7", "s-doing")], rig.Plane.Moves);
        var (item, comment) = Assert.Single(rig.Plane.Comments);
        Assert.Equal("item-7", item);
        Assert.Contains("chargehand started run run-1 (task plane-abc-7) on octo/repo", comment);
    }

    [Fact]
    public async Task Only_items_in_the_trigger_state_start_and_the_state_is_found_by_name_whatever_the_case()
    {
        using var rig = new PlaneRig(PlaneRig.Defaults() with { TriggerState = " agent " });
        rig.Item(1, "s-todo");
        rig.Item(2, "s-doing");
        rig.Item(3, "s-review");
        rig.Item(4);
        await rig.Poll();

        Assert.Equal(["plane-abc-4"], rig.Starter.Started.Select(r => r.Driven!.Tasks.Single().Id));
    }

    [Fact]
    public async Task An_item_is_started_once_even_when_the_state_change_failed_and_again_when_a_person_moves_it_back()
    {
        using var rig = new PlaneRig();
        rig.Item(7);
        await rig.Poll();
        // The item went back to the trigger state with the same update time, as when the move did not go through.
        var started = rig.Plane.Items.Single() with { StateId = "s-agent", UpdatedAt = rig.Plane.Items.Single().UpdatedAt.AddSeconds(-1) };
        rig.Plane.Items[0] = started;
        await rig.Poll();
        Assert.Single(rig.Starter.Started);

        rig.Log.End("run-1", PlaneRig.Pr(7));
        await rig.Poll();
        rig.Move("item-7", "s-agent");
        await rig.Poll();
        Assert.Equal(2, rig.Starter.Started.Count);
    }

    [Fact]
    public async Task An_item_whose_batch_is_still_running_is_not_started_again_after_a_person_edits_it()
    {
        using var rig = new PlaneRig();
        rig.Item(7);
        await rig.Poll();
        rig.Move("item-7", "s-agent");
        await rig.Poll();

        Assert.Single(rig.Starter.Started);
    }

    [Fact]
    public async Task A_trigger_over_the_concurrent_cap_waits_with_one_comment_and_starts_when_a_slot_frees()
    {
        using var rig = new PlaneRig(PlaneRig.Defaults(concurrent: 1));
        rig.Item(1);
        rig.Item(2);
        await rig.Poll();
        await rig.Poll();

        Assert.Single(rig.Starter.Started);
        Assert.Single(rig.Plane.Comments, c => c.Item == "item-2" && c.Text.Contains("waiting for a free slot"));

        rig.Log.End("run-1", PlaneRig.Pr(1));
        await rig.Poll();
        Assert.Equal(2, rig.Starter.Started.Count);
    }

    [Fact]
    public async Task Starts_are_not_counted_per_day_only_slots_bound_them()
    {
        // ADR 0081: a profile's max_starts_per_day is ignored; spend is bounded by automation.account.
        using var rig = new PlaneRig(PlaneRig.Defaults(concurrent: 1) with { MaxStartsPerDay = 1 });
        for (var n = 1; n <= 3; n++)
            rig.Item(n);
        for (var n = 1; n <= 3; n++)
        {
            await rig.Poll();
            rig.Log.End($"run-{n}", PlaneRig.Pr(n));
        }

        Assert.Equal(3, rig.Starter.Started.Count);
        Assert.DoesNotContain(rig.Plane.Comments, c => c.Text.Contains("did not start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_start_is_a_warning_and_is_tried_again_at_the_next_poll()
    {
        using var rig = new PlaneRig();
        rig.Item(7);
        rig.Starter.Refuse = "halted";
        await rig.Poll();
        Assert.Empty(rig.Plane.Moves);
        Assert.Contains(rig.Warnings, w => w.Contains("halted"));

        rig.Starter.Refuse = null;
        await rig.Poll();
        Assert.Single(rig.Starter.Started);
    }

    [Fact]
    public async Task A_finished_batch_comments_the_pull_request_and_moves_the_item_to_review()
    {
        using var rig = new PlaneRig();
        rig.Item(7);
        await rig.Poll();
        rig.Log.End("run-1", PlaneRig.Pr(7));
        await rig.Poll();
        await rig.Poll();

        Assert.Equal(("item-7", "s-review"), rig.Plane.Moves.Last());
        Assert.Contains(rig.Plane.Comments, c => c.Text == "chargehand finished run run-1: draft pull request https://github.example.test/octo/repo/pull/9");
        Assert.Equal(PlanePhase.Done, rig.State.Tasks.Single().Phase);
        Assert.Equal(2, rig.Plane.Comments.Count);
    }

    [Fact]
    public async Task A_failed_batch_comments_the_reason_and_moves_to_the_failed_state_or_stays_when_there_is_none()
    {
        using var rig = new PlaneRig();
        using var bare = new PlaneRig(PlaneRig.Defaults(failed: null));
        foreach (var r in new[] { rig, bare })
        {
            r.Item(7);
            await r.Poll();
            r.Log.End("run-1", new TaskOutcome("plane-abc-7", TaskState.Failed, null, "the tests did not pass", null));
            await r.Poll();
        }

        Assert.Equal(("item-7", "s-failed"), rig.Plane.Moves.Last());
        Assert.Contains(rig.Plane.Comments, c => c.Text.Contains("ended without a finished pull request (failed): the tests did not pass"));
        Assert.Equal([("item-7", "s-doing")], bare.Plane.Moves);
        Assert.Contains(bare.Plane.Comments, c => c.Text.Contains("the tests did not pass"));
    }

    [Fact]
    public async Task A_batch_that_asked_questions_posts_them_and_does_not_go_to_review()
    {
        using var rig = new PlaneRig();
        rig.Item(7);
        await rig.Poll();
        rig.Log.End("run-1", new TaskOutcome("plane-abc-7", TaskState.NeedsInput, "task-run", null, null));
        rig.Log.Asked("task-run", "Which client?");
        await rig.Poll();

        var comment = rig.Plane.Comments.Last().Text;
        Assert.Contains("- Which client?", comment);
        Assert.Contains("move the item to Agent again", comment);
        Assert.Equal(("item-7", "s-failed"), rig.Plane.Moves.Last());
    }

    [Fact]
    public async Task A_batch_whose_owner_is_gone_is_reported_lost_after_the_grace_period()
    {
        using var rig = new PlaneRig();
        rig.Item(7);
        await rig.Poll();
        rig.Log.Forget("run-1");
        await rig.Poll();
        Assert.Single(rig.Plane.Comments);

        rig.Time.Advance(GitHubIntake.LostAfter + TimeSpan.FromMinutes(1));
        await rig.Poll();
        Assert.Contains(rig.Plane.Comments, c => c.Text.Contains("lost run run-1"));
    }

    [Fact]
    public async Task A_comment_Plane_refuses_leaves_the_task_running_until_it_goes_through()
    {
        using var rig = new PlaneRig();
        rig.Item(7);
        await rig.Poll();
        rig.Log.End("run-1", PlaneRig.Pr(7));
        rig.Plane.CommentsFail = true;
        await rig.Poll();
        Assert.Equal(PlanePhase.Running, rig.State.Tasks.Single().Phase);

        rig.Plane.CommentsFail = false;
        await rig.Poll();
        Assert.Equal(PlanePhase.Done, rig.State.Tasks.Single().Phase);
    }

    [Fact]
    public async Task A_restart_does_not_start_a_handled_item_again_and_still_reports_the_running_batch()
    {
        using var rig = new PlaneRig();
        rig.Item(7);
        await rig.Poll();
        rig.Restart();
        rig.Plane.Items[0] = rig.Plane.Items[0] with { StateId = "s-agent", UpdatedAt = rig.Plane.Items[0].UpdatedAt.AddSeconds(-1) };
        await rig.Poll();
        Assert.Single(rig.Starter.Started);

        rig.Log.End("run-1", PlaneRig.Pr(7));
        rig.Restart();
        await rig.Poll();
        Assert.Equal(("item-7", "s-review"), rig.Plane.Moves.Last());
    }

    [Fact]
    public async Task A_project_without_the_named_states_or_that_Plane_will_not_answer_for_is_skipped_with_a_warning()
    {
        using var rig = new PlaneRig(PlaneRig.Defaults() with { ReviewState = "Shipped" });
        rig.Item(7);
        await rig.Poll();
        Assert.Empty(rig.Starter.Started);
        Assert.Contains(rig.Warnings, w => w.Contains("no state named"));

        using var down = new PlaneRig();
        down.Plane.Down = true;
        await down.Poll();
        Assert.Contains(down.Warnings, w => w.Contains("500"));
    }
}
