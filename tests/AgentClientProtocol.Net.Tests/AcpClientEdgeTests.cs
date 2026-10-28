using System.Text.Json;

namespace AgentClientProtocol.Tests;

/// <summary>Behaviour beyond the acceptance list: what the client offers, what it does with the unexpected, and how it shuts down.</summary>
public class AcpClientEdgeTests
{
    [Fact]
    public async Task The_client_offers_only_the_capabilities_it_can_answer()
    {
        await using var bare = Harness.Start();
        await (await bare.NewSessionAsync()).PromptAsync("caps");
        Assert.Equal(["{}"], bare.Chunks);

        await using var full = Harness.Start(_ => new AcpClientOptions
        {
            OnReadTextFile = (_, _) => ValueTask.FromResult(new ReadTextFileResponse("")),
            Terminal = new EchoTerminal(),
        });
        await (await full.NewSessionAsync()).PromptAsync("caps");
        using var caps = JsonDocument.Parse(Assert.Single(full.Chunks));
        Assert.True(caps.RootElement.GetProperty("terminal").GetBoolean());
        Assert.True(caps.RootElement.GetProperty("fs").GetProperty("readTextFile").GetBoolean());
        Assert.False(caps.RootElement.GetProperty("fs").GetProperty("writeTextFile").GetBoolean());
    }

    [Fact]
    public async Task File_reads_are_declined_unless_a_callback_is_given()
    {
        await using var declined = Harness.Start();
        await (await declined.NewSessionAsync()).PromptAsync("fs notes.txt");
        Assert.Equal(["error:-32601"], declined.Chunks);

        await using var allowed = Harness.Start(_ => new AcpClientOptions
        {
            OnReadTextFile = (request, _) => ValueTask.FromResult(new ReadTextFileResponse($"contents of {request.Path}")),
        });
        await (await allowed.NewSessionAsync()).PromptAsync("fs notes.txt");
        Assert.Equal(["file:contents of notes.txt"], allowed.Chunks);
    }

    [Fact]
    public async Task Terminal_methods_go_to_the_handler()
    {
        await using var h = Harness.Start(_ => new AcpClientOptions { Terminal = new EchoTerminal() });

        await (await h.NewSessionAsync()).PromptAsync("terminal");

        Assert.Equal(["terminal:hi"], h.Chunks);
    }

    [Fact]
    public async Task Tool_calls_carry_their_locations_and_content()
    {
        await using var h = Harness.Start();

        await (await h.NewSessionAsync()).PromptAsync("tool");

        var call = h.Updates.Select(n => n.Update).OfType<ToolCall>().Single();
        Assert.Equal(("t1", AcpToolKind.Read, AcpToolStatus.InProgress), (call.ToolCallId, call.Kind, call.Status));
        Assert.Equal(new ToolCallLocation("src/a.cs", 12), Assert.Single(call.Locations!));
        Assert.Equal("src/a.cs", call.RawInput?.GetProperty("path").GetString());
        var update = h.Updates.Select(n => n.Update).OfType<ToolCallUpdate>().Single();
        Assert.Equal(AcpToolStatus.Completed, update.Status);
        Assert.Equal("class A {}", Assert.Single(update.Content!).Content?.Text);
    }

    [Fact]
    public async Task Usage_comes_from_the_update_and_from_the_turn_result()
    {
        await using var h = Harness.Start();

        var result = await (await h.NewSessionAsync()).PromptAsync("usage");

        var usage = h.Updates.Select(n => n.Update).OfType<UsageUpdate>().Single();
        Assert.Equal((100L, 1000L), (usage.Used, usage.Size));
        Assert.Equal(new Cost(0.01m, "USD"), usage.Cost);
        Assert.Equal(new TokenUsage(10, 5, 15), result.Usage);
    }

    [Fact]
    public async Task A_turn_without_usage_has_none()
    {
        await using var h = Harness.Start();

        var result = await (await h.NewSessionAsync()).PromptAsync("say hello");

        Assert.Null(result.Usage);
        Assert.DoesNotContain(h.Updates, n => n.Update is UsageUpdate);
    }

    [Fact]
    public async Task An_update_of_an_unknown_kind_is_passed_on_whole()
    {
        await using var h = Harness.Start();

        await (await h.NewSessionAsync()).PromptAsync("unknown");

        var unknown = h.Updates.Select(n => n.Update).OfType<UnknownSessionUpdate>().Single();
        Assert.Equal("future_thing", unknown.Kind);
        Assert.Equal(1, unknown.Raw.GetProperty("x").GetInt32());
        Assert.Equal(["after"], h.Chunks);
    }

