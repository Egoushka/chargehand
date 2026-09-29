using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Mcp;
using Chargehand.Memory;
using Chargehand.RunLog;
using static Chargehand.Tests.MemoryStackTests;

namespace Chargehand.Tests;

/// <summary>Goal 0.6: whole runs on a ScriptedRuntime with a memory stack; no test drove a memory run before.</summary>
public class MemoryRunTests
{
    private static Orchestrator Make(string root, ScriptedRuntime runtime, MemoryStack? stack, IRunLog log) =>
        new(Runs.Profile(root), runtime, "2.0.16", Repo.Root, log, new Dictionary<string, int>(), stack);

    [Fact]
    public async Task A_run_puts_labelled_facts_in_the_prompt_and_their_blocks_in_the_chain()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var stack = new MemoryStack([
            Source("hindsight", new Fake(_ => [new RecalledMemory("f1", "Deploys go through GitOps.")])),
            Source("notes", new Fake(_ => [new RecalledMemory("n1", "The API uses MediatR.")]))]);

        var result = await Make(root.Path, runtime, stack, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"))).RunAsync(Runs.CheapRequest(repo), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        var prompt = Assert.Single(runtime.Prompts);
        Assert.Contains("- [hindsight] Deploys go through GitOps.", prompt);
        Assert.Contains("- [notes] The API uses MediatR.", prompt);
        Assert.Equal(["memory/recall/hindsight", "memory/recall/notes"], result.PromptChain.Blocks.Where(b => b.Source == BlockSource.Runtime).Select(b => b.Name));
    }

