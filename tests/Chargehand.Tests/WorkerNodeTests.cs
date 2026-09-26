using Chargehand.Budget;
using Chargehand.Contracts;
using Chargehand.Nodes;
using Chargehand.Runtime;
using Chargehand.Verification;

namespace Chargehand.Tests;

public class WorkerNodeTests
{
    /// <summary>Each submitted prompt yields the next scripted assistant reply.</summary>
    private sealed class FakeRuntime(params string[] replies) : IWorkerRuntime
    {
        private readonly Queue<string> _replies = new(replies);
        private readonly List<WorkerMessage> _messages = [];
        public List<string> Prompts { get; } = [];
        public List<(string Key, string Value)> Instructions { get; } = [];
        public List<(string Id, PermissionDecision Decision)> Answers { get; } = [];
        public Queue<PermissionRequest> Pending { get; } = new();
        public bool HangOnce { get; set; }
        public int Interrupts { get; private set; }

        public Task<WorkerSession> CreateAsync(NodeSpec spec, CancellationToken ct) => Task.FromResult(new WorkerSession("ses_1", spec.Directory));

        public Task SetInstructionAsync(string sessionId, string key, string value, CancellationToken ct)
        {
            if (Prompts.Count > 0)
                throw new InvalidOperationException("instructions after the first prompt");
            Instructions.Add((key, value));
            return Task.CompletedTask;
        }

        public Task SubmitAsync(string sessionId, string text, CancellationToken ct)
        {
            Prompts.Add(text);
            return Task.CompletedTask;
        }

        public async Task<IdleOutcome> AwaitIdleAsync(string sessionId, CancellationToken ct)
        {
            if (HangOnce)
            {
                HangOnce = false;
                await Task.Delay(Timeout.Infinite, ct);
            }
            var now = DateTimeOffset.UtcNow.AddSeconds(_messages.Count);
            _messages.Insert(0, new WorkerMessage($"msg_{_messages.Count}", WorkerMessageKind.Assistant, now, _replies.Dequeue(),
                new TokenCounts(3, 100, 0, 5000, 200), now.AddSeconds(1), "p/m", ToolOutput: "read src/calc.py"));
            return IdleOutcome.Succeeded;
        }

