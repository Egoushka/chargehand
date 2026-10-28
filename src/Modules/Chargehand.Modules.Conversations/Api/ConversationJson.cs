using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Chargehand.Contracts;

namespace Chargehand.Modules.Conversations.Api;

/// <summary>The module's JSON without reflection (ADR 0060): snake_case names, nulls left out, enums as their wire words. A host serializes a <see cref="ConversationView"/> and a
/// <see cref="ConversationEvent"/> with the type infos here.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ConversationView))]
[JsonSerializable(typeof(ConversationEvent))]
[JsonSerializable(typeof(StateData))]
[JsonSerializable(typeof(MessageChunk))]
[JsonSerializable(typeof(ToolCall))]
[JsonSerializable(typeof(ToolCallUpdate))]
[JsonSerializable(typeof(ApprovalRequested))]
[JsonSerializable(typeof(ApprovalAnswered))]
[JsonSerializable(typeof(UsageDelta))]
[JsonSerializable(typeof(ErrorData))]
[JsonSerializable(typeof(EndedData))]
internal sealed partial class ConversationsJsonContext : JsonSerializerContext;

public static class ConversationJson
{
    public static JsonTypeInfo<ConversationView> View => ConversationsJsonContext.Default.ConversationView;

    public static JsonTypeInfo<ConversationEvent> Event => ConversationsJsonContext.Default.ConversationEvent;

    /// <summary>The event that records <paramref name="data"/> as number <paramref name="id"/>.</summary>
    public static ConversationEvent ToEvent(long id, DateTimeOffset at, ConversationEventData data)
    {
        var c = ConversationsJsonContext.Default;
        JsonElement element = data switch
        {
            StateData d => JsonSerializer.SerializeToElement(d, c.StateData),
            MessageChunk d => JsonSerializer.SerializeToElement(d, c.MessageChunk),
            ToolCall d => JsonSerializer.SerializeToElement(d, c.ToolCall),
            ToolCallUpdate d => JsonSerializer.SerializeToElement(d, c.ToolCallUpdate),
            ApprovalRequested d => JsonSerializer.SerializeToElement(d, c.ApprovalRequested),
            ApprovalAnswered d => JsonSerializer.SerializeToElement(d, c.ApprovalAnswered),
            UsageDelta d => JsonSerializer.SerializeToElement(d, c.UsageDelta),
            ErrorData d => JsonSerializer.SerializeToElement(d, c.ErrorData),
            EndedData d => JsonSerializer.SerializeToElement(d, c.EndedData),
            _ => throw new ArgumentException($"no event type for {data.GetType().Name}", nameof(data)),
        };
        return new ConversationEvent(ConversationEvent.Version, id, at, data.Type, element);
    }

    /// <summary>What an event says; null for a type this version does not know, so a journal written by a later version still reads.</summary>
    /// <exception cref="InvalidDataException">The data does not have the shape its type names.</exception>
    public static ConversationEventData? Parse(ConversationEvent e)
    {
        var c = ConversationsJsonContext.Default;
        try
        {
            return e.Type switch
            {
                ConversationEventTypes.State => e.Data.Deserialize(c.StateData),
                ConversationEventTypes.MessageChunk => e.Data.Deserialize(c.MessageChunk),
                ConversationEventTypes.ToolCall => e.Data.Deserialize(c.ToolCall),
                ConversationEventTypes.ToolCallUpdate => e.Data.Deserialize(c.ToolCallUpdate),
                ConversationEventTypes.ApprovalRequested => e.Data.Deserialize(c.ApprovalRequested),
                ConversationEventTypes.ApprovalAnswered => e.Data.Deserialize(c.ApprovalAnswered),
                ConversationEventTypes.Usage => e.Data.Deserialize(c.UsageDelta),
                ConversationEventTypes.Error => e.Data.Deserialize(c.ErrorData),
                ConversationEventTypes.Ended => e.Data.Deserialize(c.EndedData),
                _ => null,
            };
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"event {e.Id} of type {e.Type} has data of another shape: {ex.Message}", ex);
        }
    }
}
