using System.Text.Json;

namespace AgentClientProtocol;

/// <summary>Reads the <c>params</c> of a <c>session/update</c> into a typed update. An update that does not fit its kind comes back as <see cref="UnknownSessionUpdate"/>.</summary>
internal static class SessionUpdateParser
{
    public static SessionNotification? Parse(JsonElement args)
    {
        if (!args.TryGetProperty("sessionId", out var id) || id.ValueKind != JsonValueKind.String
            || !args.TryGetProperty("update", out var update) || update.ValueKind != JsonValueKind.Object)
            return null;
        return new SessionNotification(id.GetString()!, ParseUpdate(update));
    }

    private static SessionUpdate ParseUpdate(JsonElement update)
    {
        var kind = update.TryGetProperty("sessionUpdate", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()! : "";
        try
        {
            SessionUpdate? parsed = kind switch
            {
                "user_message_chunk" => update.Deserialize(AcpJsonContext.Default.UserMessageChunk),
                "agent_message_chunk" => update.Deserialize(AcpJsonContext.Default.AgentMessageChunk),
                "agent_thought_chunk" => update.Deserialize(AcpJsonContext.Default.AgentThoughtChunk),
                "tool_call" => update.Deserialize(AcpJsonContext.Default.ToolCall),
                "tool_call_update" => update.Deserialize(AcpJsonContext.Default.ToolCallUpdate),
                "plan" => update.Deserialize(AcpJsonContext.Default.PlanUpdate),
                "available_commands_update" => update.Deserialize(AcpJsonContext.Default.AvailableCommandsUpdate),
                "current_mode_update" => update.Deserialize(AcpJsonContext.Default.CurrentModeUpdate),
                "config_option_update" => update.Deserialize(AcpJsonContext.Default.ConfigOptionUpdate),
                "usage_update" => update.Deserialize(AcpJsonContext.Default.UsageUpdate),
                "session_info_update" => update.Deserialize(AcpJsonContext.Default.SessionInfoUpdate),
                _ => null,
            };
            return parsed ?? new UnknownSessionUpdate(kind, update.Clone());
        }
        catch (JsonException)
        {
            return new UnknownSessionUpdate(kind, update.Clone());
        }
    }
}
