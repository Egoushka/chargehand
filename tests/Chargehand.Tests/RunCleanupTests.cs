using System.Collections.Concurrent;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.RunLog;
using Chargehand.Runtime;

namespace Chargehand.Tests;

/// <summary>ADR 0034: a runtime that keeps a run's services (OpenCode) is told when the run's nodes are done, however they ended.</summary>
public class RunCleanupTests
{
    private const string Docs = "    services:\n      - server: team-docs\n        tools: [search_docs, read_doc]\n";

    private static readonly ServiceGrant Grant = new("team-docs",
        new HttpServiceTransport(new Uri("https://mcp.example.internal/docs"), new Dictionary<string, string> { ["Authorization"] = "Bearer s3cret" }),
        ["search_docs", "read_doc"], new string('a', 64));

    private sealed class Resolver(params ServiceGrant[] grants) : IServiceResolver
    {
        public Task<ResolvedServices> ResolveAsync(IReadOnlyList<ServiceUse> uses, CancellationToken ct) =>
            Task.FromResult(new ResolvedServices(grants, [.. grants.Select(g => new ServiceReport(g.Server, g.Tools, []))]));
    }

    /// <summary>A scripted runtime that records the runs it is told have ended.</summary>
    private sealed class EndingRuntime(ScriptedRuntime inner) : IWorkerRuntime, IRunCleanup
    {
        public ConcurrentQueue<string> Ended { get; } = new();

        public Exception? EndFails { get; init; }

        public Task EndRunAsync(string runId, CancellationToken ct)
        {
            Ended.Enqueue(runId);
            return EndFails is null ? Task.CompletedTask : Task.FromException(EndFails);
        }

        public Task<string> GenerateAsync(ModelRef? model, string prompt, CancellationToken ct) => inner.GenerateAsync(model, prompt, ct);

        public Task<WorkerSession> CreateAsync(NodeSpec spec, CancellationToken ct) => inner.CreateAsync(spec, ct);

        public Task SetInstructionAsync(string sessionId, string key, string value, CancellationToken ct) => inner.SetInstructionAsync(sessionId, key, value, ct);

        public Task SubmitAsync(string sessionId, string text, CancellationToken ct) => inner.SubmitAsync(sessionId, text, ct);

        public Task<IdleOutcome> AwaitIdleAsync(string sessionId, CancellationToken ct) => inner.AwaitIdleAsync(sessionId, ct);

        public Task InterruptAsync(string sessionId, CancellationToken ct) => inner.InterruptAsync(sessionId, ct);

        public Task<IReadOnlyList<WorkerMessage>> ReadMessagesAsync(string sessionId, CancellationToken ct) => inner.ReadMessagesAsync(sessionId, ct);

        public Task<IReadOnlyList<PermissionRequest>> PendingPermissionsAsync(string sessionId, CancellationToken ct) => inner.PendingPermissionsAsync(sessionId, ct);

        public Task AnswerPermissionAsync(string sessionId, string requestId, PermissionDecision decision, string? message, CancellationToken ct) =>
            inner.AnswerPermissionAsync(sessionId, requestId, decision, message, ct);

        public Task<WorkerSession> ForkAsync(string sessionId, string? beforeMessageId, CancellationToken ct) => inner.ForkAsync(sessionId, beforeMessageId, ct);

        public Task CompactAsync(string sessionId, CancellationToken ct) => inner.CompactAsync(sessionId, ct);

        public Task<IReadOnlyList<FileDiff>> DiffAsync(string sessionId, CancellationToken ct) => inner.DiffAsync(sessionId, ct);
    }

    private static RunRequest Request(RepositoryRef repo, string preset) => new("request/v1", "What does the README say?", new RequestContext(false, preset, Repository: repo));

    private static Orchestrator Make(string workerRoot, string installRoot, IWorkerRuntime runtime, IRunLog log, IServiceResolver? resolver = null) =>
        new(Runs.Profile(workerRoot), runtime, "2.0.16", installRoot, log, new Dictionary<string, int>(), null, resolver);

    [Fact]
    public async Task The_node_carries_the_run_id_and_the_runtime_is_told_when_the_run_is_done()
    {
        using var root = new TempDir();
        using var presets = new PresetRoot("docs", Docs);
        var repo = Runs.GitRepo(root.Path);
        var scripted = new ScriptedRuntime(Runs.WorkerReply);
        var runtime = new EndingRuntime(scripted);

        var result = await Make(root.Path, presets.Path, runtime, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), new Resolver(Grant)).RunAsync(Request(repo, "docs"), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal(result.TaskId, scripted.Created.Single().Metadata[IRunCleanup.RunMetadataKey]);
        Assert.Equal([result.TaskId], runtime.Ended);
    }

    [Fact]
    public async Task The_runtime_is_told_when_a_node_fails_to_start()
    {
        using var root = new TempDir();
        using var presets = new PresetRoot("docs", Docs);
        var repo = Runs.GitRepo(root.Path);
        var scripted = new ScriptedRuntime(Runs.WorkerReply);
        scripted.CreateFailures.Enqueue(new InvalidOperationException("no session"));
        var runtime = new EndingRuntime(scripted);

        var result = await Make(root.Path, presets.Path, runtime, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), new Resolver(Grant)).RunAsync(Request(repo, "docs"), CancellationToken.None);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Equal([result.TaskId], runtime.Ended);
    }

    [Fact]
    public async Task The_runtime_is_told_when_the_run_is_cancelled()
    {
        using var root = new TempDir();
        using var presets = new PresetRoot("docs", Docs);
        var repo = Runs.GitRepo(root.Path);
        var scripted = new ScriptedRuntime(Runs.WorkerReply) { Hold = new TaskCompletionSource() };
        var runtime = new EndingRuntime(scripted);
        using var cts = new CancellationTokenSource();

        var run = Make(root.Path, presets.Path, runtime, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), new Resolver(Grant)).RunAsync(Request(repo, "docs"), cts.Token);
        while (scripted.Created.IsEmpty)
            await Task.Delay(10);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Single(runtime.Ended);
    }

    [Fact]
    public async Task A_run_with_no_granted_service_does_not_bother_the_runtime()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var runtime = new EndingRuntime(new ScriptedRuntime(Runs.WorkerReply));

        var result = await Make(root.Path, Repo.Root, runtime, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"))).RunAsync(Runs.CheapRequest(repo), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Empty(runtime.Ended);
    }

    [Fact]
    public async Task A_runtime_that_fails_to_clean_up_does_not_fail_the_run()
    {
        using var root = new TempDir();
        using var presets = new PresetRoot("docs", Docs);
        var repo = Runs.GitRepo(root.Path);
        var runtime = new EndingRuntime(new ScriptedRuntime(Runs.WorkerReply)) { EndFails = new IOException("closed") };

        var result = await Make(root.Path, presets.Path, runtime, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), new Resolver(Grant)).RunAsync(Request(repo, "docs"), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Single(runtime.Ended);
    }
}
