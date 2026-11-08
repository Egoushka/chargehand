using Chargehand.Contracts;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Tests;

/// <summary>Issue 429: intake adds a median 9 s to every run, and automation reviews already send a complete request. With
/// <c>skip_structured_intake</c> on, such a request dispatches on a spec built from its own inputs, without an intake call.</summary>
public class StructuredIntakeTests
{
    private const string Diff = "--- a/README.md\n+++ b/README.md\n@@ -1 +1 @@\n-hello\n+hello world\n";

    private static RunRequest Review(RepositoryRef repo, string diff = Diff, bool interactive = false) =>
        new("request/v1", "Review pull request o/r#1.", new RequestContext(interactive, "review", Repository: repo),
            [new CallerInput("goal", "goal", "Make the README greet the world."), new CallerInput("diff", "diff", diff), new CallerInput("tests", "test-output", "exit 0")]);

    private static Orchestrator Orchestrator(ScriptedRuntime runtime, string root, bool skip, JsonlRunLog? log = null) =>
        new(Runs.Profile(root) with { SkipStructuredIntake = skip }, runtime, "2.0.16", Repo.Root, log ?? new JsonlRunLog(Path.Combine(root, "log.jsonl")), new Dictionary<string, int>());

    [Fact]
    public async Task A_structured_review_dispatches_without_an_intake_call_when_the_flag_is_on()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));

        var result = await Orchestrator(runtime, root.Path, skip: true, log).RunAsync(Review(repo), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Empty(runtime.IntakePrompts);
        Assert.Single(runtime.Created);
        var data = await log.ReadAllAsync(CancellationToken.None);
        Assert.DoesNotContain(data.Calls, c => c.Kind == "intake");
        var run = Assert.Single(data.Runs);
        Assert.True(run.IntakeSkipped);
        Assert.Equal("Make the README greet the world.", run.Spec!.Goal);
        Assert.Equal(TaskAction.Answer, run.Spec.Action);
    }

    [Fact]
    public async Task With_the_flag_off_the_same_review_calls_intake()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);

        await Orchestrator(runtime, root.Path, skip: false).RunAsync(Review(repo), CancellationToken.None);

        Assert.Single(runtime.IntakePrompts);
    }

    [Fact]
    public async Task An_interactive_request_or_a_preset_with_other_actions_still_calls_intake()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var orchestrator = Orchestrator(runtime, root.Path, skip: true);

        await orchestrator.RunAsync(Review(repo, interactive: true), CancellationToken.None);
        await orchestrator.RunAsync(Runs.CheapRequest(repo) with { Inputs = [new CallerInput("goal", "goal", "Read the README.")] }, CancellationToken.None);

        Assert.Equal(2, runtime.IntakePrompts.Count);
    }

    [Fact]
    public async Task A_skipped_intake_still_refuses_a_diff_that_is_not_a_patch()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);

        var result = await Orchestrator(runtime, root.Path, skip: true).RunAsync(Review(repo, diff: "see attached"), CancellationToken.None);

        Assert.Equal(ResultStatus.NeedsInput, result.Status);
        Assert.Empty(runtime.IntakePrompts);
        Assert.Empty(runtime.Created);
    }

    [Fact]
    public void The_routing_report_states_the_latency_a_skipped_intake_saved()
    {
        var chain = new PromptChain([], new AsSent("2.0.16", "generate", "p/small", "2026-11-07"));
        static RunRecord Run(string id, bool? skipped) => new(id, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "review", "answer", null, Runs.Completed(), "answer", IntakeSkipped: skipped);
        CallRecord Intake(string id, double ms) => new(id, "intake", "intake", null, null, "p/small", DateTimeOffset.UnixEpoch, ms, null, null, chain);
        var data = new RunLogData([], [Run("a", null), Run("b", null), Run("c", null), Run("d", true), Run("e", true)],
            [Intake("a", 4000), Intake("b", 9000), Intake("c", 40000)], []);

        Assert.Contains("Intake skipped on 2 runs: about 18.0 s saved at the median intake latency of 9.0 s.", RoutingReport.Build(data), StringComparison.Ordinal);
    }
}
