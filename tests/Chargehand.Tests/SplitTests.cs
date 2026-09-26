using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Nodes;
using Chargehand.Plans;
using Chargehand.Results;
using Chargehand.Runtime;

namespace Chargehand.Tests;

public class SplitTests
{
    private static readonly string Trace = new('0', 32);
    private static readonly PromptChain Chain = new([], new AsSent("2.0.16", "build", "p/m", "2026-09-26"));

    private static TaskSpec Spec(string subtasks) => new("task-spec/v1", "run-1", "g", [], [], Risk.Low, new Estimate(1, 2, 0, 0, "b"), TaskAction.Split,
        JsonDocument.Parse($$"""{"subtasks":{{subtasks}}}""").RootElement);

    [Fact]
    public void Plan_orders_nodes_by_dependency()
    {
        var (nodes, reason) = SplitPlan.From(Spec("""
            [{"id":"c","goal":"C","read_only":true,"depends_on":["a"]},{"id":"a","goal":"A","read_only":true},{"id":"b","goal":"B","read_only":true}]
            """));
        Assert.Null(reason);
        Assert.Equal(["a", "c", "b"], nodes!.Select(n => n.Id));
    }

    [Theory]
    [InlineData("""[{"id":"a","goal":"A","read_only":true},{"id":"b","goal":"B","read_only":false}]""", "not read-only")]
    [InlineData("""[{"id":"a","goal":"A","read_only":true,"depends_on":["b"]},{"id":"b","goal":"B","read_only":true,"depends_on":["a"]}]""", "cycle")]
    [InlineData("""[{"id":"a","goal":"A","read_only":true,"depends_on":["x"]},{"id":"b","goal":"B","read_only":true}]""", "unknown dependency")]
    [InlineData("""[{"id":"a","goal":"A","read_only":true},{"id":"a","goal":"B","read_only":true}]""", "duplicate")]
    [InlineData("""[{"id":"a","goal":"A","read_only":true},{"id":"b","goal":"B","read_only":true},{"id":"c","goal":"C","read_only":true},{"id":"d","goal":"D","read_only":true},{"id":"e","goal":"E","read_only":true}]""", "2 to 4")]
    public void Plan_rejects_unsafe_or_malformed_splits(string subtasks, string reason)
    {
        var (nodes, why) = SplitPlan.From(Spec(subtasks));
        Assert.Null(nodes);
        Assert.Contains(reason, why, StringComparison.Ordinal);
    }

    private static ResultContract Contract(string node, ResultStatus status = ResultStatus.Completed, double confidence = 0.8) =>
        new("result/v1", "run-1", node, Trace, Chain, status, $"answer {node}",
            [new Claim($"claim {node}", ["e1"], 0.9)], [new Evidence("e1", EvidenceKind.File, "src/calc.py:1")], [],
            [$"q {node}"], confidence, new Usage(10, 20, 30, 40, 0.5m));

    private static NodeResult Result(ResultContract c) => new(c, $"ses_{c.NodeId}", [], IdleOutcome.Succeeded);

    private static ResultContract Failed(PlanNode n, string reason) => Contract(n.Id, ResultStatus.Failed) with { Summary = reason };

    [Fact]
    public async Task Later_nodes_fork_the_first_and_dependents_get_upstream_contracts()
    {
        var plan = new[] { new PlanNode("a", "A", []), new PlanNode("b", "B", []), new PlanNode("c", "C", ["a"]) };
        var forks = new Dictionary<string, ForkPoint?>();
        var upstreams = new Dictionary<string, string[]>();
        var point = new ForkPoint("ses_a", "usr_1", new string('a', 64));
        await new GraphRunner().RunAsync(plan, (node, upstream, fork, primed, ct) =>
        {
            lock (forks)
            {
                forks[node.Id] = fork;
                upstreams[node.Id] = [.. upstream.Select(u => u.NodeId)];
            }
            primed?.SetResult(point);
            return Task.FromResult(Result(Contract(node.Id)));
        }, Failed, CancellationToken.None);
        Assert.Null(forks["a"]);
        Assert.Equal(point, forks["b"]);
        Assert.Equal(point, forks["c"]);
        Assert.Equal(["a"], upstreams["c"]);
        Assert.Empty(upstreams["b"]);
    }

    [Fact]
    public async Task A_failed_node_stops_its_dependents_and_siblings_finish()
    {
        var plan = new[] { new PlanNode("a", "A", []), new PlanNode("b", "B", []), new PlanNode("c", "C", ["b"]) };
        var ran = new List<string>();
        var outcomes = await new GraphRunner().RunAsync(plan, (node, upstream, fork, primed, ct) =>
        {
            lock (ran)
                ran.Add(node.Id);
            return node.Id == "b" ? throw new HttpRequestException("boom") : Task.FromResult(Result(Contract(node.Id)));
        }, Failed, CancellationToken.None);
        Assert.Equal(["a", "b"], ran.Order());
        Assert.Equal([ResultStatus.Completed, ResultStatus.Failed, ResultStatus.Failed], outcomes.Select(o => o.Contract.Status));
        Assert.Contains("skipped: b", outcomes[2].Contract.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task At_most_two_nodes_run_at_once()
    {
        var plan = Enumerable.Range(1, 4).Select(i => new PlanNode($"n{i}", "G", [])).ToArray();
        int running = 0, peak = 0;
        await new GraphRunner().RunAsync(plan, async (node, upstream, fork, primed, ct) =>
        {
            primed?.SetResult(null);
            peak = Math.Max(peak, Interlocked.Increment(ref running));
            await Task.Delay(30, ct);
            Interlocked.Decrement(ref running);
            return Result(Contract(node.Id));
        }, Failed, CancellationToken.None);
        Assert.Equal(2, peak);
    }

    [Fact]
    public void Merge_namespaces_evidence_sums_usage_and_validates()
    {
        var parts = new[]
        {
            new NodeOutcome(new PlanNode("a", "Goal A", []), Contract("a", confidence: 0.6), null),
            new NodeOutcome(new PlanNode("b", "Goal B", []), Contract("b", confidence: 1.0), null),
            new NodeOutcome(new PlanNode("c", "Goal C", []), Contract("c", ResultStatus.Failed), null),
        };
        var merged = ResultMerger.Merge("run-1", Trace, Chain, parts);

        Assert.Equal(ResultStatus.Completed, merged.Status);
        Assert.Equal(["a.e1", "b.e1"], merged.Evidence.Select(e => e.Id));
        Assert.Equal(["a.e1"], merged.Claims[0].Evidence);
        Assert.Equal(0.8, merged.Confidence);
        Assert.Equal(new Usage(30, 60, 90, 120, 1.5m), merged.Usage);
        Assert.Contains(merged.OpenQuestions, q => q.StartsWith("[c] not answered: Goal C", StringComparison.Ordinal));
        Assert.Contains("[b] Goal B: answer b", merged.Summary, StringComparison.Ordinal);
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.Result, JsonSerializer.SerializeToElement(merged, ContractJson.Options)));
    }
}