        public Task InterruptAsync(string sessionId, CancellationToken ct)
        {
            Interrupts++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkerMessage>> ReadMessagesAsync(string sessionId, CancellationToken ct) => Task.FromResult<IReadOnlyList<WorkerMessage>>(_messages.ToList());

        public Task<IReadOnlyList<PermissionRequest>> PendingPermissionsAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PermissionRequest>>(Pending.Count > 0 ? [Pending.Dequeue()] : []);

        public Task AnswerPermissionAsync(string sessionId, string requestId, PermissionDecision decision, string? message, CancellationToken ct)
        {
            Answers.Add((requestId, decision));
            return Task.CompletedTask;
        }

        public Task<WorkerSession> ForkAsync(string sessionId, string? beforeMessageId, CancellationToken ct) => throw new NotSupportedException();
        public Task CompactAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FileDiff>> DiffAsync(string sessionId, CancellationToken ct) => Task.FromResult<IReadOnlyList<FileDiff>>([]);
        public Task<string> GenerateAsync(ModelRef model, string prompt, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>Treats src/calc.py:1-6 as the only valid file evidence.</summary>
    private sealed class FakeResolver : IEvidenceResolver
    {
        public Task<IReadOnlyList<EvidenceFailure>> ResolveAsync(ResultContract contract, EvidenceScope scope, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EvidenceFailure>>(contract.Evidence
                .Where(e => e.Kind != EvidenceKind.File || !e.Locator.StartsWith("src/calc.py:", StringComparison.Ordinal) || int.Parse(e.Locator.Split(':')[1].Split('-')[^1]) > 6)
                .Select(e => new EvidenceFailure(e.Id, "line beyond end of file")).ToList());
    }

    private static string Block(string locator) => $$"""
        Answer.
        ```json
        {"status":"completed","summary":"s","claims":[{"text":"c","evidence":["e1"],"confidence":0.9}],
         "evidence":[{"id":"e1","kind":"file","locator":"{{locator}}"}],"artifacts":[],"open_questions":[],"confidence":0.9}
        ```
        """;

    private static NodeRequest Request(TimeSpan? deadline = null) => new(
        "run-1", "worker", new string('0', 32),
        new NodeSpec("/w/repo", "build", new ModelRef("p", "m"), [], new Dictionary<string, string>()),
        [("chargehand-core", "core"), ("chargehand-preset", "preset")],
        "Task text",
        new PromptChain([], new AsSent("2.0.16", "build", "p/m", "2026-09-26")),
        "/w/repo", "abc1234", [], "", 1.00m, deadline ?? TimeSpan.FromMinutes(1));

    private static WorkerNode Node(FakeRuntime rt) =>
        new(rt, new PriceTable(new Dictionary<string, ModelPrice> { ["p/m"] = new(2m, 10m, 0.2m, 2.5m) }), new FakeResolver(), TimeSpan.FromMilliseconds(10));

    [Fact]
    public async Task Happy_path_sets_instructions_before_the_prompt_and_prices_usage()
    {
        var rt = new FakeRuntime(Block("src/calc.py:5-6"));
        var r = await Node(rt).RunAsync(Request(), CancellationToken.None);
        Assert.Equal(ResultStatus.Completed, r.Contract.Status);
        Assert.Equal(["chargehand-core", "chargehand-preset"], rt.Instructions.Select(i => i.Key));
        Assert.Single(rt.Prompts);
        Assert.Equal(5000, r.Contract.Usage.CacheRead);
        // 3 x 2 + 100 x 10 + 5000 x 0.2 + 200 x 2.5 = 2506 per 1M tokens
        Assert.Equal(0.002506m, r.Contract.Usage.Usd);
        Assert.Single(r.Calls);
    }

    [Fact]
    public async Task Invalid_block_gets_one_repair_turn()
    {
        var rt = new FakeRuntime("no block here", Block("src/calc.py:5"));
        var r = await Node(rt).RunAsync(Request(), CancellationToken.None);
        Assert.Equal(2, rt.Prompts.Count);
        Assert.Contains("failed validation", rt.Prompts[1], StringComparison.Ordinal);
        Assert.Equal(ResultStatus.Completed, r.Contract.Status);
    }

    [Fact]
    public async Task Still_invalid_after_repair_fails_the_node()
    {
        var rt = new FakeRuntime("no block", "still no block");
        var r = await Node(rt).RunAsync(Request(), CancellationToken.None);
        Assert.Equal(ResultStatus.Failed, r.Contract.Status);
        Assert.Equal(2, rt.Prompts.Count);
    }

    [Fact]
    public async Task Unresolved_evidence_gets_one_repair_then_moves_to_open_questions()
    {
        var rt = new FakeRuntime(Block("src/calc.py:40"), Block("src/calc.py:41"));
        var r = await Node(rt).RunAsync(Request(), CancellationToken.None);
        Assert.Equal(2, rt.Prompts.Count);
        Assert.Contains("did not resolve", rt.Prompts[1], StringComparison.Ordinal);
        Assert.Empty(r.Contract.Claims);
        Assert.Contains(r.Contract.OpenQuestions, q => q.StartsWith("Unverified: c", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deadline_interrupts_and_fails_the_node()
    {
        var rt = new FakeRuntime(Block("src/calc.py:5")) { HangOnce = true };
        var r = await Node(rt).RunAsync(Request(TimeSpan.FromMilliseconds(100)), CancellationToken.None);
        Assert.Equal(1, rt.Interrupts);
        Assert.Equal(ResultStatus.Failed, r.Contract.Status);
        Assert.Equal(IdleOutcome.Interrupted, r.Outcome);
    }

    [Fact]
    public async Task Pending_permissions_are_rejected_never_approved()
    {
        var rt = new FakeRuntime(Block("src/calc.py:5")) { HangOnce = true };
        rt.Pending.Enqueue(new PermissionRequest("per_1", "edit", ["README.md"]));
        await Node(rt).RunAsync(Request(TimeSpan.FromMilliseconds(200)), CancellationToken.None);
        Assert.Equal([("per_1", PermissionDecision.Reject)], rt.Answers);
    }
}
