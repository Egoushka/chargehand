using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Research.Api;
using Chargehand.RunLog;
using Chargehand.Runtime;
using Chargehand.Runtimes.Acp.Api;
using Chargehand.Runtimes.OpenCode.Api;
using static Chargehand.Tests.AcpTestSupport;

namespace Chargehand.Tests;

/// <summary>ADR 0086, decision 10: usage is what the agent reports; an agent that reports none cannot hold a dollar cap, so a run on it is refused unless the profile allows it.</summary>
public class AcpUsageTests
{
    [Fact]
    public async Task What_the_agent_reports_lands_on_the_turns_message_with_a_priced_model_label()
    {
        await using var agent = new AcpFixture();

        var (_, messages, _) = await agent.Turn("usage");

        var call = Assert.Single(messages, m => m.Kind == WorkerMessageKind.Assistant);
        Assert.Equal(new TokenCounts(10, 5, 0, 0, 0), call.Tokens);
        Assert.Equal("fakeco/fast", call.Model);
        Assert.NotNull(call.Completed);
    }

    [Fact]
    public async Task An_agent_that_reports_nothing_records_no_tokens()
    {
        await using var agent = new AcpFixture();

        var (_, messages, _) = await agent.Turn("say hello");

        Assert.Null(Assert.Single(messages, m => m.Kind == WorkerMessageKind.Assistant).Tokens);
    }

    [Fact]
    public void The_pinned_list_decides_whether_the_runtime_reports_usage()
    {
        Assert.True(NewRuntime(Fake(reportsUsage: true)).ReportsUsage);
        Assert.False(NewRuntime(Fake(reportsUsage: false)).ReportsUsage);
    }

    private sealed class Unmetered(bool researchOnly = false) : IWorkerRuntime
    {
        public bool ReportsUsage => false;

        public bool ResearchOnly => researchOnly;

        public int Generated { get; private set; }

        public Task<string> GenerateAsync(ModelRef? model, string prompt, CancellationToken ct)
        {
            Generated++;
            return Task.FromResult("""
                {"contract_version":"task-spec/v1","id":"x","goal":"g","constraints":[],"acceptance_criteria":[],"risk":"low",
                 "estimate":{"tokens_low":1,"tokens_high":2,"usd_low":0.01,"usd_high":0.02,"basis":"b"},"action":"deny","action_detail":{"reason":"r","unblock_condition":"u"}}
                """);
        }

        public Task<WorkerSession> CreateAsync(NodeSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public Task SetInstructionAsync(string sessionId, string key, string value, CancellationToken ct) => throw new NotSupportedException();
        public Task SubmitAsync(string sessionId, string text, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdleOutcome> AwaitIdleAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task InterruptAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkerMessage>> ReadMessagesAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PermissionRequest>> PendingPermissionsAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task AnswerPermissionAsync(string sessionId, string requestId, PermissionDecision decision, string? message, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkerSession> ForkAsync(string sessionId, string? beforeMessageId, CancellationToken ct) => throw new NotSupportedException();
        public Task CompactAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FileDiff>> DiffAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class MemoryLog : IRunLog
    {
        public Task AppendAsync(StartRecord record, CancellationToken ct) => Task.CompletedTask;
        public Task AppendAsync(CallRecord record, CancellationToken ct) => Task.CompletedTask;
        public Task AppendAsync(RunRecord record, CancellationToken ct) => Task.CompletedTask;
        public Task<RunEntry> ReadAsync(string runId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<RunSummary>> ListAsync(RunListQuery query, CancellationToken ct) => throw new NotSupportedException();
    }

    private static Task<ResultContract> Run(IWorkerRuntime runtime, AcpSettings? acp, string preset = "cheap")
    {
        var profile = new Profile("profile/v1", new OpenCodeSettings("http://127.0.0.1:1", "pw", "2.0.16"), "/elsewhere", preset, "p/small", new Dictionary<string, ModelPrice>(), Acp: acp);
        return new Orchestrator(profile, runtime, "1.0.0", Repo.Root, new MemoryLog(), new Dictionary<string, int>())
            .RunAsync(new RunRequest("request/v1", "Explain the repo.", new RequestContext(false, preset, null, new RepositoryRef("/w/repo", "abc1234"), null)), CancellationToken.None);
    }

    [Fact]
    public async Task An_agent_that_reports_no_usage_is_refused_runtime_uncapped_before_anything_is_spent()
    {
        var runtime = new Unmetered();

        var result = await Run(runtime, acp: null);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Equal(ErrorCode.RuntimeUncapped, result.Error!.Code);
        Assert.Contains("acp.allow_uncapped", result.Error.Action, StringComparison.Ordinal);
        Assert.Equal(0, runtime.Generated);
    }

    [Fact]
    public async Task The_profile_can_allow_an_uncapped_agent_and_the_run_goes_on_to_intake()
    {
        var runtime = new Unmetered();

        var result = await Run(runtime, new AcpSettings(AllowUncapped: true));

        Assert.Equal(ResultStatus.Denied, result.Status);
        Assert.Equal(1, runtime.Generated);
    }

    [Fact]
    public async Task A_research_only_runtime_refuses_a_preset_that_writes()
    {
        var runtime = new Unmetered(researchOnly: true);

        var result = await Run(runtime, new AcpSettings(AllowUncapped: true), preset: "code");

        Assert.Equal(ErrorCode.InvalidRequest, result.Error!.Code);
        Assert.Contains("research presets only", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal(0, runtime.Generated);
    }
}
