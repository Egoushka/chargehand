using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Tests;

/// <summary>Issue 448: 15 reviews re-reviewed an identical diff. A review of the same head commit and diff as a completed one returns the stored review.</summary>
public class ReviewReuseTests
{
    private const string Diff = "--- a/README.md\n+++ b/README.md\n@@ -1 +1 @@\n-hello\n+hello world\n";

    private static RunRequest Review(RepositoryRef repo, string diff) =>
        new("request/v1", "Review the change against its goal.", new RequestContext(false, "review", Repository: repo),
            [new CallerInput("goal", "goal", "Make the README greet the world."), new CallerInput("diff", "diff", diff), new CallerInput("tests", "test-output", "exit 0")]);

    private static Orchestrator Orchestrator(ScriptedRuntime runtime, string root) =>
        new(Runs.Profile(root), runtime, "2.0.16", Repo.Root, new JsonlRunLog(Path.Combine(root, "log.jsonl")), new Dictionary<string, int>(), reuseReviews: true);

    [Fact]
    public async Task A_second_review_of_the_same_head_and_diff_returns_the_stored_review()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var orchestrator = Orchestrator(runtime, root.Path);

        var first = await orchestrator.RunAsync(Review(repo, Diff), CancellationToken.None);
        var second = await orchestrator.RunAsync(Review(repo, Diff), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, first.Status);
        Assert.Equal(ResultStatus.Completed, second.Status);
        Assert.Single(runtime.Created);
        Assert.Single(runtime.IntakePrompts);
        Assert.NotEqual(first.TaskId, second.TaskId);
        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(first.Claims.Select(c => c.Text), second.Claims.Select(c => c.Text));
        Assert.Equal(0, second.Usage.Usd);
        var reused = JsonDocument.Parse(second.Artifacts.Single(a => a.Kind == "reused-review").Content!).RootElement;
        Assert.Equal(first.TaskId, reused.GetProperty("run_id").GetString());
    }

    [Fact]
    public async Task A_review_of_another_diff_or_another_commit_runs_again()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var orchestrator = Orchestrator(runtime, root.Path);

        await orchestrator.RunAsync(Review(repo, Diff), CancellationToken.None);
        var otherDiff = await orchestrator.RunAsync(Review(repo, Diff.Replace("hello world", "hello there", StringComparison.Ordinal)), CancellationToken.None);
        File.WriteAllText(Path.Combine(repo.Path, "NOTES.md"), "notes\n");
        var otherCommit = await orchestrator.RunAsync(Review(Runs.Commit(repo, "NOTES.md"), Diff), CancellationToken.None);

        Assert.Equal(3, runtime.Created.Count);
        Assert.DoesNotContain(otherDiff.Artifacts, a => a.Kind == "reused-review");
        Assert.DoesNotContain(otherCommit.Artifacts, a => a.Kind == "reused-review");
    }

    [Fact]
    public async Task A_review_that_did_not_complete_is_not_reused()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        runtime.CreateFailures.Enqueue(new InvalidOperationException("no worker"));
        var orchestrator = Orchestrator(runtime, root.Path);

        var failed = await orchestrator.RunAsync(Review(repo, Diff), CancellationToken.None);
        var second = await orchestrator.RunAsync(Review(repo, Diff), CancellationToken.None);

        Assert.NotEqual(ResultStatus.Completed, failed.Status);
        Assert.Equal(ResultStatus.Completed, second.Status);
        Assert.Single(runtime.Created);
    }
}
