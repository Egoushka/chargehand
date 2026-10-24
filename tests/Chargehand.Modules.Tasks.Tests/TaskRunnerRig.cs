using Chargehand.Contracts;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Research.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;
using Chargehand.Tests;
using static Chargehand.Tests.DrivenFakes;

namespace Chargehand.Modules.Tasks.Tests;

/// <summary>One <see cref="TaskRunner"/> on the shared fakes (C4 brief, S7): a task runs as it does in a batch, with no batch around it, no profile and no old project. The task's result is
/// built as the server builds it (<see cref="DrivenTaskResults"/>), with no evidence problems; nothing is journaled, as for a server that keeps no state.</summary>
internal sealed class TaskRunnerRig : IDisposable
{
    public const string BatchId = "run-batch-t";
    public const string Commit = "abc1234def5678abc1234def5678abc1234def56";

    public TempDir Dir { get; } = new();
    public FakeEngine Engine { get; } = new();
    public FakeHandover Handover { get; } = new();
    public FakeVolumes Volumes { get; }
    public JsonlRunLog Log { get; }

    private readonly TaskLimits _limits;

    private sealed class NoEvidenceProblems : IEvidenceResolver
    {
        public Task<IReadOnlyList<EvidenceFailure>> ResolveAsync(ResultContract contract, EvidenceScope scope, CancellationToken ct) => Task.FromResult<IReadOnlyList<EvidenceFailure>>([]);
    }

    private readonly bool _bare;

    public TaskRunnerRig(FakeVolumes? volumes = null, TaskLimits? limits = null, bool bare = false)
    {
        _bare = bare;
        Volumes = volumes ?? new FakeVolumes(_ => SessionStatus.Completed);
        Log = new JsonlRunLog(Path.Combine(Dir.Path, "log.jsonl"));
        _limits = limits ?? new TaskLimits(10_000_000, null);
    }

    private TaskRunnerSettings Settings(string goal)
    {
        var request = new RunRequest("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([new DrivenTask("t1", Goal: goal)], MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m, Bench: _bare ? true : null, Arm: _bare ? RequestDriven.BareArm : null));
        return new TaskRunnerSettings(BatchId, request, "ghcr.io/example/session@sha256:1111111111111111111111111111111111111111111111111111111111111111", "/srv/checkouts/repo-abc1234",
            Commit, "https://github.com/o/r.git", "main", new BatchNetworkInfo($"chargehand-net-{BatchId}", "egress-1", "egress-1", 3128), null, new DrivenPreset(), _limits, false,
            new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = "value-of-claude-code-oauth-token" }, new PushCredential("value-of-secret-push"), Path.Combine(Dir.Path, ".driven"),
            "provider/claude-sonnet-5-5", "2.1.283");
    }

    /// <summary>Runs task <c>t1</c> with <paramref name="goal"/> to its outcome.</summary>
    public Task<TaskOutcome> RunAsync(string goal, CancellationToken ct = default) =>
        new TaskRunner(Engine, Engine, Volumes, Handover, Log, g => $"tok-{g.RunId}", Settings(goal), new DrivenTaskResults(new NoEvidenceProblems(), null, null), TimeSpan.FromMilliseconds(1))
            .RunAsync(new ResolvedTask("t1", null, goal), new Progress<TaskUsage>(), ct);

    public void Dispose() => Dir.Dispose();
}
