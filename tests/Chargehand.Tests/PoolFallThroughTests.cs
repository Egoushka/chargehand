using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Modules.Economics.Api;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Tests;

/// <summary>A provider's 402 takes the run's pool out of the router and the same run goes on with the next eligible pool (ADR 0095).</summary>
public class PoolFallThroughTests
{
    private static PoolSettings Pool(string name) => new(name, PoolKinds.Monthly, ["provider/small-model"], TotalUsd: 10, NoTrainingModels: ["*"]);

    private static (Orchestrator Orchestrator, ScriptedRuntime Runtime, JsonlRunLog Log, RunRequest Request) Build(TempDir root, params PoolSettings[] order)
    {
        var repo = Runs.GitRepo(root.Path);
        var profile = Runs.Profile(root.Path) with
        {
            Pools = new PoolsSettings(Enabled: true, DefaultTier: "cheap", Tiers: [new("cheap", ["provider/small-model"])], Order: order),
        };
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));
        return (new Orchestrator(profile, runtime, "2.0.16", Repo.Root, log, new Dictionary<string, int>()), runtime, log, Runs.CheapRequest(repo));
    }

    [Fact]
    public async Task A_402_from_the_first_pool_is_answered_by_the_second_in_the_same_run()
    {
        using var root = new TempDir();
        var (orchestrator, runtime, log, request) = Build(root, Pool("first"), Pool("second"));
        runtime.CreateFailures.Enqueue(new InvalidOperationException("402 Payment Required"));

        var result = await orchestrator.RunAsync(request, CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Single(runtime.Created);
        var runs = (await log.ReadAllAsync(CancellationToken.None)).Runs;
        Assert.Equal("second", Assert.Single(runs).Route?.Pool);
    }

    [Fact]
    public async Task A_402_on_the_only_pool_ends_the_run_failed_and_is_not_retried_forever()
    {
        using var root = new TempDir();
        var (orchestrator, runtime, _, request) = Build(root, Pool("only"));
        runtime.CreateFailures.Enqueue(new InvalidOperationException("402 Payment Required"));
        runtime.CreateFailures.Enqueue(new InvalidOperationException("402 Payment Required"));

        var result = await orchestrator.RunAsync(request, CancellationToken.None);

        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Empty(runtime.Created);
    }
}
