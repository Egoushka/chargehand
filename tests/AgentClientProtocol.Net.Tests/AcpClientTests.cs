namespace AgentClientProtocol.Tests;

/// <summary>The acceptance checks of Cycle A, Task 3: the client against a fake agent process.</summary>
public class AcpClientTests
{
    [Fact]
    public async Task Initialize_negotiates_version_1_and_reports_the_agent()
    {
        await using var h = Harness.Start();

        var result = await h.Client.InitializeAsync();

        Assert.Equal(1, result.ProtocolVersion);
        Assert.Equal("fake-agent", result.AgentInfo?.Name);
        Assert.True(result.AgentCapabilities?.LoadSession);
        Assert.Equal("fake-key", Assert.Single(result.AuthMethods!).Id);
        Assert.Same(result, h.Client.Agent);
    }

    [Fact]
    public async Task Initialize_refuses_an_agent_that_speaks_another_version()
    {
        await using var h = Harness.Start(agentArgs: ["--protocol-version", "2"]);

        var error = await Assert.ThrowsAsync<AcpProtocolException>(() => h.Client.InitializeAsync());

        Assert.Contains("version 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_prompt_streams_chunks_in_order()
    {
        await using var h = Harness.Start();
        var session = await h.NewSessionAsync();

        var result = await session.PromptAsync("say abcdefghi");

        Assert.Equal(AcpStopReason.EndTurn, result.StopReason);
        Assert.Equal(["abc", "def", "ghi"], h.Chunks);
        Assert.All(h.Updates, n => Assert.Equal(session.Id, n.SessionId));
    }

    [Fact]
    public async Task A_permission_request_reaches_the_callback_and_its_answer_is_sent()
    {
        RequestPermissionRequest? seen = null;
        await using var h = Harness.Start(_ => new AcpClientOptions
        {
            OnRequestPermission = (request, _) =>
            {
                seen = request;
                return ValueTask.FromResult(RequestPermissionOutcome.Selected("allow-once"));
            },
        });
        var session = await h.NewSessionAsync();

        await session.PromptAsync("ask execute");

        Assert.NotNull(seen);
        Assert.Equal(session.Id, seen.SessionId);
        Assert.Equal(AcpToolKind.Execute, seen.ToolCall.Kind);
        Assert.Equal("t2", seen.ToolCall.ToolCallId);
        Assert.Equal(["allow-once", "reject"], seen.Options.Select(o => o.OptionId));
        Assert.Equal([AcpPermissionKind.AllowOnce, AcpPermissionKind.RejectOnce], seen.Options.Select(o => o.Kind));
        Assert.Equal(["permission:selected:allow-once"], h.Chunks);
    }

    [Fact]
    public async Task A_rejection_is_sent_as_the_chosen_option()
    {
        await using var h = Harness.Start(_ => new AcpClientOptions
        {
            OnRequestPermission = (request, _) => ValueTask.FromResult(RequestPermissionOutcome.Selected(request.Options.First(o => o.Kind == AcpPermissionKind.RejectOnce).OptionId)),
        });
        var session = await h.NewSessionAsync();

        await session.PromptAsync("ask edit");

        Assert.Equal(["permission:selected:reject"], h.Chunks);
    }

    [Fact]
    public async Task Without_a_permission_callback_every_request_is_cancelled()
    {
        await using var h = Harness.Start();
        var session = await h.NewSessionAsync();

        await session.PromptAsync("ask edit");

        Assert.Equal(["permission:cancelled:"], h.Chunks);
    }

    [Fact]
    public async Task Cancel_ends_the_turn_with_cancelled()
    {
        await using var h = Harness.Start();
        var session = await h.NewSessionAsync();
        var turn = session.PromptAsync("slow");
        await h.WaitForUpdateAsync(u => u is AgentMessageChunk);

        await session.CancelAsync();
        var result = await turn.WaitAsync(Harness.Deadline);

        Assert.Equal(AcpStopReason.Cancelled, result.StopReason);
        Assert.False(session.IsTurnActive);
    }

    [Fact]
    public async Task Cancelling_the_token_cancels_the_turn_and_returns_its_result()
    {
        await using var h = Harness.Start();
        var session = await h.NewSessionAsync();
        using var cts = new CancellationTokenSource();
        var turn = session.PromptAsync("slow", cts.Token);
        await h.WaitForUpdateAsync(u => u is AgentMessageChunk);

        await cts.CancelAsync();
        var result = await turn.WaitAsync(Harness.Deadline);

        Assert.Equal(AcpStopReason.Cancelled, result.StopReason);
    }

    [Fact]
    public async Task A_message_sent_mid_turn_is_queued_and_runs_after_the_turn()
    {
        await using var h = Harness.Start();
        var session = await h.NewSessionAsync();
        var first = session.PromptAsync("gate");
        await h.WaitForUpdateAsync(u => u is AgentMessageChunk { Content.Text: "started" });

        var second = session.PromptAsync("say xyz");
        Assert.Equal(1, session.QueuedPrompts);
        Assert.False(second.IsCompleted);

        await session.SetModeAsync("release"); // the fake agent ends "gate" on this
        await first.WaitAsync(Harness.Deadline);
        await second.WaitAsync(Harness.Deadline);

        Assert.Equal(["started", "released", "x", "y", "z"], h.Chunks);
        Assert.Equal(0, session.QueuedPrompts);
    }

    [Fact]
    public async Task An_agent_that_dies_mid_turn_surfaces_as_an_error_within_the_deadline()
    {
        await using var h = Harness.Start();
        var session = await h.NewSessionAsync();

        var error = await Assert.ThrowsAsync<AcpConnectionException>(() => session.PromptAsync("die").WaitAsync(Harness.Deadline));

        Assert.Equal(3, error.ExitCode);
        Assert.Contains("boom", error.StderrTail, StringComparison.Ordinal);
        Assert.Contains("exited with code 3", error.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<AcpConnectionException>(() => session.PromptAsync("say again"));
        Assert.Contains("boom", h.Client.StderrTail, StringComparison.Ordinal);
    }
}
