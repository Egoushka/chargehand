using Chargehand.Automation;
using Chargehand.Contracts;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0068: what is due, the monthly cap, a catch-up after downtime and the state across a restart.</summary>
public class ScheduleDueTests
{
    private static readonly TimeSpan Hours4 = TimeSpan.FromHours(4);

    [Fact]
    public async Task The_first_wake_of_an_entry_owes_nothing_and_the_next_occurrence_starts_a_run()
    {
        using var rig = new ScheduleRig(ScheduleRig.Docs());
        await rig.Tick();
        Assert.Empty(rig.Starter.Started);

        await rig.Wake(Hours4 - TimeSpan.FromMinutes(1));
        Assert.Empty(rig.Starter.Started);

        await rig.Wake(TimeSpan.FromMinutes(1));
        var request = Assert.Single(rig.Starter.Started);
        Assert.Equal(("default", "/srv/repos/repo", rig.Heads.Commit), (request.Context.Preset, request.Context.Repository!.Path, request.Context.Repository.Commit));
        Assert.Contains("README.md", request.Text);
        Assert.Null(request.Driven);
    }

    [Fact]
    public async Task One_occurrence_starts_one_run_however_many_wakes_follow()
    {
        using var rig = new ScheduleRig(ScheduleRig.Docs());
        await rig.Tick();
        await rig.Wake(Hours4);
        await rig.Wake(TimeSpan.FromMinutes(1));
        await rig.Wake(TimeSpan.FromMinutes(1));
        Assert.Single(rig.Starter.Started);
    }

    [Fact]
    public async Task A_driven_template_is_a_batch_of_one_task_of_the_budget_class_with_the_goal()
    {
        using var rig = new ScheduleRig(ScheduleRig.Deps() with { BudgetClass = "M" });
        await rig.Tick();
        await rig.Wake(Hours4);

        var request = Assert.Single(rig.Starter.Started);
        Assert.Equal("driven", request.Context.Preset);
        var task = Assert.Single(request.Driven!.Tasks);
        Assert.Equal(("schedule-deps", "M", 1), (task.Id, task.Size, request.Driven.MaxParallel));
        Assert.Contains("dependencies", task.Goal);
    }

    [Fact]
    public async Task A_driven_batch_defaults_to_size_S_and_a_free_goal_is_used_as_written()
    {
        using var rig = new ScheduleRig(new ScheduleEntry("tidy", "daily 04:00", Goal: "Remove dead code", Driven: true, Repository: new AutomationRepository("/p")));
        await rig.Tick();
        await rig.Wake(Hours4);

        var task = Assert.Single(Assert.Single(rig.Starter.Started).Driven!.Tasks);
        Assert.Equal(("S", "Remove dead code"), (task.Size, task.Goal));
    }

    [Fact]
    public async Task A_weekly_entry_waits_for_its_day()
    {
        using var rig = new ScheduleRig(ScheduleRig.Docs("weekly mon 09:00"));
        await rig.Tick();
        await rig.Wake(TimeSpan.FromDays(4));
        Assert.Empty(rig.Starter.Started);
        await rig.Wake(TimeSpan.FromHours(9));
        Assert.Single(rig.Starter.Started);
    }

    [Fact]
    public async Task A_run_that_is_still_going_when_the_next_occurrence_comes_is_not_started_again_and_the_occurrence_is_logged()
    {
        using var rig = new ScheduleRig(ScheduleRig.Docs());
        await rig.Tick();
        await rig.Wake(Hours4);
        await rig.Wake(TimeSpan.FromDays(1));

        Assert.Single(rig.Starter.Started);
        Assert.Contains(rig.Warnings, w => w.Contains("docs") && w.Contains("still going"));
    }

    [Fact]
    public async Task The_monthly_cap_skips_the_occurrence_and_says_so_and_the_next_month_counts_afresh()
    {
        using var rig = new ScheduleRig(ScheduleRig.Docs(cap: 2));
        await rig.Tick();
        for (var day = 0; day < 4; day++)
        {
            await rig.Wake(day == 0 ? Hours4 : TimeSpan.FromDays(1));
            // The run ends before the next occurrence, so only the cap can stop the third start.
            foreach (var run in rig.State.Running)
                rig.Log.End(run.RunId, ScheduleRig.Result());
            await rig.Tick();
        }

        Assert.Equal(2, rig.Starter.Started.Count);
        Assert.Contains(rig.Warnings, w => w.Contains("docs") && w.Contains("cap of 2") && w.Contains("2026-01"));
        Assert.Equal(2, rig.State.StartsIn("docs", rig.Time.GetUtcNow()));

        await rig.Wake(TimeSpan.FromDays(28));
        Assert.Equal(3, rig.Starter.Started.Count);
        Assert.Equal(1, rig.State.StartsIn("docs", rig.Time.GetUtcNow()));
    }

