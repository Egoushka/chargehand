using Chargehand.Contracts;
using Chargehand.Modules.Conversations.Api;

namespace Chargehand.Modules.Conversations.Core;

/// <summary>What one usage event added, kept so a window's spend can be summed (ADR 0050). <paramref name="Usd"/> is null when the agent reported no dollars.</summary>
internal sealed record UsageEntry(DateTimeOffset At, long Tokens, decimal? Usd);

/// <summary>A conversation as its events leave it. Immutable: <see cref="ConversationReducer"/> returns the next one.</summary>
internal sealed record ConversationSnapshot(string Id, ConversationState State, RepositoryRef Repository, string Runtime, string? Model, string? Preset, string? Actor,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Messages, IReadOnlyList<string> Pending, IReadOnlyList<string> Answered, ConversationUsage? Usage,
    IReadOnlyList<UsageEntry> UsageLog, string? EndedReason, long LastEventId)
{
    public bool IsOver => State is ConversationState.Ended or ConversationState.Lost or ConversationState.Failed;

    public ConversationView ToView(ConversationCaps caps, int queued) =>
        new(ConversationView.Version, Id, State, Repository, Runtime, Model, Preset, Actor, CreatedAt, UpdatedAt, Messages, queued, Pending, Usage, caps, LastEventId, EndedReason);
}
