using System.Runtime.CompilerServices;
using Chargehand.Modules.Conversations.Api;
using Chargehand.Modules.Conversations.Core;

namespace Chargehand.Modules.Conversations.Adapters;

/// <summary>A conversation the service knows: one it holds open (<see cref="ConversationHandle"/>) or one that was over before this process started.</summary>
internal interface IConversationEntry
{
    ConversationSnapshot Snapshot { get; }

    int Queued { get; }

    /// <summary>The events with an id above <paramref name="after"/>, oldest first, then new ones as they happen until the conversation is over.</summary>
    IAsyncEnumerable<ConversationEvent> Events(long after, CancellationToken ct);
}

/// <summary>A conversation that ended before this process started, or that failed to start: its snapshot, and its events read from the journal when asked for.</summary>
internal sealed class ClosedConversation(ConversationSnapshot snapshot, ConversationJournal journal) : IConversationEntry
{
    public ConversationSnapshot Snapshot => snapshot;

    public int Queued => 0;

    public async IAsyncEnumerable<ConversationEvent> Events(long after, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        foreach (var e in journal.Read(snapshot.Id).Where(e => e.Id > after))
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
        }
    }
}
