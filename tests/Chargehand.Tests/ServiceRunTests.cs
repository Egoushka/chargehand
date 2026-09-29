using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Prompts;
using Chargehand.RunLog;
using Chargehand.Runtime;

namespace Chargehand.Tests;

/// <summary>Goal 0.6: the orchestrator resolves a preset's services once per run and hands the grants to the node.</summary>
public class ServiceRunTests
{
    private const string Docs = "    services:\n      - server: team-docs\n        tools: [search_docs, read_doc]\n";

    private static readonly ServiceGrant Grant = new("team-docs",
        new HttpServiceTransport(new Uri("https://mcp.example.internal/docs"), new Dictionary<string, string> { ["Authorization"] = "Bearer s3cret" }),
        ["search_docs", "read_doc"], new string('a', 64));

    private sealed class FakeResolver(ResolvedServices? resolved = null, Exception? fail = null, Action? onAsk = null) : IServiceResolver
    {
        public List<IReadOnlyList<ServiceUse>> Asked { get; } = [];

        public Task<ResolvedServices> ResolveAsync(IReadOnlyList<ServiceUse> uses, CancellationToken ct)
        {
            Asked.Add(uses);
            onAsk?.Invoke();
            return fail is null ? Task.FromResult(resolved!) : Task.FromException<ResolvedServices>(fail);
        }
    }

    private static RunRequest Request(RepositoryRef repo, string preset) => new("request/v1", "What does the README say?", new RequestContext(false, preset, Repository: repo));

    private static Orchestrator Make(string workerRoot, string installRoot, ScriptedRuntime runtime, IRunLog log, IServiceResolver resolver) =>
        new(Runs.Profile(workerRoot), runtime, "2.0.16", installRoot, log, new Dictionary<string, int>(), null, resolver);

    private static string PlainToolsHash() =>
        PromptChains.ToolsSha256("2.0.16", "build", Preset.Load(Repo.Path("presets"), "cheap").NodeKinds["worker"].Rules);

    [Fact]
    public async Task A_granted_service_reaches_the_node_spec_and_changes_the_tools_hash()
    {
        using var root = new TempDir();
        using var presets = new PresetRoot("docs", Docs);
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var resolver = new FakeResolver(new ResolvedServices([Grant], [new ServiceReport("team-docs", ["search_docs", "read_doc"], [])]));

        var result = await Make(root.Path, presets.Path, runtime, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), resolver).RunAsync(Request(repo, "docs"), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal([new ServiceUse("team-docs", ["search_docs", "read_doc"])], Assert.Single(resolver.Asked));
        Assert.Same(Grant, Assert.Single(runtime.Created.Single().Services!));
        Assert.NotEqual(PlainToolsHash(), result.PromptChain.AsSent.ToolsSha256);
    }

    [Fact]
    public async Task No_services_leave_the_tools_hash_alone()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var resolver = new FakeResolver(new ResolvedServices([], []));

