using System.Text.Json.Serialization;

namespace Chargehand.Modules.Conversations.Api;

/// <summary>Where a conversation is. <see cref="Ended"/>, <see cref="Lost"/> and <see cref="Failed"/> are final.</summary>
public enum ConversationState
{
    /// <summary>The runtime is being started.</summary>
    [JsonStringEnumMemberName("starting")]
    Starting,

    /// <summary>No turn is running; a message starts one.</summary>
    [JsonStringEnumMemberName("idle")]
    Idle,

    /// <summary>A turn is running; a message waits in the queue.</summary>
    [JsonStringEnumMemberName("running")]
    Running,

    /// <summary>The person ended it, or the runtime closed it.</summary>
    [JsonStringEnumMemberName("ended")]
    Ended,

    /// <summary>The server restarted while it was open, as a run is lost (ADR 0018).</summary>
    [JsonStringEnumMemberName("lost")]
    Lost,

    /// <summary>The runtime failed to start or broke down.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,
}
