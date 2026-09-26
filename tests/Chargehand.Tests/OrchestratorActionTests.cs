using Chargehand.Budget;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.RunLog;
using Chargehand.Runtime;

namespace Chargehand.Tests;

/// <summary>Actions that stop before any worker runs: deny, ask, improve, and the preset's approval gate.</summary>
public class OrchestratorActionTests
{
    /// <summary>Intake only: generate returns the scripted spec; any worker call fails the test.</summary>
    private sealed class IntakeOnly(string detail, string action, string risk = "low", decimal usdHigh = 0.1m) : IWorkerRuntime
    {
        public Task<string> GenerateAsync(ModelRef model, string prompt, CancellationToken ct) => Task.FromResult($$"""
            {"contract_version":"task-spec/v1","id":"x","goal":"g","constraints":[],"acceptance_criteria":[],"risk":"{{risk}}",
             "estimate":{"tokens_low":1,"tokens_high":2,"usd_low":0.01,"usd_high":{{usdHigh.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"basis":"b"},"action":"{{action}}","action_detail":{{detail}}}
            """);

        public Task<WorkerSession> CreateAsync(NodeSpec spec, CancellationToken ct) => throw new InvalidOperationException("no worker expected");
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
        public List<RunRecord> Runs { get; } = [];
        public Task AppendAsync(StartRecord record, CancellationToken ct) => Task.CompletedTask;
        public Task AppendAsync(CallRecord record, CancellationToken ct) => Task.CompletedTask;
        public Task AppendAsync(RunRecord record, CancellationToken ct)
        {
            Runs.Add(record);
            return Task.CompletedTask;
        }
        public Task<RunEntry> ReadAsync(string runId, CancellationToken ct) => throw new NotSupportedException();
    }

    private static async Task<(ResultContract Result, RunRecord Run)> Run(IWorkerRuntime runtime, string preset, bool? approved = null)
    {
        var profile = new Profile("profile/v1", new OpenCodeSettings("http://127.0.0.1:1", "pw", "2.0.16"), "/elsewhere", preset, "p/small",
            new Dictionary<string, ModelPrice>());
        var log = new MemoryLog();
        var result = await new Orchestrator(profile, runtime, "2.0.16", Repo.Root, log, new Dictionary<string, int>())
            .RunAsync(new RunRequest("request/v1", "Explain the repo.", new RequestContext(false, preset, null, new RepositoryRef("/w/repo", "abc1234"), approved)), CancellationToken.None);
        return (result, log.Runs.Single());
    }

    [Fact]
    public async Task Deny_returns_the_reason_and_the_unblock_condition()
    {
        var (r, run) = await Run(new IntakeOnly("""{"reason":"touches prod","unblock_condition":"use staging"}""", "deny"), "cheap");
        Assert.Equal(ResultStatus.Denied, r.Status);
        Assert.Equal("touches prod", r.Summary);
        Assert.Equal(["Unblock: use staging"], r.OpenQuestions);
        Assert.Equal("deny", run.ExecutedAction);
    }

    [Fact]
    public async Task Ask_returns_needs_input_with_the_questions()
    {
        var (r, _) = await Run(new IntakeOnly("""{"questions":["Which service?"]}""", "ask"), "cheap");
        Assert.Equal(ResultStatus.NeedsInput, r.Status);
        Assert.Equal(["Which service?"], r.OpenQuestions);
    }

    [Fact]
    public async Task Improve_returns_the_improved_request_and_its_diff()
    {
        var (r, _) = await Run(new IntakeOnly("""{"improved_request":"Explain src/.","diff":"-Explain the repo.\n+Explain src/."}""", "improve"), "cheap");
        Assert.Equal(ResultStatus.NeedsInput, r.Status);
        Assert.Equal("Explain src/.", r.Summary);
        var diff = Assert.Single(r.Artifacts);
        Assert.Equal("text/x-diff", diff.MediaType);
        Assert.StartsWith("-Explain the repo.", diff.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("medium", 0.1)]
    [InlineData("low", 0.6)]
    public async Task Strict_asks_for_approval_above_its_thresholds(string risk, double usdHigh)
    {
        var (r, run) = await Run(new IntakeOnly("null", "answer", risk, (decimal)usdHigh), "strict");
        Assert.Equal(ResultStatus.NeedsInput, r.Status);
        Assert.Contains("approval", r.Summary, StringComparison.Ordinal);
        Assert.Equal("answer", run.ExecutedAction);
    }

    [Fact]
    public async Task Approved_request_goes_past_the_gate()
    {
        // Past the gate the orchestrator checks the checkout, which this fake repository fails; the run still ends with a result.
        var (r, run) = await Run(new IntakeOnly("null", "answer", "medium"), "strict", approved: true);
        Assert.Equal(ResultStatus.Failed, r.Status);
        Assert.Contains("worker_root", r.Summary, StringComparison.Ordinal);
        Assert.Equal("answer", run.ExecutedAction);
    }

    [Fact]
    public async Task An_action_the_preset_does_not_allow_runs_as_answer()
    {
        var (r, run) = await Run(new IntakeOnly("""{"questions":["Which?"]}""", "ask"), "default");
        Assert.Contains("worker_root", r.Summary, StringComparison.Ordinal);
        Assert.Equal("ask", run.IntakeAction);
        Assert.Equal("answer", run.ExecutedAction);
    }
}
