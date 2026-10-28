using System.Text.Json;

namespace Chargehand.Modules.Conversations.Api;

/// <summary>conversation-event/v1: one thing that happened in a conversation. <paramref name="Id"/> counts from 1 within the conversation, so a client that read up to
/// id N resumes with <c>Last-Event-ID: N</c> and neither misses nor repeats an event. <paramref name="Data"/> is the shape <see cref="ConversationJson.Parse"/> reads for the type.</summary>
public sealed record ConversationEvent(string ContractVersion, long Id, DateTimeOffset At, string Type, JsonElement Data)
{
    public const string Version = "conversation-event/v1";
}

/// <summary>The <c>type</c> of an event.</summary>
public static class ConversationEventTypes
{
    public const string State = "state";
    public const string MessageChunk = "message_chunk";
    public const string ToolCall = "tool_call";
    public const string ToolCallUpdate = "tool_call_update";
    public const string ApprovalRequested = "approval_requested";
    public const string ApprovalAnswered = "approval_answered";
    public const string Usage = "usage";
    public const string Error = "error";
    public const string Ended = "ended";
}
