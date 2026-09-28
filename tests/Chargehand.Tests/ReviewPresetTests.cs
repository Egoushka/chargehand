using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.RunLog;

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
    public async Task A_truncated_diff_input_still_resolves()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var diff = string.Concat(Enumerable.Repeat("+x\n", 20_000)) + "\n[diff truncated at 60000 characters]\n";
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
            .RunAsync(Review(repo, "+x\n"), CancellationToken.None);

        Assert.Equal(ResultStatus.Failed, r.Status);
        Assert.Equal(ErrorCode.RuntimeUnavailable, r.Error?.Code);
        Assert.False(string.IsNullOrEmpty(r.Error!.Action));
    }
}
