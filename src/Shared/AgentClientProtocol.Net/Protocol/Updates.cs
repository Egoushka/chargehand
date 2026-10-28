using System.Text.Json;

namespace AgentClientProtocol;

/// <summary>A <c>session/update</c> notification: what the agent reports about a session while a turn runs (or while it replays a loaded one).</summary>
public sealed record SessionNotification(string SessionId, SessionUpdate Update);

/// <summary>One kind of session update. Pattern-match on the derived records; <see cref="UnknownSessionUpdate"/> carries a kind this library does not know.</summary>
public abstract record SessionUpdate;

public sealed record UserMessageChunk(ContentBlock Content) : SessionUpdate;

public sealed record AgentMessageChunk(ContentBlock Content) : SessionUpdate;

public sealed record AgentThoughtChunk(ContentBlock Content) : SessionUpdate;

/// <summary>A tool call starts. <see cref="Kind"/> is an <see cref="AcpToolKind"/>, <see cref="Status"/> an <see cref="AcpToolStatus"/>.</summary>
public sealed record ToolCall(
    string ToolCallId,
    string Title,
    string? Kind = null,
    string? Status = null,
    IReadOnlyList<ToolCallContent>? Content = null,
    IReadOnlyList<ToolCallLocation>? Locations = null,
    JsonElement? RawInput = null,
    JsonElement? RawOutput = null) : SessionUpdate;

/// <summary>A tool call changes: only the fields that changed are set. Also the tool call a permission request names.</summary>
public sealed record ToolCallUpdate(
    string ToolCallId,
    string? Title = null,
    string? Kind = null,
    string? Status = null,
    IReadOnlyList<ToolCallContent>? Content = null,
    IReadOnlyList<ToolCallLocation>? Locations = null,
    JsonElement? RawInput = null,
    JsonElement? RawOutput = null) : SessionUpdate;

public sealed record PlanEntry(string Content, string? Priority = null, string? Status = null);

public sealed record PlanUpdate(IReadOnlyList<PlanEntry> Entries) : SessionUpdate;

public sealed record AvailableCommand(string Name, string? Description = null, JsonElement? Input = null);

public sealed record AvailableCommandsUpdate(IReadOnlyList<AvailableCommand> AvailableCommands) : SessionUpdate;

public sealed record CurrentModeUpdate(string CurrentModeId) : SessionUpdate;

public sealed record ConfigOptionUpdate(IReadOnlyList<ConfigOption> ConfigOptions) : SessionUpdate;

/// <summary>Money an agent says a session has cost so far.</summary>
public sealed record Cost(decimal Amount, string Currency);

/// <summary>Context use of a session: <see cref="Used"/> tokens of a window of <see cref="Size"/>, and the cost when the agent reports it.</summary>
public sealed record UsageUpdate(long Used, long Size, Cost? Cost = null) : SessionUpdate;

public sealed record SessionInfoUpdate(string? Title = null, string? UpdatedAt = null) : SessionUpdate;

/// <summary>An update of a kind this version does not model, kept whole so the caller can still log it.</summary>
public sealed record UnknownSessionUpdate(string Kind, JsonElement Raw) : SessionUpdate;
