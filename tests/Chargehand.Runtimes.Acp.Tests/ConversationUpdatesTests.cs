using AgentClientProtocol;
using Chargehand.Modules.Conversations.Api;
using Chargehand.Runtimes.Acp.Core;
using Shown = Chargehand.Modules.Conversations.Api.ToolCall;
using ShownUpdate = Chargehand.Modules.Conversations.Api.ToolCallUpdate;

namespace Chargehand.Runtimes.Acp.Tests;

/// <summary>What an agent's updates become on a conversation's stream.</summary>
public class ConversationUpdatesTests
{
    private const string Checkout = "/work/.checkouts/repo-1a2b3c4d/0123456789ab";

    private static ConversationEventData? Translate(SessionUpdate update) => AcpConversationUpdates.Translate(update, Checkout);

    [Fact]
    public void The_agents_words_are_assistant_message_chunks_and_an_empty_one_is_nothing()
    {
        Assert.Equal(new MessageChunk("assistant", "Hello"), Translate(new AgentMessageChunk(ContentBlock.FromText("Hello"))));
        Assert.Null(Translate(new AgentMessageChunk(ContentBlock.FromText(""))));
    }

    [Fact]
    public void Thoughts_replays_plans_usage_and_kinds_nobody_has_seen_stay_off_the_stream()
    {
        Assert.Null(Translate(new AgentThoughtChunk(ContentBlock.FromText("hmm"))));
        Assert.Null(Translate(new UserMessageChunk(ContentBlock.FromText("earlier"))));
        Assert.Null(Translate(new PlanUpdate([new PlanEntry("step")])));
        Assert.Null(Translate(new UsageUpdate(10, 100)));
        Assert.Null(Translate(new UnknownSessionUpdate("future_thing", default)));
    }

    [Fact]
    public void A_tool_call_shows_the_repositorys_paths_not_the_servers()
    {
        var call = new AgentClientProtocol.ToolCall("t1", $"Read {Checkout}/src/a.cs", AcpToolKind.Read, AcpToolStatus.InProgress, Locations: [new ToolCallLocation($"{Checkout}/src/a.cs", 12), new ToolCallLocation("/usr/lib/x.so")]);

        var shown = Assert.IsType<Shown>(Translate(call));

        Assert.Equal(("t1", "Read src/a.cs", "read", "in_progress"), (shown.CallId, shown.Title, shown.Kind, shown.Status));
        Assert.Equal([new ToolLocation("src/a.cs", 12), new ToolLocation("/usr/lib/x.so")], shown.Locations);
    }

    [Fact]
    public void A_tool_call_with_no_kind_or_status_is_other_and_pending()
    {
        var shown = Assert.IsType<Shown>(Translate(new AgentClientProtocol.ToolCall("t1", "Think")));

        Assert.Equal(("other", "pending", null), (shown.Kind, shown.Status, shown.Locations));
    }

    [Fact]
    public void A_tool_call_update_carries_only_what_changed()
    {
        var shown = Assert.IsType<ShownUpdate>(Translate(new AgentClientProtocol.ToolCallUpdate("t1", Status: AcpToolStatus.Completed)));

        Assert.Equal(new ShownUpdate("t1", "completed"), shown);
    }

    [Fact]
    public void An_approval_names_the_call_its_kind_and_where_it_would_touch()
    {
        var request = new RequestPermissionRequest("s1", new AgentClientProtocol.ToolCallUpdate("t2", Title: $"Run {Checkout}/build.sh", Kind: AcpToolKind.Execute),
            [new PermissionOption("go", "Allow", AcpPermissionKind.AllowOnce)]);

        var asked = AcpConversationUpdates.Approval(request, Checkout);

        Assert.Equal(new ApprovalRequested("t2", "Run build.sh", "execute"), asked);
    }
}
