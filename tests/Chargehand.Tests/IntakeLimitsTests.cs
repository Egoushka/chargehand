using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chargehand.Automation;
using Chargehand.Config;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Tests;

/// <summary>The owner's overrides of the intake caps and the pause switch, set from the console and read at the next poll.</summary>
public class IntakeLimitsTests
{
    private static AutomationGitHub Profile(int maxConcurrent) =>
        new(new Dictionary<string, AutomationRepository> { [IntakeRig.Repo] = new("/srv/repos/repo") }, [IntakeRig.Author], MaxConcurrent: maxConcurrent);

    [Fact]
    public async Task An_override_of_max_concurrent_wins_over_the_profile_at_the_next_poll()
    {
        using var rig = new IntakeRig(Profile(2));
        rig.Intake.Sources.SetLimits(new IntakeSources.Limits(MaxConcurrent: 1));
        rig.Labeled(7);
        rig.Labeled(8, eventId: 2);
        await rig.Poll();

        Assert.Single(rig.Starter.Started);
        Assert.Contains(rig.State.Waiting, w => w.Reason == "max_concurrent");
    }

    [Fact]
    public async Task An_override_of_max_usd_total_is_the_budget_of_the_batch_that_starts()
    {
        using var rig = new IntakeRig(Profile(2));
        rig.Intake.Sources.SetLimits(new IntakeSources.Limits(MaxUsdTotal: 2.5m));
        rig.Labeled(7);
        await rig.Poll();

        Assert.Equal(2.5m, Assert.Single(rig.Starter.Started).Driven!.MaxUsdTotal);
    }

    [Fact]
    public async Task An_override_of_max_review_rounds_of_zero_turns_review_rounds_off()
    {
        using var rig = new IntakeRig(Profile(2) with { MaxReviewRounds = 3 });
        rig.Intake.Sources.SetLimits(new IntakeSources.Limits(MaxReviewRounds: 0));
        await rig.Poll();
        rig.Labeled(7, "alice", 41);
        await rig.Poll();
        rig.Log.TaskRun("task-run", "run-1", "chargehand/task-run");
        rig.Log.End("run-1", IntakeRig.Pr());
        rig.Pull(9);
        await rig.Poll();
        rig.Review(9, "alice", "Fix this.");
        await rig.Poll();

        Assert.Single(rig.Starter.Started);
        Assert.Equal(0, rig.GitHub.PullReads);
    }

    [Fact]
    public async Task Paused_holds_a_pull_request_review_and_the_review_starts_once_it_is_switched_off()
    {
        using var rig = new IntakeRig(Profile(2) with { ReviewPulls = true });
        rig.Intake.Sources.SetLimits(new IntakeSources.Limits(Paused: true));
        rig.OpenPull(5, "1111111aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        await rig.Poll();
        Assert.Empty(rig.Starter.Started);

        rig.Intake.Sources.SetLimits(new IntakeSources.Limits());
        await rig.Poll();
        Assert.Single(rig.Starter.Started);
    }

    [Fact]
    public async Task Paused_holds_every_new_start_until_it_is_switched_off()
    {
        using var rig = new IntakeRig(Profile(2));
        rig.Intake.Sources.SetLimits(new IntakeSources.Limits(Paused: true));
        rig.Labeled(7);
        await rig.Poll();
        Assert.Empty(rig.Starter.Started);
        Assert.Contains(rig.State.Waiting, w => w.Reason == "paused");

        rig.Intake.Sources.SetLimits(new IntakeSources.Limits());
        await rig.Poll();
        Assert.Single(rig.Starter.Started);
    }

    [Fact]
    public void The_limits_are_kept_beside_the_switches_and_a_new_process_reads_them()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sources.json");
        var sources = IntakeSources.Load(path);
        var limits = new IntakeSources.Limits(3, 1, 2.5m, true);

        Assert.True(sources.SetLimits(limits));
        Assert.False(sources.SetLimits(limits));
        Assert.Equal(limits, IntakeSources.Load(path).Current);
    }

    [Fact]
    public async Task The_console_reads_and_sets_the_limits_and_refuses_a_bad_one()
    {
        using var rig = new IntakeRig(Profile(2));
        await using var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), intake: rig.Intake);

        var before = await server.Http.GetFromJsonAsync<JsonElement>("/v1/intake/limits");
        Assert.Equal((2, 2), (before.GetProperty("max_concurrent").GetProperty("profile").GetInt32(), before.GetProperty("max_concurrent").GetProperty("effective").GetInt32()));

        var put = await server.Http.PutAsJsonAsync("/v1/intake/limits", new { max_concurrent = 1, max_usd_total = 1.5, paused = true });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var after = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, after.GetProperty("max_concurrent").GetProperty("effective").GetInt32());
        Assert.Equal(1.5m, after.GetProperty("max_usd_total").GetProperty("override").GetDecimal());
        Assert.True(after.GetProperty("paused").GetBoolean());
        Assert.Equal(new IntakeSources.Limits(1, null, 1.5m, true), rig.Sources.Current);

        Assert.Equal(HttpStatusCode.BadRequest, (await server.Http.PutAsJsonAsync("/v1/intake/limits", new { max_concurrent = 11 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Http.PutAsJsonAsync("/v1/intake/limits", new { max_usd_total = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Http.PutAsJsonAsync("/v1/intake/limits", new { paused = "yes" })).StatusCode);
    }

    [Fact]
    public async Task Without_an_intake_in_the_profile_a_change_is_a_conflict()
    {
        await using var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        Assert.False((await server.Http.GetFromJsonAsync<JsonElement>("/v1/intake/limits")).GetProperty("configured").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await server.Http.PutAsJsonAsync("/v1/intake/limits", new { paused = true })).StatusCode);
    }
}
