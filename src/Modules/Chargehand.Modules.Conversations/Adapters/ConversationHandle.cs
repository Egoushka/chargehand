using System.Runtime.CompilerServices;
using Chargehand.Modules.Conversations.Api;
using Chargehand.Modules.Conversations.Core;

namespace Chargehand.Modules.Conversations.Adapters;

/// <summary>A conversation this process holds open. Everything that happens to it is an event: it is written to the journal, folded into the snapshot and kept in memory for the
/// streams, under one lock, so a stream that reads the events up to N and waits never misses N+1 or sees it twice. The runtime's updates arrive on <see cref="PumpAsync"/>.</summary>
internal sealed class ConversationHandle : IConversationEntry, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly string _id;
    private readonly IConversationRuntime _runtime;
    private readonly ConversationJournal.Writer _writer;
    private readonly TimeProvider _clock;
    private readonly List<ConversationEvent> _events = [];
    private readonly Queue<string> _queue = new();
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource _changed = NewSignal();
    private ConversationSnapshot? _snapshot;
    private Task _pump = Task.CompletedTask;

    private ConversationHandle(string id, IConversationRuntime runtime, ConversationJournal.Writer writer, TimeProvider clock)
    {
        (_id, _runtime, _writer, _clock) = (id, runtime, writer, clock);
    }

    public ConversationSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return _snapshot!;
        }
    }

    public int Queued
    {
        get
        {
            lock (_gate)
                return _queue.Count;
        }
    }

    /// <summary>The handle of a new conversation, whose first event records its start fields. The runtime has not been started.</summary>
    public static ConversationHandle Open(string id, IConversationRuntime runtime, ConversationJournal journal, TimeProvider clock, StateData start)
    {
        var handle = new ConversationHandle(id, runtime, journal.Open(id), clock);
        lock (handle._gate)
            handle.Append(start);
        return handle;
    }

    /// <summary>The runtime is up: the conversation is idle, with the model the runtime settled on, and the runtime's updates are read from now on.</summary>
    public void Started(RuntimeStarted started)
    {
        lock (_gate)
            Append(new StateData(ConversationState.Idle, Model: started.Model));
        _pump = Task.Run(PumpAsync, CancellationToken.None);
    }

    public void StartFailed(string message)
    {
        lock (_gate)
            Fail(message);
    }

    /// <summary>Sends the person's message, now when no turn runs and otherwise after the running one.</summary>
    public async Task<SendResult> SendAsync(string text)
    {
        lock (_gate)
        {
            if (_snapshot!.IsOver)
                return SendResult.Over;
            if (_snapshot.State != ConversationState.Idle)
            {
                _queue.Enqueue(text);
                return SendResult.Queued;
            }

            Begin(text);
        }

        await PromptAsync(text);
        return SendResult.Sent;
    }

    public async Task<AnswerResult> AnswerAsync(string callId, string decision)
    {
        lock (_gate)
        {
            if (_snapshot!.IsOver)
                return new(AnswerOutcome.Over);
            if (_snapshot.Answered.Contains(callId))
                return new(AnswerOutcome.AlreadyAnswered);
            if (!_snapshot.Pending.Contains(callId))
                return new(AnswerOutcome.Unknown);
            Append(new ApprovalAnswered(callId, decision));
        }

        try
        {
            await _runtime.AnswerPermissionAsync(_id, callId, decision == ApprovalAnswered.AllowOnce ? PermissionAnswer.AllowOnce : PermissionAnswer.Reject, _stop.Token);
            return new(AnswerOutcome.Answered);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var message = Scrubbed(e);
            lock (_gate)
                Report(message);
            return new(AnswerOutcome.RuntimeFailed, message);
        }
    }

    /// <summary>Ends the conversation at the person's word and cancels the turn. The end is written first, so the runtime closing its stream after it is not taken for the reason; an
    /// ended conversation stays as it is.</summary>
    public async Task EndAsync() => await CloseAsync(EndedData.User);

    /// <summary>The server is stopping: the conversation is lost, as it would be after a crash, and its agent is closed.</summary>
    public async ValueTask DisposeAsync()
    {
        await CloseAsync(EndedData.Lost);
        await _pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
        _writer.Dispose();
    }

    private async Task CloseAsync(string reason)
    {
        lock (_gate)
        {
            if (_snapshot!.IsOver)
                return;
            _queue.Clear();
            Finish(reason);
        }

        try
        {
            await _runtime.CancelAsync(_id, CancellationToken.None);
            await _runtime.EndAsync(_id, CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The conversation is over whatever the agent does now; closing it is the runtime's own business.
        }

        await _stop.CancelAsync();
    }

    public async IAsyncEnumerable<ConversationEvent> Events(long after, [EnumeratorCancellation] CancellationToken ct)
    {
        var delivered = after;
        while (true)
        {
            ConversationEvent[] batch;
            Task signal;
            bool over;
            lock (_gate)
            {
                var from = (int)Math.Clamp(delivered, 0, _events.Count);
                batch = [.. _events.Skip(from)];
                signal = _changed.Task;
                over = _snapshot!.IsOver;
            }

            foreach (var e in batch)
            {
                yield return e;
                delivered = e.Id;
            }

            if (over)
                yield break;
            await signal.WaitAsync(ct);
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var update in _runtime.UpdatesAsync(_id, _stop.Token))
                await ApplyAsync(update);
            lock (_gate)
                Finish(EndedData.RuntimeExited);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            lock (_gate)
                Fail(Scrubbed(e));
        }
    }

    private async Task ApplyAsync(ConversationUpdate update)
    {
        string? next;
        lock (_gate)
        {
            if (_snapshot!.IsOver)
                return;
            next = update switch
            {
                Emitted emitted => Relay(emitted.Data),
                TurnEnded ended => EndTurn(ended.StopReason),
                RuntimeEnded => Finish(EndedData.RuntimeExited),
                _ => null,
            };
        }

        if (next is not null)
            await PromptAsync(next);
    }

    /// <summary>Hands the runtime a message that <see cref="Begin"/> has recorded. A runtime that refuses it fails the turn, not the conversation, and the messages behind it are dropped.</summary>
    private async Task PromptAsync(string text)
    {
        try
        {
            await _runtime.PromptAsync(_id, text, _stop.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var message = Scrubbed(e);
            lock (_gate)
            {
                if (_snapshot!.IsOver)
                    return;
                Report(message);
                _queue.Clear();
                Append(new StateData(ConversationState.Idle, StopReason: "error"));
            }
        }
    }

    // The members below run with the gate held.

    private string? Relay(ConversationEventData data)
    {
        switch (data)
        {
            case ErrorData { Fatal: true } error:
                Append(error);
                return Finish(EndedData.Error);
            case MessageChunk chunk:
                Append(chunk with { Role = "assistant" });
                return null;
            case ToolCall or ToolCallUpdate or ApprovalRequested or UsageDelta or ErrorData:
                Append(data);
                return null;
            default:
                Report($"the runtime sent a {data.Type} event, which only the server writes");
                return null;
        }
    }

    private string? EndTurn(string stopReason)
    {
        Append(new StateData(ConversationState.Idle, StopReason: stopReason));
        if (!_queue.TryDequeue(out var next))
            return null;
        Begin(next);
        return next;
    }

    private void Begin(string text)
    {
        Append(new MessageChunk("user", text));
        Append(new StateData(ConversationState.Running));
    }

    private void Report(string message)
    {
        if (!_snapshot!.IsOver)
            Append(new ErrorData(message, false));
    }

    private void Fail(string message)
    {
        if (_snapshot!.IsOver)
            return;
        Append(new ErrorData(message, true));
        Finish(EndedData.Error);
    }

    private string? Finish(string reason)
    {
        if (!_snapshot!.IsOver)
            Append(new EndedData(reason));
        return null;
    }

    private void Append(ConversationEventData data)
    {
        var e = ConversationJson.ToEvent((_snapshot?.LastEventId ?? 0) + 1, _clock.GetUtcNow(), data);
        _writer.Append(e);
        _events.Add(e);
        _snapshot = ConversationReducer.Apply(_snapshot, _id, e);
        if (_snapshot.IsOver)
            _writer.Dispose();
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string Scrubbed(Exception e) => Chargehand.Kernel.ChargehandException.Scrub(e.Message);
}

internal enum SendResult { Sent, Queued, Over }

internal enum AnswerOutcome { Answered, Unknown, AlreadyAnswered, Over, RuntimeFailed }

internal sealed record AnswerResult(AnswerOutcome Outcome, string? Message = null);
