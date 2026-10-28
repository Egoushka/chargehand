using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Conversations.Api;
using Chargehand.Modules.Conversations.Core;

namespace Chargehand.Modules.Conversations.Tests;

/// <summary>What the module writes is what conversation/v1 and conversation-event/v1 say, whichever type of event it is.</summary>
public class ConversationSchemaTests
{
    private static readonly ToolLocation[] Where = [new("src/Widget.cs", 12), new("README.md", null)];

    public static TheoryData<ConversationEventData> Data() => new()
    {
        new StateData(ConversationState.Starting, Repository: Events.Repository, Runtime: "fake", Model: "m-1", Preset: "cheap", Actor: "alice"),
        new StateData(ConversationState.Idle, StopReason: "end_turn"),
        new MessageChunk("assistant", "hello"),
        new ToolCall("call-1", "Read Widget.cs", "read", "pending", Where),
        new ToolCallUpdate("call-1", "completed", null, null),
        new ApprovalRequested("call-2", "Edit Widget.cs", "edit", Where),
        new ApprovalAnswered("call-2", "allow_once"),
        new UsageDelta(120, 30, 0.01m),
        new UsageDelta(120, 30, null),
        new ErrorData("the agent stopped answering", true),
        new EndedData("user"),
    };

    [Theory]
    [MemberData(nameof(Data))]
    public void Every_type_of_event_validates_against_the_event_schema(ConversationEventData data)
    {
        var e = ConversationJson.ToEvent(7, Events.Start, data);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(e, ConversationJson.Event));

        Assert.Empty(ContractSchemas.Validate(ContractSchemas.ConversationEvent, doc.RootElement));
        Assert.Equal(data.Type, e.Type);
        Assert.Equal(e.Data.GetRawText(), ConversationJson.ToEvent(7, Events.Start, ConversationJson.Parse(e)!).Data.GetRawText());
    }

    [Fact]
    public void A_conversation_with_everything_set_validates_against_the_conversation_schema()
    {
        var events = Events.Opened(model: "m-1", preset: "cheap")
            .Add(new MessageChunk("user", "edit it"))
            .Add(new StateData(ConversationState.Running))
            .Add(new UsageDelta(100, 20, 0.02m))
            .Add(new ApprovalRequested("call-1", "Edit Widget.cs", "edit", Where));
        var view = ConversationReducer.Fold("conv-20261028-090000-abc123", events.List).ToView(new ConversationCaps(200), queued: 1);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(view, ConversationJson.View));

        Assert.Empty(ContractSchemas.Validate(ContractSchemas.Conversation, doc.RootElement));
        Assert.Equal("running", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal("call-1", doc.RootElement.GetProperty("pending_approvals")[0].GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("queued").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("messages").GetInt32());
    }

    [Fact]
    public void A_bare_conversation_leaves_out_what_it_does_not_have()
    {
        var view = ConversationReducer.Fold("conv-1", Events.Opened(actor: null).List).ToView(new ConversationCaps(200), queued: 0);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(view, ConversationJson.View));

        Assert.Empty(ContractSchemas.Validate(ContractSchemas.Conversation, doc.RootElement));
        foreach (var absent in new[] { "model", "preset", "actor", "usage", "ended_reason" })
            Assert.False(doc.RootElement.TryGetProperty(absent, out _), absent);
    }
}
