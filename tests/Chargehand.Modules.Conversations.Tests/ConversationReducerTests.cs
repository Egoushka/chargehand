using System.Text.Json;
using Chargehand.Modules.Conversations.Api;
using Chargehand.Modules.Conversations.Core;

namespace Chargehand.Modules.Conversations.Tests;

/// <summary>A conversation's state is its events folded: the same function runs live and when the journal is read after a restart.</summary>
public class ConversationReducerTests
{
    private static readonly ToolLocation[] Where = [new("src/Widget.cs", 12)];

    private static ConversationSnapshot Fold(Events events) => ConversationReducer.Fold("conv-1", events.List);

    [Fact]
    public void The_first_two_events_open_the_conversation_with_its_start_fields()
    {
        var snapshot = Fold(Events.Opened(model: "m-1", preset: "cheap"));

        Assert.Equal(ConversationState.Idle, snapshot.State);
        Assert.Equal(Events.Repository, snapshot.Repository);
        Assert.Equal(("fake", "m-1", "cheap", "alice"), (snapshot.Runtime, snapshot.Model, snapshot.Preset, snapshot.Actor));
        Assert.Equal(Events.Start, snapshot.CreatedAt);
        Assert.Equal(Events.Start.AddSeconds(1), snapshot.UpdatedAt);
        Assert.Equal(2, snapshot.LastEventId);
    }

    [Fact]
    public void The_model_the_agent_settled_on_replaces_the_one_asked_for()
    {
        var events = Events.Opened(model: "asked").Add(new StateData(ConversationState.Running)).Add(new StateData(ConversationState.Idle, Model: "settled"));

        Assert.Equal("settled", Fold(events).Model);
        Assert.Equal("settled", Fold(events.Add(new StateData(ConversationState.Running))).Model);
    }

    [Fact]
    public void A_user_message_counts_and_a_turn_runs_until_the_runtime_ends_it()
    {
        var events = Events.Opened()
            .Add(new MessageChunk("user", "list the widgets"))
            .Add(new StateData(ConversationState.Running))
            .Add(new MessageChunk("assistant", "There are "));

        Assert.Equal((ConversationState.Running, 1), (Fold(events).State, Fold(events).Messages));

        events.Add(new StateData(ConversationState.Idle, StopReason: "end_turn"));
        Assert.Equal(ConversationState.Idle, Fold(events).State);
    }

    [Fact]
    public void An_approval_is_pending_until_it_is_answered_and_the_answer_is_remembered()
    {
        var events = Events.Opened().Add(new ApprovalRequested("call-1", "Edit Widget.cs", "edit", Where));
        var pending = Fold(events);
        Assert.Equal(["call-1"], pending.Pending);
        Assert.Empty(pending.Answered);

        events.Add(new ApprovalAnswered("call-1", "allow_once"));
        var answered = Fold(events);
        Assert.Empty(answered.Pending);
        Assert.Equal(["call-1"], answered.Answered);
    }

    [Fact]
    public void A_turn_that_ends_leaves_no_approval_waiting()
    {
        var events = Events.Opened().Add(new StateData(ConversationState.Running)).Add(new ApprovalRequested("call-1", "Edit Widget.cs", "edit", null));
        Assert.Equal(["call-1"], Fold(events).Pending);

        events.Add(new StateData(ConversationState.Idle, StopReason: "cancelled"));

        Assert.Empty(Fold(events).Pending);
    }

    [Fact]
    public void Usage_adds_up_and_a_dollar_figure_appears_only_when_an_agent_reported_one()
    {
        var events = Events.Opened().Add(new UsageDelta(100, 20, null)).Add(new UsageDelta(50, 5, null));
        Assert.Equal(new ConversationUsage(150, 25, null), Fold(events).Usage);

        events.Add(new UsageDelta(10, 1, 0.25m)).Add(new UsageDelta(10, 1, 0.5m));
        var snapshot = Fold(events);
        Assert.Equal(new ConversationUsage(170, 27, 0.75m), snapshot.Usage);
        Assert.Equal(4, snapshot.UsageLog.Count);
        Assert.Equal(0.5m, snapshot.UsageLog[^1].Usd);
    }

    [Fact]
    public void A_conversation_without_usage_has_none()
    {
        Assert.Null(Fold(Events.Opened()).Usage);
    }

    [Theory]
    [InlineData("user", ConversationState.Ended)]
    [InlineData("runtime_exited", ConversationState.Ended)]
    [InlineData("lost", ConversationState.Lost)]
    [InlineData("error", ConversationState.Failed)]
    public void The_reason_an_conversation_ended_decides_its_final_state(string reason, ConversationState expected)
    {
        var snapshot = Fold(Events.Opened().Add(new EndedData(reason)));

        Assert.Equal(expected, snapshot.State);
        Assert.Equal(reason, snapshot.EndedReason);
        Assert.True(snapshot.IsOver);
    }

    [Fact]
    public void Pending_approvals_are_dropped_when_the_conversation_ends()
    {
        var snapshot = Fold(Events.Opened().Add(new ApprovalRequested("call-1", "Run tests", "execute", null)).Add(new EndedData("user")));

        Assert.Empty(snapshot.Pending);
    }

    [Fact]
    public void A_tool_call_an_error_and_an_event_type_from_a_later_version_change_nothing_but_the_clock()
    {
        var events = Events.Opened();
        var before = Fold(events);
        events.Add(new ToolCall("call-1", "Read Widget.cs", "read", "pending", Where))
            .Add(new ToolCallUpdate("call-1", "completed", null, null))
            .Add(new ErrorData("tool failed", false));
        using var doc = JsonDocument.Parse("{}");
        var future = new List<ConversationEvent>(events.List) { new(ConversationEvent.Version, 6, Events.Start.AddMinutes(1), "plan", doc.RootElement.Clone()) };

        var after = ConversationReducer.Fold("conv-1", future);

        Assert.Equal((before.State, before.Messages, before.Usage), (after.State, after.Messages, after.Usage));
        Assert.Empty(after.Pending);
        Assert.Equal((Events.Start.AddMinutes(1), 6), (after.UpdatedAt, after.LastEventId));
    }

    [Fact]
    public void A_journal_that_does_not_open_with_a_state_event_is_refused()
    {
        var events = new Events().Add(new MessageChunk("user", "hello"));

        Assert.Throws<InvalidDataException>(() => Fold(events));
        Assert.Throws<InvalidDataException>(() => ConversationReducer.Fold("conv-1", []));
    }
}
