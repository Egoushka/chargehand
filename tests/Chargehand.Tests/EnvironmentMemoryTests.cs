using Chargehand.Contracts;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Memory.Api;

namespace Chargehand.Tests;

/// <summary>Issue 434: an environment fact one session retains for a repository reaches the next session's prompt for that repository, and no other.</summary>
public class EnvironmentMemoryTests
{
    private const string Fact = "Environment: the tests need Docker.";

    private static readonly string EnvironmentReply = WorkerReplyWith(Fact);

    private static string WorkerReplyWith(string claim) => $$"""
        ```json
        {"status":"completed","summary":"Ran it.","claims":[{"text":"The README says hello.","evidence":["e1"],"confidence":0.9},{"text":"{{claim}}","evidence":["e1"],"confidence":0.8}],
         "evidence":[{"id":"e1","kind":"file","locator":"README.md:1"}],"artifacts":[],"open_questions":[],"confidence":0.9}
        ```
        """;

    /// <summary>A memory that keeps what is retained and answers a recall with the items that name the repository the query names.</summary>
    private sealed class Store : IMemoryProvider
    {
        public List<(string Text, IReadOnlyList<string>? Tags)> Items { get; } = [];

        public List<string> Queries { get; } = [];

        public Task<IReadOnlyList<RecalledMemory>> RecallAsync(string query, MemoryScope scope, CancellationToken ct)
        {
            Queries.Add(query);
            IReadOnlyList<RecalledMemory> found = [.. Items.Where(i => i.Tags?.Contains(EnvironmentFacts.Tag) == true && query.Contains(i.Text.Split('\n')[0].Split(' ')[1], StringComparison.Ordinal))
                .SelectMany(i => i.Text.Split('\n').Skip(1)).Select((t, n) => new RecalledMemory($"m{n}", t.TrimStart('-', ' ')))];
            return Task.FromResult(found);
        }

        public Task RetainAsync(MemoryItem item, MemoryScope scope, CancellationToken ct)
        {
            Items.Add((item.Text, item.Tags));
            return Task.CompletedTask;
        }

        public Task InvalidateAsync(string id, string reason, MemoryScope scope, CancellationToken ct) => throw new NotSupportedException();
    }

    private static async Task<ScriptedRuntime> Run(Store store, string reply, string workerRoot, string origin)
    {
        var repo = Runs.GitRepo(workerRoot);
        Runs.SetOrigin(repo, origin);
        var runtime = new ScriptedRuntime(reply);
        var stack = new MemoryStack([new MemorySource("hindsight", store, new MemoryScope("hindsight", "ns"), new MemoryLimits(), Retain: true)]);
        var orchestrator = new Orchestrator(Runs.Profile(workerRoot), runtime, "2.0.16", Repo.Root, new JsonlRunLog(Path.Combine(workerRoot, "log.jsonl")), new Dictionary<string, int>(), stack);
        var result = await orchestrator.RunAsync(Runs.CheapRequest(repo), CancellationToken.None);
        Assert.Equal(ResultStatus.Completed, result.Status);
        return runtime;
    }

    [Fact]
    public async Task A_fact_one_session_retains_is_in_the_next_sessions_prompt_for_the_same_repository()
    {
        var store = new Store();
        using var first = new TempDir();
        using var second = new TempDir();

        var before = await Run(store, EnvironmentReply, first.Path, "https://example.com/team/proj.git");
        var after = await Run(store, Runs.WorkerReply, second.Path, "https://example.com/team/proj.git");

        Assert.DoesNotContain(Fact, string.Join("\n", before.Prompts));
        Assert.Contains(Fact, string.Join("\n", after.Prompts));
    }

    [Fact]
    public async Task A_fact_stays_out_of_the_prompt_for_another_repository()
    {
        var store = new Store();
        using var first = new TempDir();
        using var second = new TempDir();

        await Run(store, EnvironmentReply, first.Path, "https://example.com/team/proj.git");
        var other = await Run(store, Runs.WorkerReply, second.Path, "https://example.com/team/other.git");

        Assert.DoesNotContain(Fact, string.Join("\n", other.Prompts));
    }

    [Fact]
    public void Only_a_completed_results_prefixed_claims_are_environment_facts()
    {
        var claims = new[] { new Claim("  environment:   Gradle is\nmissing. ", ["e1"], 0.9), new Claim("The README says hello.", ["e1"], 0.9), new Claim("Environment:", ["e1"], 0.9) };
        var completed = Runs.Completed() with { Claims = claims };

        Assert.Equal(["environment: Gradle is missing."], EnvironmentFacts.From(completed));
        Assert.Empty(EnvironmentFacts.From(completed with { Status = ResultStatus.Failed }));
    }
}