        var result = await Make(root.Path, Repo.Root, runtime, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), resolver).RunAsync(Runs.CheapRequest(repo), CancellationToken.None);

        Assert.Empty(resolver.Asked);
        Assert.Equal(PlainToolsHash(), result.PromptChain.AsSent.ToolsSha256);
        Assert.True(runtime.Created.Single().Services is null or { Count: 0 });
    }

    [Fact]
    public async Task A_preset_with_services_and_no_resolver_runs_without_them()
    {
        using var root = new TempDir();
        using var presets = new PresetRoot("docs", Docs);
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);

        var result = await new Orchestrator(Runs.Profile(root.Path), runtime, "2.0.16", presets.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), new Dictionary<string, int>())
            .RunAsync(Request(repo, "docs"), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.True(runtime.Created.Single().Services is null or { Count: 0 });
        Assert.Equal(PlainToolsHash(), result.PromptChain.AsSent.ToolsSha256);
    }

    [Fact]
    public async Task A_dropped_service_does_not_fail_the_run_and_is_in_the_run_log()
    {
        using var root = new TempDir();
        using var presets = new PresetRoot("docs", Docs);
        var repo = Runs.GitRepo(root.Path);
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));
        var resolver = new FakeResolver(new ResolvedServices([], [new ServiceReport("team-docs", [], ["unreachable: connection refused"])]));

        var result = await Make(root.Path, presets.Path, new ScriptedRuntime(Runs.WorkerReply), log, resolver).RunAsync(Request(repo, "docs"), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        var service = Assert.Single((await log.ReadAsync(result.TaskId, CancellationToken.None)).Run!.Extensions!.Services);
        Assert.Equal(["unreachable: connection refused"], service.Issues);
    }

    [Fact]
    public async Task A_granted_server_the_worker_could_not_reach_is_an_issue_on_the_run()
    {
        using var root = new TempDir();
        using var presets = new PresetRoot("docs", Docs);
        var repo = Runs.GitRepo(root.Path);
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));
        var runtime = new ScriptedRuntime(Runs.WorkerReply) { Unavailable = { ["team-docs"] = "failed" } };
        var resolver = new FakeResolver(new ResolvedServices([Grant], [new ServiceReport("team-docs", ["search_docs", "read_doc"], [])]));

        var result = await Make(root.Path, presets.Path, runtime, log, resolver).RunAsync(Request(repo, "docs"), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        var service = Assert.Single((await log.ReadAsync(result.TaskId, CancellationToken.None)).Run!.Extensions!.Services);
        Assert.Equal("team-docs", service.Server);
        Assert.Equal(["not_connected: failed"], service.Issues);
        Assert.Equal(["search_docs", "read_doc"], service.Tools);
        Assert.Equal("service team-docs: granted search_docs, read_doc (not_connected: failed)", Assert.Single((await log.ReadAsync(result.TaskId, CancellationToken.None)).Run!.Extensions!.Lines()));
    }

    [Fact]
    public async Task A_server_that_connected_adds_no_issue()
    {
        using var root = new TempDir();
        using var presets = new PresetRoot("docs", Docs);
        var repo = Runs.GitRepo(root.Path);
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));
        var resolver = new FakeResolver(new ResolvedServices([Grant], [new ServiceReport("team-docs", ["search_docs", "read_doc"], [])]));

        var result = await Make(root.Path, presets.Path, new ScriptedRuntime(Runs.WorkerReply), log, resolver).RunAsync(Request(repo, "docs"), CancellationToken.None);

        Assert.Empty(Assert.Single((await log.ReadAsync(result.TaskId, CancellationToken.None)).Run!.Extensions!.Services).Issues);
    }

    [Fact]
    public void The_same_unavailable_server_is_recorded_once_however_many_nodes_saw_it()
    {
        var collector = new ExtensionsCollector();
        collector.Serviced([new ServiceReport("team-docs", ["read_doc"], ["tool_missing: x"])]);

        collector.Unavailable("team-docs", "not_connected: failed");
        collector.Unavailable("team-docs", "not_connected: failed");
        collector.Unavailable("other", "not_connected: absent");

        var report = collector.ToReport()!.Services;
        Assert.Equal(["tool_missing: x", "not_connected: failed"], report.Single(s => s.Server == "team-docs").Issues);
        var other = report.Single(s => s.Server == "other");
        Assert.Equal(["not_connected: absent"], other.Issues);
        Assert.Empty(other.Tools);
    }

    [Fact]
    public async Task A_resolver_that_throws_does_not_fail_the_run()
    {
        using var root = new TempDir();
        using var presets = new PresetRoot("docs", Docs);
        var repo = Runs.GitRepo(root.Path);
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));

        var result = await Make(root.Path, presets.Path, new ScriptedRuntime(Runs.WorkerReply), log, new FakeResolver(fail: new IOException("closed"))).RunAsync(Request(repo, "docs"), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        var service = Assert.Single((await log.ReadAsync(result.TaskId, CancellationToken.None)).Run!.Extensions!.Services);
        Assert.Equal("team-docs", service.Server);
        Assert.Equal(["unreachable: closed"], service.Issues);
    }

    [Fact]
    public async Task The_runs_own_cancellation_propagates_out_of_the_resolver()
    {
        using var root = new TempDir();
        using var presets = new PresetRoot("docs", Docs);
        var repo = Runs.GitRepo(root.Path);
        using var cts = new CancellationTokenSource();
        var resolver = new FakeResolver(fail: new OperationCanceledException(), onAsk: cts.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Make(root.Path, presets.Path, new ScriptedRuntime(Runs.WorkerReply), new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), resolver)
                .RunAsync(Request(repo, "docs"), cts.Token));
    }
}
