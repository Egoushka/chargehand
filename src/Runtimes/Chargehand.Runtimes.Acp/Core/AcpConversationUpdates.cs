using AgentClientProtocol;
using Chargehand.Modules.Conversations.Api;
using ConversationToolCall = Chargehand.Modules.Conversations.Api.ToolCall;
using ConversationToolCallUpdate = Chargehand.Modules.Conversations.Api.ToolCallUpdate;

namespace Chargehand.Runtimes.Acp.Core;

/// <summary>What an agent's <c>session/update</c> is to a conversation's stream (ADR 0086, decision 3). The agent's thoughts, plans and replays of earlier turns stay out; usage is the
/// runtime's own business. Paths are shown relative to the checkout, so a client sees the repository's files and not the server's directories.</summary>
internal static class AcpConversationUpdates
{
    public static ConversationEventData? Translate(SessionUpdate update, string directory) => update switch
    {
        AgentMessageChunk { Content: { Type: "text", Text: { Length: > 0 } text } } => new MessageChunk("assistant", text),
        AgentClientProtocol.ToolCall call => new ConversationToolCall(call.ToolCallId, Relative(call.Title, directory), call.Kind ?? AcpToolKind.Other, call.Status ?? AcpToolStatus.Pending, Locations(call.Locations, directory)),
        AgentClientProtocol.ToolCallUpdate change => new ConversationToolCallUpdate(change.ToolCallId, change.Status, change.Title is null ? null : Relative(change.Title, directory), Locations(change.Locations, directory)),
        _ => null,
    };

    public static ApprovalRequested Approval(RequestPermissionRequest request, string directory) =>
        new(request.ToolCall.ToolCallId, Relative(request.ToolCall.Title ?? "tool call", directory), request.ToolCall.Kind ?? AcpToolKind.Other, Locations(request.ToolCall.Locations, directory));

    public static string Relative(string text, string directory) => text.Replace(Prefix(directory), "", StringComparison.Ordinal);

    private static IReadOnlyList<ToolLocation>? Locations(IReadOnlyList<ToolCallLocation>? locations, string directory) =>
        locations is { Count: > 0 } ? [.. locations.Select(l => new ToolLocation(Relative(l.Path, directory), l.Line))] : null;

    private static string Prefix(string directory) => directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
}
