using Chargehand.Contracts;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Tests;

/// <summary>Issue #435: research and review prompts load a repository context pack (map, test command, conventions) first, so they
/// do not spend their own budget rediscovering what the repository's own files already say.</summary>
public class RepositoryPackPromptTests
{
    private const string FindingReply = """
        ```json
        {"status":"completed","summary":"One finding.",
         "claims":[{"text":"The new branch in greet() never returns a value.","evidence":["e1","e2"],"confidence":0.8}],
         "evidence":[{"id":"e1","kind":"input","locator":"diff"},{"id":"e2","kind":"file","locator":"README.md:1"}],
         "artifacts":[],"open_questions":[],"confidence":0.8}
        ```
        """;


    private static Orchestrator StructuredOrchestrator(ScriptedRuntime runtime, string root, JsonlRunLog log) =>
        new(Runs.Profile(root) with { SkipStructuredIntake = true }, runtime, "2.0.16", Repo.Root, log, new Dictionary<string, int>());

    private static RunRequest Research(RepositoryRef repo) => new("request/v1",
        "Answer without changing anything: which files does this change touch?", new RequestContext(false, "default", Repository: repo),
        [new CallerInput("goal", "goal", "Answer without changing anything: which files does this change touch?")]);

    private static RunRequest Review(RepositoryRef repo, string diff) => new("request/v1",
        "Review the change against its goal.", new RequestContext(false, "review", Repository: repo),
        [new CallerInput("goal", "goal", "Make the README greet."), new CallerInput("diff", "diff", diff), new CallerInput("tests", "test-output", "exit 0")]);

    [Fact]
    public async Task A_research_prompt_includes_the_repository_context_pack()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        File.WriteAllText(Path.Combine(repo.Path, "go.mod"), "module x\n");
        File.WriteAllText(Path.Combine(repo.Path, "CLAUDE.md"), "Never commit secrets.\n");
        repo = Runs.Commit(repo, "go.mod", "CLAUDE.md");
        var runtime = new ScriptedRuntime(Runs.WorkerReply);

        await StructuredOrchestrator(runtime, root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")))
            .RunAsync(Research(repo), CancellationToken.None);

        var prompt = Assert.Single(runtime.Prompts);
        Assert.Contains("Repository context pack", prompt, StringComparison.Ordinal);
        Assert.Contains("uses go", prompt, StringComparison.Ordinal);
        Assert.Contains("Never commit secrets.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_review_prompt_includes_the_repository_context_pack()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        File.WriteAllText(Path.Combine(repo.Path, "AGENTS.md"), "Run the tests before a PR.\n");
        repo = Runs.Commit(repo, "AGENTS.md");
        var runtime = new ScriptedRuntime(FindingReply);

        await StructuredOrchestrator(runtime, root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")))
            .RunAsync(Review(repo, "--- a/README.md\n+++ b/README.md\n@@ -1 +1 @@\n-hello\n+hello world\n"), CancellationToken.None);

        var prompt = Assert.Single(runtime.Prompts);
        Assert.Contains("Repository context pack", prompt, StringComparison.Ordinal);
        Assert.Contains("Run the tests before a PR.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_research_call_on_the_same_commit_reuses_the_cached_pack()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        File.WriteAllText(Path.Combine(repo.Path, "CLAUDE.md"), "First version.\n");
        repo = Runs.Commit(repo, "CLAUDE.md");
        var runtime = new ScriptedRuntime(Runs.WorkerReply, Runs.WorkerReply);
        var orchestrator = StructuredOrchestrator(runtime, root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")));

        await orchestrator.RunAsync(Research(repo), CancellationToken.None);
        // The source repository's CLAUDE.md changes, but the clone under worker_root for this commit does not: a second
        // call on the same commit must still see the content the pack cached on the first call, not a rescan.
        File.WriteAllText(Path.Combine(repo.Path, "CLAUDE.md"), "Second version.\n");
        await orchestrator.RunAsync(Research(repo), CancellationToken.None);

        Assert.Equal(2, runtime.Prompts.Count);
        Assert.All(runtime.Prompts, p => Assert.Contains("First version.", p, StringComparison.Ordinal));
        Assert.DoesNotContain(runtime.Prompts, p => p.Contains("Second version.", StringComparison.Ordinal));
    }
}
