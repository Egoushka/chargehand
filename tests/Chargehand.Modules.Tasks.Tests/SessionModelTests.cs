using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Tasks.Tests;

/// <summary>A driven session names the model its preset resolves to, so the Claude Code CLI never picks its own default (ADR 0095).</summary>
public class SessionModelTests
{
    [Theory]
    [InlineData("anthropic/claude-sonnet-5", "claude-sonnet-5")]
    [InlineData("claude-sonnet-5", "claude-sonnet-5")]
    [InlineData("provider/worker-model", null)]
    [InlineData("", null)]
    public void The_session_model_is_the_gateway_id_without_its_provider_and_none_for_an_unresolved_placeholder(string model, string? expected) =>
        Assert.Equal(expected, TaskRunner.SessionModel(model));
}
