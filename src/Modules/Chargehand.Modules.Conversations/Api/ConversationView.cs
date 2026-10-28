using Chargehand.Contracts;

namespace Chargehand.Modules.Conversations.Api;

/// <summary>conversation/v1: a conversation as <c>GET /v1/conversations/{id}</c> shows it.</summary>
/// <param name="Actor">The account that started it (ADR 0050); absent for one started from the CLI.</param>
/// <param name="Messages">Messages the person has sent that reached the agent.</param>
/// <param name="Queued">Messages that wait for the running turn to end.</param>
/// <param name="PendingApprovals">Call ids the agent waits on.</param>
/// <param name="Usage">Null while the agent has reported none.</param>
/// <param name="LastEventId">The id of the latest event: read the stream after it to see what happens next.</param>
/// <param name="EndedReason">Set once the conversation is over.</param>
public sealed record ConversationView(string ContractVersion, string Id, ConversationState State, RepositoryRef Repository, string Runtime, string? Model, string? Preset, string? Actor,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Messages, int Queued, IReadOnlyList<string> PendingApprovals, ConversationUsage? Usage, ConversationCaps Caps, long LastEventId,
    string? EndedReason)
{
    public const string Version = "conversation/v1";
}

/// <summary>What the conversation has used so far; <paramref name="Usd"/> is null when the agent has reported no dollars.</summary>
public sealed record ConversationUsage(long InputTokens, long OutputTokens, decimal? Usd);

/// <summary>The limits on one conversation. The account's own caps (ADR 0050) apply besides.</summary>
public sealed record ConversationCaps(int MaxMessages);
