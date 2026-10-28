using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Chargehand.Contracts;

namespace Chargehand.Modules.Conversations.Api;

/// <summary>A scripted agent that touches nothing, for trying the API and for tests (<c>runtime: fake</c>). It echoes a message, and acts on two prefixes: <c>approve:</c> asks before it
/// "edits" README.md and goes on when the answer comes; <c>hold:</c> keeps the turn open until <see cref="Release"/> or a cancel. Every turn reports usage. <see cref="Emit"/> makes it
/// do anything else.</summary>
/// <param name="usdPerTurn">The dollars each turn reports; null: it reports tokens only, as an agent that does not price its calls.</param>
public sealed class FakeConversationRuntime(decimal? usdPerTurn = null) : IConversationRuntime
{
    public const string Name = "fake";

    public const string DefaultModel = "fake-1";

    private static readonly ToolLocation[] Readme = [new("README.md", 1)];

    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    /// <summary>The credential the agent pays with, for the pause check; null: it needs none.</summary>
    public string? Credential { get; init; }

    /// <summary>Set to make the next <see cref="StartAsync"/> throw it, as an agent that does not start does.</summary>
    public Exception? StartFailure { get; set; }

    /// <summary>Texts the agent was prompted with, per conversation, in order.</summary>
    public IReadOnlyList<string> Prompts(string conversationId) => _sessions.TryGetValue(conversationId, out var s) ? [.. s.Prompts] : [];

    public bool Supports(string runtime) => runtime == Name;

    public string? CredentialKey(string runtime) => Credential;

    public Task<RuntimeStarted> StartAsync(RuntimeStart start, CancellationToken ct)
    {
        if (StartFailure is { } failure)
            return Task.FromException<RuntimeStarted>(failure);
        _sessions[start.ConversationId] = new Session();
        return Task.FromResult(new RuntimeStarted(start.Model ?? DefaultModel));
    }

    public async IAsyncEnumerable<ConversationUpdate> UpdatesAsync(string conversationId, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var update in Find(conversationId).Updates.Reader.ReadAllAsync(ct))
            yield return update;
    }

    public Task PromptAsync(string conversationId, string text, CancellationToken ct)
    {
        var session = Find(conversationId);
        lock (session)
        {
            if (session.Running)
                throw new InvalidOperationException("a turn is running");
            session.Running = true;
            session.Prompts.Add(text);
            session.Turn = new CancellationTokenSource();
            session.Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _ = Task.Run(() => TurnAsync(session, text), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task CancelAsync(string conversationId, CancellationToken ct)
    {
        Find(conversationId).Turn?.Cancel();
        return Task.CompletedTask;
    }

    public Task AnswerPermissionAsync(string conversationId, string callId, PermissionAnswer answer, CancellationToken ct) =>
        Find(conversationId).Approvals.TryRemove(callId, out var waiting) && waiting.TrySetResult(answer)
            ? Task.CompletedTask
            : throw new InvalidOperationException($"no approval is waiting on {callId}");

    public Task EndAsync(string conversationId, CancellationToken ct)
    {
        var session = Find(conversationId);
        session.Turn?.Cancel();
        session.Updates.Writer.TryComplete();
        return Task.CompletedTask;
    }

    /// <summary>Ends a turn that began with <c>hold:</c>.</summary>
    public void Release(string conversationId) => Find(conversationId).Release?.TrySetResult();

    /// <summary>Sends the stream an update of the caller's making: an error, a tool call, the end of the session.</summary>
    public void Emit(string conversationId, ConversationUpdate update) => Find(conversationId).Updates.Writer.TryWrite(update);

    private Session Find(string conversationId) =>
        _sessions.TryGetValue(conversationId, out var session) ? session : throw new InvalidOperationException($"no conversation {conversationId} is open");

    private async Task TurnAsync(Session session, string text)
    {
        var stop = "end_turn";
        try
        {
            var token = session.Turn!.Token;
            if (text.StartsWith("hold:", StringComparison.Ordinal))
            {
                Say(session, "Holding.");
                await session.Release!.Task.WaitAsync(token);
                Say(session, "Released.");
            }
            else if (text.StartsWith("approve:", StringComparison.Ordinal))
            {
                await ApproveAsync(session, token);
            }
            else
            {
                Say(session, "You said: ");
                Say(session, text);
            }
            session.Updates.Writer.TryWrite(new Emitted(new UsageDelta(text.Length, text.Length + 10, usdPerTurn)));
        }
        catch (OperationCanceledException)
        {
            stop = "cancelled";
        }

        lock (session)
            session.Running = false;
        session.Updates.Writer.TryWrite(new TurnEnded(stop));
    }

    private static async Task ApproveAsync(Session session, CancellationToken token)
    {
        var call = $"call-{Interlocked.Increment(ref session.Calls)}";
        var waiting = new TaskCompletionSource<PermissionAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Approvals[call] = waiting;
        session.Updates.Writer.TryWrite(new Emitted(new ToolCall(call, "Edit README.md", "edit", "pending", Readme)));
        session.Updates.Writer.TryWrite(new Emitted(new ApprovalRequested(call, "Edit README.md", "edit", Readme)));
        var answer = await waiting.Task.WaitAsync(token);
        session.Updates.Writer.TryWrite(new Emitted(new ToolCallUpdate(call, answer == PermissionAnswer.AllowOnce ? "completed" : "failed")));
        Say(session, answer == PermissionAnswer.AllowOnce ? "Edited README.md." : "Left README.md alone.");
    }

    private static void Say(Session session, string text) =>
        session.Updates.Writer.TryWrite(new Emitted(new MessageChunk("assistant", text)));

    private sealed class Session
    {
        public Channel<ConversationUpdate> Updates { get; } = Channel.CreateUnbounded<ConversationUpdate>(new UnboundedChannelOptions { SingleReader = true });

        public ConcurrentDictionary<string, TaskCompletionSource<PermissionAnswer>> Approvals { get; } = new();

        public List<string> Prompts { get; } = [];

        public CancellationTokenSource? Turn { get; set; }

        public TaskCompletionSource? Release { get; set; }

        public bool Running { get; set; }

        public int Calls;
    }
}
