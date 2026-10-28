using System.Threading.Channels;
using AgentClientProtocol;
using Chargehand.Kernel;
using Chargehand.Modules.Conversations.Api;
using Chargehand.Runtimes.Acp.Api;

namespace Chargehand.Runtimes.Acp.Core;

/// <summary>One conversation's side of an agent: its turn, the approvals it waits on and what it has used. Everything the agent does reaches the conversation as <see cref="Updates"/>.</summary>
internal sealed class AcpConversation(AcpAgentSpec spec, string directory) : IDisposable
{
    private static readonly TimeSpan TurnWait = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = [];
    private readonly AcpUsageMeter _meter = new();
    private CancellationTokenSource? _turn;
    private Task _turnTask = Task.CompletedTask;
    private IAcpAgent? _agent;
    private bool _running;
    private bool _over;
    private decimal? _cost;
    private decimal _costReported;

    public Channel<ConversationUpdate> Updates { get; } = Channel.CreateUnbounded<ConversationUpdate>(new UnboundedChannelOptions { SingleReader = true });

    public void Attach(IAcpAgent agent)
    {
        _agent = agent;
        _ = agent.Closed.ContinueWith(t => Died(Reason(t.IsCompletedSuccessfully ? t.Result : t.Exception!)), TaskScheduler.Default);
    }

    public ValueTask OnUpdate(SessionNotification note)
    {
        if (note.Update is UsageUpdate usage)
        {
            lock (_gate)
            {
                _meter.Observe(usage);
                if (usage.Cost is { } cost && string.Equals(cost.Currency, "USD", StringComparison.OrdinalIgnoreCase))
                    _cost = cost.Amount;
            }
        }
        else if (AcpConversationUpdates.Translate(note.Update, directory) is { } data)
        {
            Updates.Writer.TryWrite(new Emitted(data));
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>A request to read, search or think is allowed once and nobody is asked. Any other waits for the person's answer, which is "once" or "no": no saved permission is ever chosen (ADR 0006).</summary>
    public async ValueTask<RequestPermissionOutcome> OnPermission(RequestPermissionRequest request)
    {
        if (AcpReadOnlyPolicy.Decide(request) is { Refused: false } allowed)
            return allowed.Outcome;
        var waiting = new TaskCompletionSource<RequestPermissionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_over)
                return RequestPermissionOutcome.Cancelled;
            _pending[request.ToolCall.ToolCallId] = new Pending(request, waiting);
        }

        Updates.Writer.TryWrite(new Emitted(AcpConversationUpdates.Approval(request, directory)));
        return await waiting.Task;
    }

    /// <summary>Hands the agent a message. The turn runs in the background and ends with a <see cref="TurnEnded"/> update.</summary>
    public void Prompt(string text)
    {
        lock (_gate)
        {
            if (_over)
                throw new InvalidOperationException($"the {spec.Name} session is over");
            if (_running)
                throw new InvalidOperationException("a turn is running");
            _running = true;
            _meter.Reset();
            _turn?.Dispose();
            _turn = new CancellationTokenSource();
            _turnTask = Task.Run(() => RunTurnAsync(text, _turn.Token), CancellationToken.None);
        }
    }

    /// <summary>Stops the turn: the client sends <c>session/cancel</c>, and an approval the agent waits on is declined, as the protocol asks.</summary>
    public void Cancel()
    {
        lock (_gate)
            _turn?.Cancel();
        CancelPending();
    }

    /// <summary>Answers the approval the agent waits on with the option that does it, once or not at all.</summary>
    /// <exception cref="InvalidOperationException">Nothing waits on <paramref name="callId"/>, or the agent offered no one-time allow (the request is then declined).</exception>
    public void Answer(string callId, PermissionAnswer answer)
    {
        Pending pending;
        lock (_gate)
        {
            if (!_pending.Remove(callId, out pending!))
                throw new InvalidOperationException($"no approval is waiting on {callId}");
        }

        var kind = answer == PermissionAnswer.AllowOnce ? AcpPermissionKind.AllowOnce : AcpPermissionKind.RejectOnce;
        var option = pending.Request.Options.FirstOrDefault(o => o.Kind == kind)?.OptionId;
        pending.Answer.TrySetResult(option is null ? RequestPermissionOutcome.Cancelled : RequestPermissionOutcome.Selected(option));
        if (option is null && answer == PermissionAnswer.AllowOnce)
            throw new InvalidOperationException($"{spec.Name} offers no one-time allow for {callId}; the request was declined");
    }

    public async Task EndAsync()
    {
        Task turn;
        lock (_gate)
        {
            if (_over)
                return;
            _over = true;
            _turn?.Cancel();
            turn = _turnTask;
        }

        CancelPending();
        Updates.Writer.TryComplete();
        if (_agent is { } agent)
            await agent.DisposeAsync();
        await turn.WaitAsync(TurnWait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public void Dispose() => _turn?.Dispose();

    private async Task RunTurnAsync(string text, CancellationToken ct)
    {
        var stop = AcpStopReason.EndTurn;
        string? died = null;
        try
        {
            var result = await _agent!.Session.PromptAsync(text, ct);
            stop = result.StopReason;
            lock (_gate)
                _meter.Finish(result.Usage);
            ReportUsage();
        }
        catch (OperationCanceledException)
        {
            stop = AcpStopReason.Cancelled;
        }
        catch (AcpRequestException e)
        {
            // The agent refused this prompt (a rejected key, bad parameters): the turn fails, and the session is still there.
            stop = "error";
            Updates.Writer.TryWrite(new Emitted(new ErrorData(Reason(e), false)));
        }
        catch (Exception e)
        {
            died = Reason(e);
        }

        CancelPending();
        lock (_gate)
            _running = false;
        if (died is not null)
            Died(died);
        else
            Updates.Writer.TryWrite(new TurnEnded(stop));
    }

    /// <summary>What the turn used, as the agent said: the token counts of its result, else the context gauge as input (a lower bound), and the cost it has added since the last turn.</summary>
    private void ReportUsage()
    {
        TokenCounts? tokens;
        decimal added;
        lock (_gate)
        {
            tokens = _meter.Tokens;
            added = (_cost ?? _costReported) - _costReported;
            _costReported += added;
        }

        if (tokens is null && added <= 0)
            return;
        Updates.Writer.TryWrite(new Emitted(new UsageDelta(
            (tokens?.Input ?? 0) + (tokens?.CacheRead ?? 0) + (tokens?.CacheWrite ?? 0), (tokens?.Output ?? 0) + (tokens?.Reasoning ?? 0), added > 0 ? added : null)));
    }

    /// <summary>The agent is gone, or its connection broke: the conversation cannot go on.</summary>
    private void Died(string reason)
    {
        lock (_gate)
        {
            if (_over)
                return;
            _over = true;
        }

        CancelPending();
        Updates.Writer.TryWrite(new Emitted(new ErrorData(reason, true)));
        Updates.Writer.TryComplete();
    }

    private void CancelPending()
    {
        List<Pending> declined;
        lock (_gate)
        {
            declined = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (var pending in declined)
            pending.Answer.TrySetResult(RequestPermissionOutcome.Cancelled);
    }

    private string Reason(Exception e) =>
        $"{spec.Name} failed: {(string.IsNullOrWhiteSpace(e.Message) ? e.GetType().Name : e.Message)}{(e is AcpConnectionException { StderrTail.Length: > 0 } c ? $" ({c.StderrTail.Trim()})" : "")}";

    private sealed record Pending(RequestPermissionRequest Request, TaskCompletionSource<RequestPermissionOutcome> Answer);
}
