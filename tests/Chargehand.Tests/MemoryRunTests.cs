using Chargehand.Config;
using Chargehand.Contracts;
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