    [Theory]
    [MemberData(nameof(ExceptionKinds), MemberType = typeof(MemoryStackTests))]
    public async Task A_run_survives_every_kind_of_provider_failure(string kind)
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));
        var stack = new MemoryStack([Source("broken", new Fake(fail: Kind(kind)), retain: true)]);

        var result = await Make(root.Path, new ScriptedRuntime(Runs.WorkerReply), stack, log).RunAsync(Runs.CheapRequest(repo), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        var report = (await log.ReadAsync(result.TaskId, CancellationToken.None)).Run!.Extensions!.Memory.Single();
        Assert.Equal("broken", report.Source);
        Assert.NotNull(report.RecallSkipped);
        Assert.NotNull(report.RetainSkipped);
    }

    [Fact]
    public async Task The_run_log_reports_what_each_source_recalled_and_retained()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));
        var keeps = new Fake(_ => [new RecalledMemory("f1", "Deploys go through GitOps."), new RecalledMemory("f2", "Releases are tagged.")]);
        var stack = new MemoryStack([Source("hindsight", keeps, retain: true), Source("notes", new Fake(_ => [new RecalledMemory("n1", "The API uses MediatR.")]))]);

        var result = await Make(root.Path, new ScriptedRuntime(Runs.WorkerReply), stack, log).RunAsync(Runs.CheapRequest(repo), CancellationToken.None);

        var lines = (await log.ReadAsync(result.TaskId, CancellationToken.None)).Run!.Extensions!.Lines();
        Assert.Equal(["memory hindsight: recalled 2, retained 1", "memory notes: recalled 1, retained 0"], lines);
        var retained = Assert.Single(keeps.Retained);
        Assert.Equal(result.TaskId, retained.DocumentId);
    }

    [Fact]
    public async Task A_run_without_memory_is_unchanged()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));

        var result = await Make(root.Path, runtime, null, log).RunAsync(Runs.CheapRequest(repo), CancellationToken.None);

        Assert.DoesNotContain("long-term memory", Assert.Single(runtime.Prompts), StringComparison.Ordinal);
        Assert.DoesNotContain(result.PromptChain.Blocks, b => b.Source == BlockSource.Runtime);
        Assert.Null((await log.ReadAsync(result.TaskId, CancellationToken.None)).Run!.Extensions);
    }

    [Fact]
    public async Task A_run_that_stops_before_it_executes_recalls_nothing_and_reports_nothing()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));
        var provider = new Fake(_ => [new RecalledMemory("f1", "Deploys go through GitOps.")]);
        var runtime = new ScriptedRuntime(Runs.WorkerReply, ScriptedRuntime.Spec("ask", """{"questions":["Which service?"]}"""));

        var result = await Make(root.Path, runtime, new MemoryStack([Source("hindsight", provider, retain: true)]), log).RunAsync(Runs.CheapRequest(repo), CancellationToken.None);

        Assert.Equal(ResultStatus.NeedsInput, result.Status);
        Assert.Empty(provider.Retained);
        Assert.Null((await log.ReadAsync(result.TaskId, CancellationToken.None)).Run!.Extensions);
    }

    private const string ThreeClaims = """
        ```json
        {"status":"completed","summary":"SUMMARY-MARKER","claims":[
           {"text":"The README says hello.","evidence":["e1"],"confidence":0.9},
           {"text":"The README has ninety-nine lines.","evidence":["e2"],"confidence":0.8},
           {"text":"The caller says v1 shipped.","evidence":["e3"],"confidence":0.7}],
         "evidence":[{"id":"e1","kind":"file","locator":"README.md:1"},{"id":"e2","kind":"file","locator":"README.md:99"},{"id":"e3","kind":"input","locator":"rel-v1"}],
         "artifacts":[],"open_questions":[],"confidence":0.8}
        ```
        """;

    private const string InputOnlyReply = """
        ```json
        {"status":"completed","summary":"SUMMARY-MARKER","claims":[{"text":"The caller says v1 shipped.","evidence":["e1"],"confidence":0.7}],
         "evidence":[{"id":"e1","kind":"input","locator":"rel-v1"}],"artifacts":[],"open_questions":[],"confidence":0.8}
        ```
        """;

    [Fact]
    public async Task A_run_retains_only_qualifying_claims()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var provider = new Fake();
        var stack = new MemoryStack([Source("notes", provider, retain: true)]);
        var request = Runs.CheapRequest(repo) with { Inputs = [new CallerInput("rel-v1", "signal", "Released v1.")] };

        var result = await Make(root.Path, new ScriptedRuntime(ThreeClaims), stack, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"))).RunAsync(request, CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Contains(result.OpenQuestions, q => q.StartsWith("Unverified: The README has ninety-nine lines.", StringComparison.Ordinal)); // README.md:99 does not resolve
        var item = Assert.Single(provider.Retained);
        Assert.Contains("- The README says hello. [README.md:1]", item.Text, StringComparison.Ordinal);
        Assert.Contains($"commit {repo.Commit[..12]}", item.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ninety-nine", item.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("caller says", item.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("SUMMARY-MARKER", item.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("What does the README say?", item.Text, StringComparison.Ordinal);
        Assert.Equal(result.TaskId, item.DocumentId);
        Assert.Equal(["chargehand"], item.Tags);
    }

    [Fact]
    public async Task A_profile_with_a_memory_list_recalls_from_and_retains_to_its_mcp_server()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        using var config = new TempDir();
        var profile = Profile.Load(config.Write("p.json",
            """{"schema":"profile/v1","mcp_servers":{"gw":{"url":"https://mcp.example.internal/mcp"}},"memory":[""" + MemoryConfigTests.Hindsight + "]}"));
        await using var server = new FakeMcpServer(
            new FakeTool("recall", _ => FakeMcpServer.Text("""{"results":[{"id":"f1","text":"Deploys go through GitOps."}]}""")),
            new FakeTool("retain", _ => FakeMcpServer.Text("queued")),
            new FakeTool("invalidate_memory", _ => FakeMcpServer.Text("ok")));
        await using var pool = new McpConnectionPool(profile.McpServers!, _ => "", async (_, _, _) => await server.TransportAsync());
        var runtime = new ScriptedRuntime(ThreeClaims);
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));
        var request = Runs.CheapRequest(repo) with { Inputs = [new CallerInput("rel-v1", "signal", "Released v1.")] };

        var result = await Make(root.Path, runtime, MemoryStacks.From(profile, pool, _ => throw new InvalidOperationException("no object form here")), log)
            .RunAsync(request, CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Contains("- [hindsight] Deploys go through GitOps.", runtime.Prompts.First(), StringComparison.Ordinal);
        Assert.Equal("chargehand", Assert.Single(server.Calls, c => c.Tool == "recall").Arguments["bank_id"].GetString());
        var retain = Assert.Single(server.Calls, c => c.Tool == "retain").Arguments;
        Assert.Contains("- The README says hello. [README.md:1]", retain["content"].GetString(), StringComparison.Ordinal);
        Assert.Equal(result.TaskId, retain["document_id"].GetString());
        Assert.Equal(["chargehand"], retain["tags"].EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(["memory hindsight: recalled 1, retained 1"], (await log.ReadAsync(result.TaskId, CancellationToken.None)).Run!.Extensions!.Lines());
    }

    [Fact]
    public async Task The_retained_repository_is_the_normalised_origin_and_the_directory_name_without_one()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var provider = new Fake();
        var stack = new MemoryStack([Source("notes", provider, retain: true)]);
        var orchestrator = Make(root.Path, new ScriptedRuntime(Runs.WorkerReply), stack, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")));

        await orchestrator.RunAsync(Runs.CheapRequest(repo), CancellationToken.None);
        Runs.SetOrigin(repo, "https://user:tok@git.example.com/team/proj.git");
        await orchestrator.RunAsync(Runs.CheapRequest(repo), CancellationToken.None);

        var firstLines = provider.Retained.Select(i => i.Text.Split('\n')[0]).ToList();
        Assert.StartsWith("Repository: repo, commit ", firstLines[0], StringComparison.Ordinal);
        Assert.StartsWith("Repository: git.example.com/team/proj, commit ", firstLines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("tok@", firstLines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_retained_when_no_claim_qualifies_or_there_is_no_commit()
    {
        using var root = new TempDir();
        var provider = new Fake();
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));
        var stack = new MemoryStack([Source("notes", provider, retain: true)]);

        var result = await Make(root.Path, new ScriptedRuntime(Runs.DraftReply), stack, log).RunAsync(Runs.DraftRequest(), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Empty(provider.Retained);
        Assert.Equal("no commit", (await log.ReadAsync(result.TaskId, CancellationToken.None)).Run!.Extensions!.Memory.Single().RetainSkipped);
    }

    [Fact]
    public async Task Nothing_is_retained_when_every_claim_rests_on_the_request()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var provider = new Fake();
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));
        var stack = new MemoryStack([Source("notes", provider, retain: true)]);
        var request = Runs.CheapRequest(repo) with { Inputs = [new CallerInput("rel-v1", "signal", "Released v1.")] };

        var result = await Make(root.Path, new ScriptedRuntime(InputOnlyReply), stack, log).RunAsync(request, CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Empty(provider.Retained);
        var report = (await log.ReadAsync(result.TaskId, CancellationToken.None)).Run!.Extensions!.Memory.Single();
        Assert.Equal("no claim qualified", report.RetainSkipped);
        Assert.Equal(0, report.Retained);
    }

    [Fact]
    public void The_object_form_of_memory_becomes_one_source_named_hindsight()
    {
        var stack = MemoryStack.ForObjectForm(new MemorySettings("hindsight", "http://memory.example.internal:8888", "ns", Retain: true), new Fake());

        var source = Assert.Single(stack.Sources);
        Assert.Equal(("hindsight", true), (source.Name, source.Retain));
        Assert.Equal(new MemoryScope("hindsight", "ns"), source.Scope);
    }

    [Fact]
    public void The_object_form_retains_nothing_unless_it_says_so()
    {
        var stack = MemoryStack.ForObjectForm(new MemorySettings("hindsight", "http://memory.example.internal:8888", "ns"), new Fake());

        Assert.False(Assert.Single(stack.Sources).Retain);
    }
}
