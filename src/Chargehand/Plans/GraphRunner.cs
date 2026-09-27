using Chargehand.Contracts;
using Chargehand.Nodes;

namespace Chargehand.Plans;

public sealed record NodeOutcome(PlanNode Node, ResultContract Contract, NodeResult? Result);

/// <summary>Runs one node: its upstream contracts, a fork point (null for the first node or when none is ready),
/// and for the first node the source to complete once its session can be forked.</summary>
public delegate Task<NodeResult> RunNode(PlanNode node, IReadOnlyList<ResultContract> upstream, ForkPoint? fork, TaskCompletionSource<ForkPoint?>? primed, CancellationToken ct);

/// <summary>
/// Runs a task graph in dependency order. The first node primes a session that every later node forks before its
/// first message, so they read the system prefix from cache instead of writing it again (ADR 0010). Contracts pass
/// between nodes by id, never transcripts: the tasks' results are the run blackboard (ADR 0008). A node that does
/// not complete stops its dependents; siblings finish (ADR 0011).
/// </summary>
public sealed class GraphRunner(int maxConcurrent = 2)
{
    /// <param name="plan">In dependency order (<see cref="SplitPlan.From"/>).</param>
    /// <param name="failed">Builds the contract of a node that threw or was skipped.</param>
    public async Task<IReadOnlyList<NodeOutcome>> RunAsync(IReadOnlyList<PlanNode> plan, RunNode run, Func<PlanNode, string, ResultContract> failed, CancellationToken ct)
    {
        // ponytail: one gate for the run; ADR 0011's limit is per model, and a v1 run uses one model.
        using var gate = new SemaphoreSlim(maxConcurrent);
        var primed = new TaskCompletionSource<ForkPoint?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new Dictionary<string, Task<NodeOutcome>>();
        foreach (var node in plan)
            tasks[node.Id] = RunOne(node, node == plan[0]);
        return await Task.WhenAll(plan.Select(n => tasks[n.Id]));

        async Task<NodeOutcome> RunOne(PlanNode node, bool first)
        {
            var upstream = await Task.WhenAll(node.DependsOn.Select(d => tasks[d]));
            if (upstream.FirstOrDefault(u => u.Contract.Status != ResultStatus.Completed) is { } stopped)
                return new(node, failed(node, $"skipped: {stopped.Node.Id} did not complete") with { Error = stopped.Contract.Error }, null);
            var fork = first ? null : await primed.Task.WaitAsync(ct);
            await gate.WaitAsync(ct);
            try
            {
                var result = await run(node, upstream.Select(u => u.Contract).ToList(), fork, first ? primed : null, ct);
                return new(node, result.Contract, result);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return new(node, failed(node, $"node failed: {e.Message}") with { Error = ChargehandException.ErrorOf(e) }, null);
            }
            finally
            {
                // Whatever the first node did, later nodes must not wait on it forever (null: fresh sessions).
                if (first)
                    primed.TrySetResult(null);
                gate.Release();
            }
        }
    }
}