    [Fact]
    public async Task A_cap_of_the_default_is_four_starts_a_month()
    {
        using var rig = new ScheduleRig(ScheduleRig.Docs());
        await rig.Tick();
        for (var day = 0; day < 10; day++)
        {
            await rig.Wake(day == 0 ? Hours4 : TimeSpan.FromDays(1));
            foreach (var run in rig.State.Running)
                rig.Log.End(run.RunId, ScheduleRig.Result());
            await rig.Tick();
        }

        Assert.Equal(4, rig.Starter.Started.Count);
    }

    [Fact]
    public async Task Slots_missed_while_the_server_was_down_run_once_at_startup()
    {
        using var rig = new ScheduleRig(ScheduleRig.Docs());
        await rig.Tick();
        await rig.Wake(Hours4);
        rig.Log.End("run-1", ScheduleRig.Result());
        await rig.Tick();

        // Five daily slots pass while the server is off; a new process wakes once.
        rig.Time.Advance(TimeSpan.FromDays(5));
        rig.Restart();
        await rig.Tick();
        await rig.Tick();
        await rig.Wake(TimeSpan.FromMinutes(1));

        Assert.Equal(2, rig.Starter.Started.Count);
    }

    [Fact]
    public async Task The_state_survives_a_restart_so_an_occurrence_already_run_does_not_run_again()
    {
        using var rig = new ScheduleRig(ScheduleRig.Docs());
        await rig.Tick();
        await rig.Wake(Hours4);
        Assert.Single(rig.Starter.Started);

        rig.Restart();
        await rig.Tick();

        Assert.Single(rig.Starter.Started);
        Assert.True(File.Exists(rig.StateFile));
        var state = rig.State;
        Assert.Equal(1, state.StartsIn("docs", rig.Time.GetUtcNow()));
        Assert.Equal("run-1", Assert.Single(state.Running).RunId);
    }

    [Fact]
    public async Task A_restart_keeps_the_count_of_the_month_so_the_cap_still_holds()
    {
        using var rig = new ScheduleRig(ScheduleRig.Docs(cap: 1));
        await rig.Tick();
        await rig.Wake(Hours4);
        rig.Log.End("run-1", ScheduleRig.Result());
        await rig.Tick();

        rig.Restart();
        await rig.Wake(TimeSpan.FromDays(1));

        Assert.Single(rig.Starter.Started);
    }

    [Fact]
    public async Task A_refused_start_is_tried_again_after_the_retry_delay_and_not_counted()
    {
        using var rig = new ScheduleRig(ScheduleRig.Docs());
        await rig.Tick();
        rig.Starter.Refuse = "halted";
        await rig.Wake(Hours4);
        await rig.Wake(TimeSpan.FromMinutes(1));
        Assert.Empty(rig.Starter.Started);
        Assert.Single(rig.Warnings, w => w.Contains("halted"));
        Assert.Equal(0, rig.State.StartsIn("docs", rig.Time.GetUtcNow()));

        rig.Starter.Refuse = null;
        await rig.Wake(Scheduler.RetryAfter);

        Assert.Single(rig.Starter.Started);
        Assert.Equal(1, rig.State.StartsIn("docs", rig.Time.GetUtcNow()));
    }

    [Fact]
    public async Task A_checkout_that_cannot_be_read_is_reported_and_one_bad_entry_does_not_stop_the_others()
    {
        using var rig = new ScheduleRig(ScheduleRig.Docs(), ScheduleRig.Docs(id: "other"));
        rig.Heads.Fail = "checkout is gone";
        await rig.Tick();
        await rig.Wake(Hours4);

        Assert.Empty(rig.Starter.Started);
        Assert.Equal(2, rig.Warnings.Count(w => w.Contains("checkout is gone")));
        rig.Heads.Fail = null;
        await rig.Wake(Scheduler.RetryAfter);
        Assert.Equal(2, rig.Starter.Started.Count);
    }

    [Fact]
    public async Task An_entry_removed_from_the_profile_while_its_run_goes_is_dropped_without_a_post()
    {
        using var rig = new ScheduleRig(ScheduleRig.Docs());
        await rig.Tick();
        await rig.Wake(Hours4);
        rig.Log.End("run-1", ScheduleRig.Result());
        rig.Restart(ScheduleRig.Docs(id: "else"));

        await rig.Tick();

        Assert.Empty(rig.Notifier.Posted);
        Assert.Empty(rig.State.Running);
    }
}