    [Fact]
    public async Task Text_on_standard_output_that_is_not_json_is_ignored_but_tapped()
    {
        await using var h = Harness.Start();

        await (await h.NewSessionAsync()).PromptAsync("noise");

        Assert.Equal(["ok"], h.Chunks);
        Assert.Contains(h.Wire, w => w is { Direction: AcpWireDirection.FromAgent, Line: "Loaded cached credentials." });
        Assert.Contains(h.Wire, w => w.Direction == AcpWireDirection.ToAgent && w.Line.Contains("\"method\":\"session/prompt\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Loading_a_session_replays_its_history_as_updates()
    {
        await using var h = Harness.Start();
        await h.Client.InitializeAsync();

        var session = await h.Client.LoadSessionAsync("sess-old", Path.GetTempPath());

        Assert.Equal("sess-old", session.Id);
        Assert.Equal("earlier question", h.Updates.Select(n => n.Update).OfType<UserMessageChunk>().Single().Content.Text);
        Assert.Equal(["earlier answer"], h.Chunks);
        Assert.Equal("default", session.Modes?.CurrentModeId);
    }

    [Fact]
    public async Task New_sessions_report_modes_models_and_config_options_that_can_be_changed()
    {
        await using var h = Harness.Start();
        var session = await h.NewSessionAsync();
        Assert.Equal(["default", "plan"], session.Modes!.AvailableModes!.Select(m => m.Id));
        Assert.Equal("fast", session.Models!.CurrentModelId);
        var model = Assert.Single(session.ConfigOptions);
        Assert.Equal(["fast", "deep"], model.Values());

        var after = await session.SetConfigOptionAsync("model", "deep");
        await session.SetModeAsync("plan");
        await session.SetModelAsync("deep");

        Assert.Equal("deep", Assert.Single(after).CurrentValue?.GetString());
        Assert.Equal("plan", session.Modes.CurrentModeId);
        Assert.Equal("deep", session.Models.CurrentModelId);
    }

    [Fact]
    public async Task An_error_from_the_agent_carries_its_code()
    {
        await using var h = Harness.Start();
        var session = await h.NewSessionAsync();

        var error = await Assert.ThrowsAsync<AcpRequestException>(() => session.SetModeAsync("bad"));

        Assert.Equal((-32602, "session/set_mode"), (error.Code, error.Method));
        Assert.Contains("unknown mode", error.Message, StringComparison.Ordinal);
        Assert.Equal(AcpStopReason.EndTurn, (await session.PromptAsync("say ok")).StopReason);
    }

    [Fact]
    public async Task Authenticate_sends_the_method_id()
    {
        await using var h = Harness.Start();
        await h.Client.InitializeAsync();

        await h.Client.AuthenticateAsync("fake-key");

        Assert.Contains(h.Wire, w => w.Direction == AcpWireDirection.ToAgent && w.Line.Contains("\"methodId\":\"fake-key\"", StringComparison.Ordinal));
    }

    [Fact]
    public void A_program_that_cannot_start_is_a_connection_error()
    {
        var error = Assert.Throws<AcpConnectionException>(() =>
            AcpClient.Launch(new AcpLaunchOptions { FileName = "/nonexistent/acp-agent" }, new AcpClientOptions()));

        Assert.Contains("cannot start", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disposing_stops_an_agent_that_is_busy_and_fails_the_turn()
    {
        var h = Harness.Start();
        var session = await h.NewSessionAsync();
        var turn = session.PromptAsync("slow");
        await h.WaitForUpdateAsync(u => u is AgentMessageChunk);

        await h.DisposeAsync().AsTask().WaitAsync(Harness.Deadline);

        await Assert.ThrowsAsync<AcpConnectionException>(() => turn.WaitAsync(Harness.Deadline));
    }

    private sealed class EchoTerminal : IAcpTerminalHandler
    {
        public ValueTask<CreateTerminalResponse> CreateAsync(CreateTerminalRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CreateTerminalResponse("term-1"));

        public ValueTask<TerminalOutputResponse> OutputAsync(TerminalRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new TerminalOutputResponse("hi", false, new TerminalExitStatus(0)));

        public ValueTask<TerminalExitStatus> WaitForExitAsync(TerminalRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new TerminalExitStatus(0));

        public ValueTask KillAsync(TerminalRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask ReleaseAsync(TerminalRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
