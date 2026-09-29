using System.Text.Json;
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
    ForkPoint? Fork = null,
    Func<int, CancellationToken, Task<string?>>? AfterAnswer = null,
    int MaxFixRounds = 0);

/// <summary>
/// A session to fork before its first message: the fork keeps the system prefix, cached, and no history (spike,
/// 2.0.16). It also keeps that session's instruction entries, so only a node with the same entries may fork it.
/// </summary>
public sealed record ForkPoint(string SessionId, string BeforeMessageId, string InstructionsSha256);

public sealed record NodeResult(ResultContract Contract, string SessionId, IReadOnlyList<WorkerMessage> Calls, IdleOutcome Outcome, string? ForkedFrom = null);

/// <summary>
/// One worker node: fresh session with fixed instruction entries (or a fork of a sibling's that already has them),
/// one task prompt, idle with a deadline, result contract with at most one repair turn for the schema and one for
/// evidence (ADR 0009, ADR 0010, ADR 0011). A writing node also takes up to <see cref="NodeRequest.MaxFixRounds"/> fix turns.
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

        var (outcome, overBudget) = await Turn(session.Id, r.TaskText, r, ct, primed is null ? null : (primed, instructionsSha256));
        // A node stopped at its token budget answers from what it has read instead of returning nothing. The answer
        // turn and its repairs each re-read the whole context, so they get room for three calls at the last call's
        // context on top of what is spent (the watcher polls, so that already overshoots); the USD cap stays hard.
        if (overBudget)
        {
            var spent = Calls(await runtime.ReadMessagesAsync(session.Id, ct));
            r = r with { MaxInputTokens = spent.Sum(m => Context(m.Tokens!)) + 3 * Context(spent[^1].Tokens!) };
            (outcome, _) = await Turn(session.Id, BudgetAnswer, r, ct);
        }
        // A first turn shorter than the watcher's poll still leaves a cached prefix to fork.
        if (primed is not null && ForkPointOf(session.Id, await runtime.ReadMessagesAsync(session.Id, ct), instructionsSha256) is { } point)
            primed.TrySetResult(point);
        var (contract, errors) = await Assemble(session.Id, r, ct);
        if (contract is null && outcome == IdleOutcome.Succeeded)
        {
            (outcome, _) = await Turn(session.Id, $"Your result block failed validation: {string.Join("; ", errors.Take(5))}. Reply again ending with only the corrected ```json block.", r, ct);
            (contract, errors) = await Assemble(session.Id, r, ct);
        }

        if (contract is not null && outcome == IdleOutcome.Succeeded)
        {
            var failures = await resolver.ResolveAsync(contract, await Scope(session.Id, r, ct), ct);
            if (failures.Count > 0)
            {
                (outcome, _) = await Turn(session.Id, $"These evidence references did not resolve: {string.Join("; ", failures.Select(f => $"{f.EvidenceId}: {f.Reason}"))}. Fix or drop them and reply ending with only the corrected ```json block.", r, ct);
                var (repaired, _) = await Assemble(session.Id, r, ct);
                contract = repaired ?? contract;
                contract = ResultAssembler.MoveUnresolved(contract, await resolver.ResolveAsync(contract, await Scope(session.Id, r, ct), ct));
            }
        }

        // A writing node (ADR 0035): after each answer the caller verifies the change and returns feedback for the next turn, or null
        // to stop. The check after the last fix round still runs, so the caller holds the outcome of the final answer.
        for (var round = 0; contract is not null && outcome == IdleOutcome.Succeeded && r.AfterAnswer is not null; round++)
        {
            var feedback = await r.AfterAnswer(round, ct);
            if (feedback is null || round >= r.MaxFixRounds)
                break;
            (outcome, _) = await Turn(session.Id, feedback, r, ct);
            // A reply with no valid block leaves the previous answer standing; the next check still sees the edited tree.
            var (next, _) = await Assemble(session.Id, r, ct);
            if (next is not null)
                contract = ResultAssembler.MoveUnresolved(next, await resolver.ResolveAsync(next, await Scope(session.Id, r, ct), ct));
        }

        if (overBudget && contract is not null)
            contract = contract with { OpenQuestions = [.. contract.OpenQuestions, BudgetNote] };
        var messages = await runtime.ReadMessagesAsync(session.Id, ct);
        var usage = Usage(messages, r.Spec.Model);
        var error = contract is null || outcome != IdleOutcome.Succeeded
            ? Failure(outcome, messages, r, errors)
            : contract.Status == ResultStatus.Failed
                ? new ChargehandException(ErrorCode.Internal, $"the worker reported failed: {contract.Summary}", WorkerFailedAction(r)).Error
                : null;
        contract = contract is null
            ? Failed(r, error!.Message, usage) with { Error = error }
            : contract with { Usage = usage, Status = outcome == IdleOutcome.Succeeded ? contract.Status : ResultStatus.Failed, Error = error };
        return new NodeResult(contract, session.Id, Calls(messages), outcome, fork?.SessionId);
    }

    internal const string BudgetAnswer = "You reached this node's token budget. Stop using tools and reply now with only the ```json result block, " +
        "built from what you have already read; put what you did not get to in open_questions.";

    internal const string BudgetNote = "The node reached its token budget before it finished exploring; the answer covers what it had read.";

    /// <summary>Submits one prompt and waits for idle. A watcher rejects permission requests and enforces budgets and
    /// deadline; OverBudget is true when it interrupted the turn for the token budget alone.</summary>
    private async Task<(IdleOutcome Outcome, bool OverBudget)> Turn(string sessionId, string text, NodeRequest r, CancellationToken ct, (TaskCompletionSource<ForkPoint?> Source, string InstructionsSha256)? primed = null)
    {
        await runtime.SubmitAsync(sessionId, text, ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(r.Deadline);
        using var stopWatcher = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = Watch(sessionId, r, primed, stopWatcher.Token);
        IdleOutcome outcome;
        var overBudget = false;
        try
        {
            outcome = await runtime.AwaitIdleAsync(sessionId, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await runtime.InterruptAsync(sessionId, ct);
            outcome = IdleOutcome.Interrupted;
        }
        finally
        {
            await stopWatcher.CancelAsync();
            overBudget = await watcher;
        }
        return (outcome, overBudget && outcome == IdleOutcome.Interrupted);
    }

    /// <returns>True when it interrupted the session for the token budget with the USD cap not reached.</returns>
    private async Task<bool> Watch(string sessionId, NodeRequest r, (TaskCompletionSource<ForkPoint?> Source, string InstructionsSha256)? primed, CancellationToken ct)
    {
        string? compactedAfter = null;
        // Set before interrupting: the turn ends when the session stops and cancels this watcher, which may still be
        // inside InterruptAsync (Claude Code waits for the process to exit).
        var overBudget = false;
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
                var overUsd = Usage(messages, r.Spec.Model).Usd > r.CapUsd;
                if (overUsd || calls.Sum(m => Context(m.Tokens!)) > r.MaxInputTokens)
                {
                    overBudget = !overUsd;
                    await runtime.InterruptAsync(sessionId, ct);
                    return overBudget;
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
        return overBudget;
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
        return new EvidenceScope(r.RepositoryPath, r.Commit, messages.Select(m => m.Id).ToHashSet(), r.InputIds, seen, await runtime.DiffAsync(sessionId, ct),
            [.. messages.SelectMany(m => m.ToolResults ?? [])]);
    }

    /// <summary>Usd is null (unknown, not $0) once any call's model had no price entry (ADR 0026). A call that names no
    /// model ran on the node's; with no model on either (the runtime's default), its cost is unknown too.</summary>
    private Usage Usage(IReadOnlyList<WorkerMessage> messages, ModelRef? nodeModel)
    {
        long input = 0, output = 0, read = 0, write = 0;
        decimal usd = 0;
        var unknown = false;
        var fallback = nodeModel is null ? null : $"{nodeModel.ProviderId}/{nodeModel.ModelId}";
        foreach (var m in messages.Where(m => m.Tokens is not null && m.Kind is WorkerMessageKind.Assistant or WorkerMessageKind.Compaction))
        {
            var t = m.Tokens!;
            input += t.Input;
            output += t.Output + t.Reasoning;
            read += t.CacheRead;
            write += t.CacheWrite;
            if ((m.Model ?? fallback) is { } model && prices.PriceUsd(model, t) is { } price)
                usd += price;
            else
                unknown = true;
        }
        return new Usage(input, output, read, write, unknown ? null : decimal.Round(usd, 6));
    }

    /// <summary>Forkable once a call has completed: the provider has cached the prefix by then.</summary>
    private static ForkPoint? ForkPointOf(string sessionId, IReadOnlyList<WorkerMessage> messages, string instructionsSha256) =>
        messages.Any(m => m.Kind == WorkerMessageKind.Assistant && m.Tokens is not null && m.Completed is not null)
        && messages.LastOrDefault(m => m.Kind == WorkerMessageKind.User) is { } first
            ? new ForkPoint(sessionId, first.Id, instructionsSha256)
            : null;

    private static long Context(TokenCounts t) => t.Input + t.CacheRead + t.CacheWrite;

    private static List<WorkerMessage> Calls(IReadOnlyList<WorkerMessage> messages) =>
        messages.Where(m => m.Kind == WorkerMessageKind.Assistant && m.Tokens is not null).OrderBy(m => m.Created).ToList();

    /// <summary>What the caller can branch on: the code, the provider's reason when a message carries one, and what to do.</summary>
    private ResultError Failure(IdleOutcome outcome, IReadOnlyList<WorkerMessage> messages, NodeRequest r, IReadOnlyList<string> errors)
    {
        var code = ErrorOf(outcome, messages, r);
        if (outcome == IdleOutcome.Failed && Cause(messages).Length == 0 && !messages.Any(m => m.Kind == WorkerMessageKind.Assistant))
            return new ChargehandException(code, $"worker ended failed: {NoModelCall}", NoModelCallAction(r)).Error;
        var message = outcome == IdleOutcome.Succeeded
            ? $"no valid result contract: {string.Join("; ", errors.Take(3))}"
            : $"worker ended {outcome.ToString().ToLowerInvariant()}{Cause(messages)}";
        return new ChargehandException(code, message, ActionOf(code, r)).Error;
    }

    private const int MaxCause = 300;

    /// <summary>": " and the reason from the newest message that carries a provider error, else empty. OpenCode relays the error
    /// object as JSON, Claude Code its result text (else the result event's subtype and errors) or exit status. Scrubbed before
    /// it is cut, so a cut cannot leave half a key.</summary>
    private static string Cause(IReadOnlyList<WorkerMessage> messages)
    {
        if (messages.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Error))?.Error is not { } raw)
            return "";
        var text = string.Join(' ', ChargehandException.Scrub(ReasonOf(raw)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? "" : $": {(text.Length > MaxCause ? text[..MaxCause] + "…" : text)}";
    }

    /// <summary>The error object's message when the text is JSON that has one (<c>message</c>, <c>data.message</c> or <c>error.message</c>), else the text.</summary>
    private static string ReasonOf(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var path in new[] { new[] { "data", "message" }, new[] { "message" }, new[] { "error", "message" } })
                {
                    var node = doc.RootElement;
                    foreach (var name in path)
                        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(name, out node))
                            break;
                    if (node.ValueKind == JsonValueKind.String && node.GetString() is { Length: > 0 } reason)
                        return reason;
                }
        }
        catch (JsonException)
        {
        }
        return raw;
    }

    /// <summary>A worker that writes status failed itself (ADR 0022 still wants an error): most often a change request
    /// on a read-only preset, which runs as an answer when intake's action is not in the preset.</summary>
    private static string WorkerFailedAction(NodeRequest r) => r.AfterAnswer is not null
        ? $"Read the summary: the writer could not make the change as asked. `chargehand show {r.RunId}` prints the session."
        : $"Read the summary: the worker could not do the task as asked. Ask for a change as a read-only question, or use a preset that allows it. `chargehand show {r.RunId}` prints the session.";

    private const string NoModelCall = "the worker runtime ended the session before any model call and gave no reason";

    /// <summary>A failed session with no assistant message and no error text: OpenCode ends it before it calls a model (a model
    /// the server does not declare does this) and only the server's own log names the reason. The Claude Code adapter always
    /// gives a failed turn a reason, so it does not reach this. The model is config, not a secret.</summary>
    private static string NoModelCallAction(NodeRequest r) =>
        "Read the worker runtime's log for the cause: for OpenCode, the server's log (a model the server does not declare ends a session this way). "
        + (r.Spec.Model is { } model
            ? $"This run asked for `{model.ProviderId}/{model.ModelId}`: check it against the runtime's declared models and the profile's `models` map. "
            : "No model was mapped for this run, so the runtime used its own default: map the preset's model in the profile's `models`, or give the runtime a default. ")
        + $"`chargehand show {r.RunId}` prints the session.";

    private static string ActionOf(ErrorCode code, NodeRequest r) => code switch
    {
        ErrorCode.RateLimited => "Retry once the provider's rate limit clears, or map the preset's model to one with a higher limit.",
        ErrorCode.CostCapReached => "Raise context.budget_usd, or ask a narrower question so the worker reads less.",
        ErrorCode.DeadlineExceeded => $"Retry, or ask a narrower question: a node stops after {r.Deadline.TotalMinutes:0} minutes.",
        ErrorCode.InvalidResult => $"Retry; if it repeats, read the worker's last message with `chargehand show {r.RunId}`.",
        _ => $"Fix the cause named in the message (a model, credential or provider setting), then retry. `chargehand show {r.RunId}` prints the session.",
    };

    /// <summary>
    /// Why a node failed, from what the watcher and the deadline leave behind: an interrupt over the USD cap or the token
    /// budget is the cap, any other interrupt the deadline; a turn that ended without a valid contract is invalid_result.
    /// </summary>
    private ErrorCode ErrorOf(IdleOutcome outcome, IReadOnlyList<WorkerMessage> messages, NodeRequest r) => outcome switch
    {
        IdleOutcome.Succeeded => ErrorCode.InvalidResult,
        // A deadline reached while the provider kept rate limiting the node is the rate limit's doing.
        _ when ChargehandException.RateLimited(messages.FirstOrDefault(m => m.Error is not null)?.Error) => ErrorCode.RateLimited,
        IdleOutcome.Interrupted when Usage(messages, r.Spec.Model).Usd > r.CapUsd || Calls(messages).Sum(m => Context(m.Tokens!)) > r.MaxInputTokens => ErrorCode.CostCapReached,
        IdleOutcome.Interrupted => ErrorCode.DeadlineExceeded,
        _ => ErrorCode.Internal,
    };

    public static ResultContract Failed(NodeRequest r, string reason, Usage usage) => new(
        "result/v1", r.RunId, r.NodeId, r.TraceId, r.PromptChain, ResultStatus.Failed, reason, [], [], [], [reason], 0, usage);
}
