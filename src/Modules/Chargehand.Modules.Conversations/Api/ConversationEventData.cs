using System.Text.Json.Serialization;
using Chargehand.Contracts;

namespace Chargehand.Modules.Conversations.Api;

/// <summary>The <c>data</c> of an event, one record per type. <see cref="Type"/> is the event's <c>type</c>.</summary>
public abstract record ConversationEventData
{
    [JsonIgnore]
    public abstract string Type { get; }
}

/// <summary>The conversation's state changed. The first event also carries the conversation's start fields, so a client that reads the stream from its start has them.</summary>
/// <param name="State">Only <see cref="ConversationState.Starting"/>, <see cref="ConversationState.Idle"/> or <see cref="ConversationState.Running"/>: how a conversation ends is the <c>ended</c> event.</param>
/// <param name="StopReason">Why the turn that just ended stopped, as the agent said (<c>end_turn</c>, <c>cancelled</c>, ...); with a change to idle.</param>
public sealed record StateData(ConversationState State, string? StopReason = null, RepositoryRef? Repository = null, string? Runtime = null, string? Model = null, string? Preset = null,
    string? Actor = null) : ConversationEventData
{
    [JsonIgnore]
    public override string Type => ConversationEventTypes.State;
}

/// <param name="Role"><c>user</c> for a message the person sent (written when it reaches the agent), <c>assistant</c> for the agent's.</param>
public sealed record MessageChunk(string Role, string Text) : ConversationEventData
{
    [JsonIgnore]
    public override string Type => ConversationEventTypes.MessageChunk;
}

/// <summary>Where a tool call touched a file, as ACP reports it: a client shows a citation as a location.</summary>
public sealed record ToolLocation(string Path, int? Line = null);

/// <param name="Kind">ACP's tool kind: <c>read</c>, <c>edit</c>, <c>execute</c>, <c>search</c> and the like; the agent may send others.</param>
/// <param name="Status">ACP's tool call status: <c>pending</c>, <c>in_progress</c>, <c>completed</c> or <c>failed</c>.</param>
public sealed record ToolCall(string CallId, string Title, string Kind, string Status, IReadOnlyList<ToolLocation>? Locations = null) : ConversationEventData
{
    [JsonIgnore]
    public override string Type => ConversationEventTypes.ToolCall;
}

/// <summary>What changed about a tool call; a field left out did not change.</summary>
public sealed record ToolCallUpdate(string CallId, string? Status = null, string? Title = null, IReadOnlyList<ToolLocation>? Locations = null) : ConversationEventData
{
    [JsonIgnore]
    public override string Type => ConversationEventTypes.ToolCallUpdate;
}

/// <summary>The agent asks before a tool call that edits, runs or fetches. The turn waits until <c>POST /v1/conversations/{id}/approvals/{call}</c> answers it.</summary>
public sealed record ApprovalRequested(string CallId, string Title, string Kind, IReadOnlyList<ToolLocation>? Locations = null) : ConversationEventData
{
    [JsonIgnore]
    public override string Type => ConversationEventTypes.ApprovalRequested;
}

/// <param name="Decision"><c>allow_once</c> or <c>reject</c>: never a saved permission (ADR 0006).</param>
public sealed record ApprovalAnswered(string CallId, string Decision) : ConversationEventData
{
    public const string AllowOnce = "allow_once";
    public const string Reject = "reject";

    [JsonIgnore]
    public override string Type => ConversationEventTypes.ApprovalAnswered;
}

/// <summary>What one turn used, as the agent reported it. <paramref name="Usd"/> is null when the agent reports no dollars.</summary>
public sealed record UsageDelta(long InputTokens, long OutputTokens, decimal? Usd = null) : ConversationEventData
{
    [JsonIgnore]
    public override string Type => ConversationEventTypes.Usage;
}

/// <param name="Fatal">The conversation cannot go on; an <c>ended</c> event with reason <c>error</c> follows.</param>
public sealed record ErrorData(string Message, bool Fatal) : ConversationEventData
{
    [JsonIgnore]
    public override string Type => ConversationEventTypes.Error;
}

/// <param name="Reason"><c>user</c> (deleted), <c>runtime_exited</c>, <c>lost</c> (the server restarted) or <c>error</c>.</param>
public sealed record EndedData(string Reason) : ConversationEventData
{
    public const string User = "user";
    public const string RuntimeExited = "runtime_exited";
    public const string Lost = "lost";
    public const string Error = "error";

    [JsonIgnore]
    public override string Type => ConversationEventTypes.Ended;
}
