using Chargehand.Budget;
using Chargehand.Contracts;
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
    TimeSpan Deadline);

public sealed record NodeResult(ResultContract Contract, string SessionId, IReadOnlyList<WorkerMessage> Calls, IdleOutcome Outcome);

/// <summary>
/// One worker node: fresh session, fixed instruction entries, one task prompt, idle with a deadline, result
/// contract with at most one repair turn for the schema and one for evidence (ADR 0009, ADR 0011).
/// </summary>
public sealed class WorkerNode(IWorkerRuntime runtime, IPriceTable prices, IEvidenceResolver resolver, TimeSpan? pollInterval = null)
{
    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromSeconds(5);

    public async Task<NodeResult> RunAsync(NodeRequest r, CancellationToken ct)
    {
        var session = await runtime.CreateAsync(r.Spec, ct);
        foreach (var (key, value) in r.Instructions)
            await runtime.SetInstructionAsync(session.Id, key, value, ct);

        var outcome = await Turn(session.Id, r.TaskText, r, ct);
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
        return new NodeResult(contract, session.Id, Calls(messages), outcome);
    }

    /// <summary>Submits one prompt and waits for idle. A watcher rejects permission requests and enforces cap and deadline.</summary>
    private async Task<IdleOutcome> Turn(string sessionId, string text, NodeRequest r, CancellationToken ct)
    {
        await runtime.SubmitAsync(sessionId, text, ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(r.Deadline);
        using var stopWatcher = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = Watch(sessionId, r.CapUsd, stopWatcher.Token);
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

    private async Task Watch(string sessionId, decimal capUsd, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_poll, ct);
                foreach (var p in await runtime.PendingPermissionsAsync(sessionId, ct))
                    await runtime.AnswerPermissionAsync(sessionId, p.Id, PermissionDecision.Reject, "Not allowed by this preset.", ct);
                if (Usage(await runtime.ReadMessagesAsync(sessionId, ct)).Usd > capUsd)
                {
                    await runtime.InterruptAsync(sessionId, ct);
                    return;
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

    private static IReadOnlyList<WorkerMessage> Calls(IReadOnlyList<WorkerMessage> messages) =>
        messages.Where(m => m.Kind == WorkerMessageKind.Assistant && m.Tokens is not null).OrderBy(m => m.Created).ToList();

    public static ResultContract Failed(NodeRequest r, string reason, Usage usage) => new(
        "result/v1", r.RunId, r.NodeId, r.TraceId, r.PromptChain, ResultStatus.Failed, reason, [], [], [], [reason], 0, usage);
}
