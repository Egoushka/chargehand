using Chargehand.Modules.Conversations.Api;

namespace Chargehand.Modules.Conversations.Core;

/// <summary>A conversation's state is its events folded, and nothing else keeps it (ADR 0060: the decision logic has no I/O). The service applies each event as it writes it;
/// after a restart the journal's events are folded the same way.</summary>
internal static class ConversationReducer
{
    public static ConversationSnapshot Fold(string id, IReadOnlyList<ConversationEvent> events)
    {
        if (events.Count == 0)
            throw new InvalidDataException($"conversation {id} has no events");
        ConversationSnapshot? snapshot = null;
        foreach (var e in events)
            snapshot = Apply(snapshot, id, e);
        return snapshot!;
    }

    /// <exception cref="InvalidDataException">The first event is not the one that opens a conversation.</exception>
    public static ConversationSnapshot Apply(ConversationSnapshot? current, string id, ConversationEvent e)
    {
        var data = ConversationJson.Parse(e);
        if (current is null)
            return Open(id, e, data);
        var next = current with { UpdatedAt = e.At, LastEventId = e.Id };
        return data switch
        {
            StateData s when !next.IsOver => next with { State = s.State, Model = s.Model ?? next.Model, Pending = s.State == ConversationState.Idle ? [] : next.Pending },
            MessageChunk { Role: "user" } => next with { Messages = next.Messages + 1 },
            ApprovalRequested a => next with { Pending = [.. next.Pending, a.CallId] },
            ApprovalAnswered a => next with { Pending = [.. next.Pending.Where(c => c != a.CallId)], Answered = [.. next.Answered, a.CallId] },
            UsageDelta u => AddUsage(next, e.At, u),
            EndedData ended when !next.IsOver => next with { State = FinalState(ended.Reason), EndedReason = ended.Reason, Pending = [] },
            _ => next,
        };
    }

    private static ConversationSnapshot Open(string id, ConversationEvent e, ConversationEventData? data) =>
        data is StateData { State: ConversationState.Starting, Repository: { } repository, Runtime: { } runtime } start
            ? new ConversationSnapshot(id, ConversationState.Starting, repository, runtime, start.Model, start.Preset, start.Actor, e.At, e.At, 0, [], [], null, [], null, e.Id)
            : throw new InvalidDataException($"conversation {id} does not open with a state event that names its repository and runtime");

    private static ConversationSnapshot AddUsage(ConversationSnapshot s, DateTimeOffset at, UsageDelta u)
    {
        var before = s.Usage;
        var usd = (before?.Usd, u.Usd) switch
        {
            (null, null) => (decimal?)null,
            var (a, b) => (a ?? 0) + (b ?? 0),
        };
        return s with
        {
            Usage = new ConversationUsage((before?.InputTokens ?? 0) + u.InputTokens, (before?.OutputTokens ?? 0) + u.OutputTokens, usd),
            UsageLog = [.. s.UsageLog, new UsageEntry(at, u.InputTokens + u.OutputTokens, u.Usd)],
        };
    }

    private static ConversationState FinalState(string reason) => reason switch
    {
        EndedData.Lost => ConversationState.Lost,
        EndedData.Error => ConversationState.Failed,
        _ => ConversationState.Ended,
    };
}
