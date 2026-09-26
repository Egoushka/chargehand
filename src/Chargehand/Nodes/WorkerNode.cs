using Chargehand.Budget;
using Chargehand.Contracts;
using Chargehand.Prompts;
using Chargehand.Results;
using Chargehand.Runtime;
using Chargehand.Verification;

namespace Chargehand.Nodes;

public sealed record NodeRequest(
    string RunId,
    string NodeId,
    string TraceId,
    NodeSpec Spec,
    IReadOnlyList<(string Key, string Value)> Instructions,
    string TaskText,
    PromptChain PromptChain,
    string RepositoryPath,
    string Commit,
    IReadOnlyCollection<string> InputIds,
    string InputText,
    decimal CapUsd,
    TimeSpan Deadline,
    long MaxInputTokens = long.MaxValue,
    long? CompactAtTokens = null,
    ForkPoint? Fork = null);

/// <summary>
/// A session to fork before its first message: the fork keeps the system prefix, cached, and no history (spike,
/// 2.0.16). It also keeps that session's instruction entries, so only a node with the same entries may fork it.
/// </summary>
public sealed record ForkPoint(string SessionId, string BeforeMessageId, string InstructionsSha256);

public sealed record NodeResult(ResultContract Contract, string SessionId, IReadOnlyList<WorkerMessage> Calls, IdleOutcome Outcome, string? ForkedFrom = null);

/// <summary>
/// One worker node: fresh session with fixed instruction entries (or a fork of a sibling's that already has them),
/// one task prompt, idle with a deadline, result contract with at most one repair turn for the schema and one for
/// evidence (ADR 0009, ADR 0010, ADR 0011).
/// </summary>
public sealed class WorkerNode(IWorkerRuntime runtime, IPriceTable prices, IEvidenceResolver resolver, TimeSpan? pollInterval = null)
{
    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromSeconds(5);

    /// <param name="primed">Completed with this node's fork point once its first call has finished (the provider has
    /// cached the prefix by then), or with null if the node ends without one.</param>
    public async Task<NodeResult> RunAsync(NodeRequest r, CancellationToken ct, TaskCompletionSource<ForkPoint?>? primed = null)
    {
        try
        {
            return await Run(r, primed, ct);
        }
        finally
        {
            primed?.TrySetResult(null);
        }
    }

    private async Task<NodeResult> Run(NodeRequest r, TaskCompletionSource<ForkPoint?>? primed, CancellationToken ct)
    {
        WorkerSession session;
        var instructionsSha256 = PromptChains.InstructionsSha256(r.Instructions);
        var fork = r.Fork?.InstructionsSha256 == instructionsSha256 ? r.Fork : null;
        if (fork is not null)
            session = await runtime.ForkAsync(fork.SessionId, fork.BeforeMessageId, ct);
        else
        {
            session = await runtime.CreateAsync(r.Spec, ct);
            foreach (var (key, value) in r.Instructions)
                await runtime.SetInstructionAsync(session.Id, key, value, ct);
        }

        var outcome = await Turn(session.Id, r.TaskText, r, ct, primed is null ? null : (primed, instructionsSha256));
        // A first turn shorter than the watcher's poll still leaves a cached prefix to fork.
        if (primed is not null && ForkPointOf(session.Id, await runtime.ReadMessagesAsync(session.Id, ct), instructionsSha256) is { } point)
            primed.TrySetResult(point);
        var (contract, errors) = await Assemble(session.Id, r, ct);
        if (contract is null && outcome == IdleOutcome.Succeeded)
        {
            outcome = await Turn(session.Id, $"Your result block failed validation: {string.Join("; ", errors.Take(5))}. Reply again ending with only the corrected ```json block.", r, ct);
            (contract, errors) = await Assemble(session.Id, r, ct);
        }

        if (contract is not null && outcome == IdleOutcome.Succeeded)
        {
            var failures = await resolver.ResolveAsync(contract, await Scope(session.Id, r, ct), ct);
            if (failures.Count > 0)
            {
                outcome = await Turn(session.Id, $"These evidence references did not resolve: {string.Join("; ", failures.Select(f => $"{f.EvidenceId}: {f.Reason}"))}. Fix or drop them and reply ending with only the corrected ```json block.", r, ct);
                var (repaired, _) = await Assemble(session.Id, r, ct);
                contract = repaired ?? contract;
                contract = ResultAssembler.MoveUnresolved(contract, await resolver.ResolveAsync(contract, await Scope(session.Id, r, ct), ct));
            }
        }

        var messages = await runtime.ReadMessagesAsync(session.Id, ct);
        var usage = Usage(messages);
        contract = contract is null
            ? Failed(r, outcome == IdleOutcome.Succeeded ? $"no valid result contract: {string.Join("; ", errors.Take(3))}" : $"worker ended {outcome.ToString().ToLowerInvariant()}", usage)
            : contract with { Usage = usage, Status = outcome == IdleOutcome.Succeeded ? contract.Status : ResultStatus.Failed };
        return new NodeResult(contract, session.Id, Calls(messages), outcome, fork?.SessionId);
    }

