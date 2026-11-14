using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Tests;

/// <summary>Goal 0.5: the read-only preset the change command's review step runs under.</summary>
public class ReviewPresetTests
{
    private const string FindingReply = """
        ```json
        {"status":"completed","summary":"One finding.",
         "claims":[{"text":"The new branch in greet() never returns a value.","evidence":["e1","e2"],"confidence":0.8}],
         "evidence":[{"id":"e1","kind":"input","locator":"diff"},{"id":"e2","kind":"file","locator":"README.md:1"}],
         "artifacts":[],"open_questions":[],"confidence":0.8}
        ```
        """;

    private static RunRequest Review(RepositoryRef repo, string diff) => new("request/v1",
        "Review the change against its goal.", new RequestContext(false, "review", Repository: repo),
        [new CallerInput("goal", "goal", "Make the README greet."), new CallerInput("diff", "diff", diff),
         new CallerInput("tests", "test-output", "exit 0")], []);

    [Fact]
    public void Is_read_only_and_answers_only()
    {
        var preset = Preset.Load(Repo.Path("presets"), "review");
        Assert.Equal(["answer"], preset.AllowedActions);
        var rules = preset.NodeKinds["worker"].Permissions.Select(p => $"{p.Action} {p.Resource} {p.Effect}").ToList();
        Assert.Contains("edit * deny", rules);
        Assert.Contains("shell * deny", rules);
    }

    [Fact]
    public void The_worker_compacts_well_before_its_hard_budget_so_a_long_diff_does_not_cut_the_review_off()
    {
        // issue #449: with no trigger_tokens the session never compacts mid-turn (WorkerNode.Watch), so a review
        // that reads a large diff plus surrounding files just grows until it hits max_input_tokens and is
        // interrupted. A trigger well under the hard cap lets it compact and keep going instead.
        var worker = Preset.Load(Repo.Path("presets"), "review").NodeKinds["worker"];
        Assert.NotNull(worker.Compaction?.TriggerTokens);
        Assert.True(worker.Compaction!.TriggerTokens < worker.Budget.MaxInputTokens);
    }

    [Fact]
    public async Task Findings_cite_the_diff_input_and_resolve()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var r = await Runs.Orchestrator(new ScriptedRuntime(FindingReply), root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")))
            .RunAsync(Review(repo, "--- a/README.md\n+++ b/README.md\n@@ -1 +1 @@\n-hello\n+hello world\n"), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, r.Status);
        Assert.Single(r.Claims);
        Assert.Contains(r.Evidence, e => (e.Kind, e.Locator) == (EvidenceKind.Input, "diff"));
        Assert.DoesNotContain(r.OpenQuestions, q => q.StartsWith("Unverified:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Intake_is_given_the_goal_diff_and_test_output_the_request_carries()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(FindingReply);

        await Runs.Orchestrator(runtime, root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")))
            .RunAsync(Review(repo, "--- a/README.md\n+++ b/README.md\n@@ -1 +1 @@\n-hello\n+hello world\n"), CancellationToken.None);

        // Told only that the inputs exist, intake asked the caller for "the actual goal, diff and test output".
        var prompt = runtime.IntakePrompts.Single();
        Assert.Contains("Make the README greet.", prompt, StringComparison.Ordinal);
        Assert.Contains("+hello world", prompt, StringComparison.Ordinal);
        Assert.Contains("exit 0", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_truncated_diff_input_still_resolves()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var diff = "--- a/x\n+++ b/x\n@@ -0,0 +1,20000 @@\n" + string.Concat(Enumerable.Repeat("+x\n", 20_000)) + "\n[diff truncated at 60000 characters]\n";
        var r = await Runs.Orchestrator(new ScriptedRuntime(FindingReply), root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")))
            .RunAsync(Review(repo, diff), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, r.Status);
        Assert.Contains(r.Evidence, e => (e.Kind, e.Locator) == (EvidenceKind.Input, "diff"));
    }

    [Fact]
    public async Task A_failed_review_returns_its_error_with_an_action()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(FindingReply);
        runtime.CreateFailures.Enqueue(new ChargehandException(ErrorCode.RuntimeUnavailable, "down", "Start it."));
        var r = await Runs.Orchestrator(runtime, root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")))
            .RunAsync(Review(repo, "--- a/x\n+++ b/x\n@@ -0,0 +1 @@\n+x\n"), CancellationToken.None);

        Assert.Equal(ResultStatus.Failed, r.Status);
        Assert.Equal(ErrorCode.RuntimeUnavailable, r.Error?.Code);
        Assert.False(string.IsNullOrEmpty(r.Error!.Action));
    }
}
