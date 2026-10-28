using Chargehand.Acp;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Runtime;
using static Chargehand.Tests.AcpTestSupport;

namespace Chargehand.Tests;

/// <summary>The runtime as WorkerNode drives it: create, instruct, submit, await, interrupt, read, fork and end.</summary>
public class AcpRuntimeLifecycleTests
{
    [Fact]
    public async Task A_prompt_streams_into_one_assistant_message_that_the_node_can_assemble_from()
    {
        await using var agent = new AcpFixture();

        var (_, messages, outcome) = await agent.Turn("say hello from the agent");

        Assert.Equal(IdleOutcome.Succeeded, outcome);
        Assert.Equal("hello from the agent", messages.First(m => m.Kind == WorkerMessageKind.Assistant).Text);
        Assert.Equal([WorkerMessageKind.Idle, WorkerMessageKind.Assistant, WorkerMessageKind.User], messages.Select(m => m.Kind));
    }

    [Fact]
    public async Task A_tool_call_lands_in_the_messages_evidence_scope_with_its_location_and_output()
    {
        await using var agent = new AcpFixture();

        var (_, messages, _) = await agent.Turn("tool");

        var call = messages.First(m => m.Kind == WorkerMessageKind.Assistant);
        Assert.Contains("src/a.cs:12", call.ToolOutput, StringComparison.Ordinal);
        Assert.Contains("class A {}", call.ToolOutput, StringComparison.Ordinal);
        Assert.Equal(["class A {}"], call.ToolResults);
    }

    [Fact]
    public async Task A_second_turn_goes_to_the_same_agent_process_and_session()
    {
        await using var agent = new AcpFixture();
        var session = await agent.Runtime.CreateAsync(Spec(agent.Dir.Path), default);

        await agent.Runtime.SubmitAsync(session.Id, "say first", default);
        await agent.Runtime.AwaitIdleAsync(session.Id, default);
        await agent.Runtime.SubmitAsync(session.Id, "say second", default);
        await agent.Runtime.AwaitIdleAsync(session.Id, default);

        var texts = (await agent.Runtime.ReadMessagesAsync(session.Id, default)).Where(m => m.Kind == WorkerMessageKind.Assistant).Select(m => m.Text);
        Assert.Equal(["second", "first"], texts);
    }

    [Fact]
    public async Task An_interrupt_ends_a_running_turn_as_interrupted()
    {
        await using var agent = new AcpFixture();
        var session = await agent.Runtime.CreateAsync(Spec(agent.Dir.Path), default);
        await agent.Runtime.SubmitAsync(session.Id, "slow", default);
        Assert.True(SpinWait.SpinUntil(() => agent.Runtime.ReadMessagesAsync(session.Id, default).GetAwaiter().GetResult().Any(m => m.Kind == WorkerMessageKind.Assistant),
            TimeSpan.FromSeconds(30)));

        await agent.Runtime.InterruptAsync(session.Id, default);

        Assert.Equal(IdleOutcome.Interrupted, await agent.Runtime.AwaitIdleAsync(session.Id, default));
    }

    [Fact]
    public async Task An_agent_that_dies_mid_turn_fails_the_turn_with_what_it_wrote_to_stderr()
    {
        await using var agent = new AcpFixture();

        var (_, messages, outcome) = await agent.Turn("die");

        Assert.Equal(IdleOutcome.Failed, outcome);
        Assert.Contains("boom", messages.First(m => m.Kind == WorkerMessageKind.Idle).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_that_cannot_start_is_runtime_unavailable_at_the_first_prompt()
    {
        await using var agent = new AcpFixture(Fake(command: "/nonexistent/acp-agent"));
        var session = await agent.Runtime.CreateAsync(Spec(agent.Dir.Path), default);

        var e = await Assert.ThrowsAsync<ChargehandException>(() => agent.Runtime.SubmitAsync(session.Id, "say hi", default));

        Assert.Equal(ErrorCode.RuntimeUnavailable, e.Code);
        Assert.Contains("Fake agent", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_auth_method_the_agent_does_not_offer_is_refused_naming_the_ones_it_does()
    {
        await using var agent = new AcpFixture(Fake(authMethod: "oauth-personal"));
        var session = await agent.Runtime.CreateAsync(Spec(agent.Dir.Path), default);

        var e = await Assert.ThrowsAsync<ChargehandException>(() => agent.Runtime.SubmitAsync(session.Id, "say hi", default));

        Assert.Equal(ErrorCode.RuntimeUnavailable, e.Code);
        Assert.Contains("fake-key", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_with_no_auth_method_pinned_is_not_asked_to_sign_in()
    {
        await using var agent = new AcpFixture(Fake(authMethod: null));

        var (_, _, outcome) = await agent.Turn("say hi");

        Assert.Equal(IdleOutcome.Succeeded, outcome);
    }

    [Fact]
    public async Task A_fork_before_the_first_user_message_is_a_fresh_session_and_any_other_is_not_supported()
    {
        await using var agent = new AcpFixture();
        var session = await agent.Runtime.CreateAsync(Spec(agent.Dir.Path), default);
        await agent.Runtime.SetInstructionAsync(session.Id, "core", "be brief", default);
        await agent.Runtime.SubmitAsync(session.Id, "say hi", default);
        await agent.Runtime.AwaitIdleAsync(session.Id, default);
        var first = (await agent.Runtime.ReadMessagesAsync(session.Id, default)).Last(m => m.Kind == WorkerMessageKind.User).Id;

        var fork = await agent.Runtime.ForkAsync(session.Id, first, default);

        Assert.NotEqual(session.Id, fork.Id);
        await Assert.ThrowsAsync<NotSupportedException>(() => agent.Runtime.ForkAsync(session.Id, "other", default));
        await Assert.ThrowsAsync<NotSupportedException>(() => agent.Runtime.CompactAsync(session.Id, default));
    }

    [Fact]
    public async Task Instructions_cannot_change_after_the_first_prompt()
    {
        await using var agent = new AcpFixture();
        var session = await agent.Runtime.CreateAsync(Spec(agent.Dir.Path), default);
        await agent.Runtime.SubmitAsync(session.Id, "say hi", default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Runtime.SetInstructionAsync(session.Id, "late", "x", default));
    }

    [Fact]
    public void Instructions_head_the_first_prompt_each_in_a_block_that_names_it()
    {
        var preamble = AcpWorkerRuntime.Preamble([("core", "one"), ("preset", "two")]);

        Assert.Equal("<instructions name=\"core\">\none\n</instructions>\n\n<instructions name=\"preset\">\ntwo\n</instructions>\n\n", preamble);
    }

    [Fact]
    public async Task One_shot_generation_returns_the_agents_text_and_leaves_no_session()
    {
        await using var agent = new AcpFixture();

        var text = await agent.Runtime.GenerateAsync(null, "say hello there friend", default);

        Assert.Equal("hello there friend", text);
    }

    [Fact]
    public async Task Ending_the_run_stops_its_agents_and_forgets_its_sessions()
    {
        await using var agent = new AcpFixture();
        var (id, _, _) = await agent.Turn("say hi");

        await agent.Runtime.EndRunAsync("run-1", default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Runtime.ReadMessagesAsync(id, default));
    }
}
