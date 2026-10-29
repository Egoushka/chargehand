using Chargehand.Contracts;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Research.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;
using Chargehand.Tests;
using Microsoft.Coyote;
using Microsoft.Coyote.SystematicTesting;
using static Chargehand.Tests.DrivenFakes;

namespace Chargehand.Modules.Batches.CoyoteTests;

/// <summary>Coyote over <c>TaskRunner</c>: a cancel that lands at any point of a task's run, including right after its branch was pushed (the window PR 308 closed).</summary>
public class TaskRunnerCancelTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";

    [Fact]
    public void A_cancel_at_any_point_keeps_the_pull_request_of_a_task_that_pushed()
    {
        var config = Configuration.Create().WithTestingIterations(1000).WithMaxSchedulingSteps(5000).WithRandomGeneratorSeed(1).WithTelemetryEnabled(false);
        var engine = TestingEngine.Create(config, CancelDuringRun);
        engine.Run();
        output.WriteLine(engine.TestReport.GetText(config, "Coyote"));
        Assert.True(engine.TestReport.NumOfFoundBugs == 0, engine.GetReport());
    }

    /// <summary>The run log honours its token as the file-backed one does; the file-backed one keeps a static lock, which Coyote refuses to share between iterations.</summary>
    private sealed class MemoryRunLog : IRunLog
    {
        public Task AppendAsync(StartRecord record, CancellationToken ct) => ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;

        public Task AppendAsync(CallRecord record, CancellationToken ct) => ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;

        public Task AppendAsync(RunRecord record, CancellationToken ct) => ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;

        public Task<RunEntry> ReadAsync(string runId, CancellationToken ct) => throw new NotSupportedException();

        public async Task<IReadOnlyList<RunSummary>> ListAsync(RunListQuery query, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            return [];
        }
    }

    private sealed class NoEvidenceProblems : IEvidenceResolver
    {
        public Task<IReadOnlyList<EvidenceFailure>> ResolveAsync(ResultContract contract, EvidenceScope scope, CancellationToken ct) => Task.FromResult<IReadOnlyList<EvidenceFailure>>([]);
    }

    internal static async Task CancelDuringRun()
    {
        using var dir = new TempDir();
        var engine = new FakeEngine();
        var handover = new FakeHandover();
        var volumes = new FakeVolumes(_ => SessionStatus.Completed);
        var request = new RunRequest("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([new DrivenTask("t1", Goal: "Add a retry")], MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));
        var settings = new TaskRunnerSettings("run-batch-t", request, "ghcr.io/example/session@sha256:1111111111111111111111111111111111111111111111111111111111111111", "/srv/checkouts/repo-abc1234",
            Commit, "https://github.com/o/r.git", "main", new BatchNetworkInfo("chargehand-net-run-batch-t", "egress-1", "egress-1", 3128), null, new DrivenPreset(), new TaskLimits(10_000_000, null), false,
            new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = "value-of-claude-code-oauth-token" }, new PushCredential("value-of-secret-push"), Path.Combine(dir.Path, ".driven"),
            "provider/claude-sonnet-5-5", "2.1.283");
        var runner = new TaskRunner(engine, engine, volumes, handover, new MemoryRunLog(), g => $"tok-{g.RunId}", settings, new DrivenTaskResults(new NoEvidenceProblems(), null, null), TimeSpan.FromMilliseconds(1));

        using var cts = new CancellationTokenSource();
        var canceller = Task.Run(async () =>
        {
            await Task.Yield();
            await cts.CancelAsync();
        }, CancellationToken.None);
        TaskOutcome? outcome = null;
        try
        {
            outcome = await runner.RunAsync(new ResolvedTask("t1", null, "Add a retry"), new Progress<TaskUsage>(), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // agent-checks: allow empty-catch -- the cancel is the scenario; the invariant below is on what the handover pushed
        }
        await canceller;

        // Once the handover pushed the branch and opened the pull request, the task's result keeps it, whenever the cancel came.
        if (handover.Inputs.Count > 0)
            Assert.NotNull(outcome?.PrUrl);
    }
}