    /// <summary>Submits one prompt and waits for idle. A watcher rejects permission requests and enforces budgets and deadline.</summary>
    private async Task<IdleOutcome> Turn(string sessionId, string text, NodeRequest r, CancellationToken ct, (TaskCompletionSource<ForkPoint?> Source, string InstructionsSha256)? primed = null)
    {
        await runtime.SubmitAsync(sessionId, text, ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(r.Deadline);
        using var stopWatcher = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = Watch(sessionId, r, primed, stopWatcher.Token);
        try
        {
            return await runtime.AwaitIdleAsync(sessionId, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await runtime.InterruptAsync(sessionId, ct);
            return IdleOutcome.Interrupted;
        }
        finally
        {
            await stopWatcher.CancelAsync();
            await watcher;
        }
    }

    private async Task Watch(string sessionId, NodeRequest r, (TaskCompletionSource<ForkPoint?> Source, string InstructionsSha256)? primed, CancellationToken ct)
    {
        string? compactedAfter = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_poll, ct);
                foreach (var p in await runtime.PendingPermissionsAsync(sessionId, ct))
                    await runtime.AnswerPermissionAsync(sessionId, p.Id, PermissionDecision.Reject, "Not allowed by this preset.", ct);
                var messages = await runtime.ReadMessagesAsync(sessionId, ct); // newest first
                var calls = messages.Where(m => m.Kind == WorkerMessageKind.Assistant && m.Tokens is not null).ToList();
                if (primed is not null && ForkPointOf(sessionId, messages, primed.Value.InstructionsSha256) is { } point)
                    primed.Value.Source.TrySetResult(point);
                if (Usage(messages).Usd > r.CapUsd || calls.Sum(m => Context(m.Tokens!)) > r.MaxInputTokens)
                {
                    await runtime.InterruptAsync(sessionId, ct);
                    return;
                }
                // Steered compaction runs after the current step; the turn continues and the system prefix stays cached.
                if (r.CompactAtTokens is { } at && calls.FirstOrDefault() is { } latest && Context(latest.Tokens!) > at
                    && messages[0].Kind != WorkerMessageKind.Compaction && compactedAfter != latest.Id)
                {
                    compactedAfter = latest.Id;
                    await runtime.CompactAsync(sessionId, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<(ResultContract? Contract, IReadOnlyList<string> Errors)> Assemble(string sessionId, NodeRequest r, CancellationToken ct)
    {
        var last = (await runtime.ReadMessagesAsync(sessionId, ct)).FirstOrDefault(m => m.Kind == WorkerMessageKind.Assistant && !string.IsNullOrEmpty(m.Text));
        if (last is null)
            return (null, ["no assistant text"]);
        var outcome = ResultAssembler.Assemble(last.Text!, new ResultEnvelope(r.RunId, r.NodeId, r.TraceId, r.PromptChain, new Usage(0, 0, 0, 0, 0)));
        return (outcome.Contract, outcome.Errors);
    }

    private async Task<EvidenceScope> Scope(string sessionId, NodeRequest r, CancellationToken ct)
    {
        var messages = await runtime.ReadMessagesAsync(sessionId, ct);
        var seen = r.InputText + "\n" + string.Join("\n", messages.Select(m => m.ToolOutput).Where(t => t is not null));
        return new EvidenceScope(r.RepositoryPath, r.Commit, messages.Select(m => m.Id).ToHashSet(), r.InputIds, seen, await runtime.DiffAsync(sessionId, ct));
    }

    private Usage Usage(IReadOnlyList<WorkerMessage> messages)
    {
        long input = 0, output = 0, read = 0, write = 0;
        decimal usd = 0;
        foreach (var m in messages.Where(m => m.Tokens is not null && m.Kind is WorkerMessageKind.Assistant or WorkerMessageKind.Compaction))
        {
            var t = m.Tokens!;
            input += t.Input;
            output += t.Output + t.Reasoning;
            read += t.CacheRead;
            write += t.CacheWrite;
            usd += m.Model is null ? 0 : prices.PriceUsd(m.Model, t);
        }
        return new Usage(input, output, read, write, decimal.Round(usd, 6));
    }

    /// <summary>Forkable once a call has completed: the provider has cached the prefix by then.</summary>
    private static ForkPoint? ForkPointOf(string sessionId, IReadOnlyList<WorkerMessage> messages, string instructionsSha256) =>
        messages.Any(m => m.Kind == WorkerMessageKind.Assistant && m.Tokens is not null && m.Completed is not null)
        && messages.LastOrDefault(m => m.Kind == WorkerMessageKind.User) is { } first
            ? new ForkPoint(sessionId, first.Id, instructionsSha256)
            : null;

    private static long Context(TokenCounts t) => t.Input + t.CacheRead + t.CacheWrite;

    private static IReadOnlyList<WorkerMessage> Calls(IReadOnlyList<WorkerMessage> messages) =>
        messages.Where(m => m.Kind == WorkerMessageKind.Assistant && m.Tokens is not null).OrderBy(m => m.Created).ToList();

    public static ResultContract Failed(NodeRequest r, string reason, Usage usage) => new(
        "result/v1", r.RunId, r.NodeId, r.TraceId, r.PromptChain, ResultStatus.Failed, reason, [], [], [], [reason], 0, usage);
}
