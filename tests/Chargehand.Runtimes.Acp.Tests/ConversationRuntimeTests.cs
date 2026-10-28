using Chargehand.Runtimes.Acp.Api;

namespace Chargehand.Runtimes.Acp.Tests;

public class ConversationRuntimeTests
{
    private const string Pinned = """
        { "schema": "acp-agents/v1", "agents": [
          { "id": "codex", "command": "npx", "version": "2.3.0", "api_key_env": "OPENAI_API_KEY" },
          { "id": "gemini", "command": "npx", "version": "0.66.0", "api_key_env": "GEMINI_API_KEY" } ] }
        """;

    private static AcpConversationRuntime Runtime(AcpSettings settings) =>
        new(AcpAgentCatalog.Parse(Pinned, "test"), settings, _ => "k", (_, _, _) => Task.FromResult("/work"), new NoLauncher());

    private static AcpSettings Keyed(params string[] agents) =>
        new(Agents: agents.ToDictionary(a => a, a => new AcpAgentCredential(ApiKeySecret: $"{a}-key")));

    [Fact]
    public void An_agent_is_supported_when_it_is_pinned_and_the_profile_gave_it_a_key()
    {
        var runtime = Runtime(Keyed("codex"));

        Assert.True(runtime.Supports("acp:codex"));
        Assert.False(runtime.Supports("acp:gemini"));
        Assert.False(runtime.Supports("acp:copilot"));
        Assert.False(runtime.Supports("codex"));
        Assert.False(runtime.Supports("fake"));
    }

    [Fact]
    public void An_agent_with_a_blank_key_name_is_not_supported()
    {
        Assert.False(Runtime(new AcpSettings(Agents: new Dictionary<string, AcpAgentCredential> { ["codex"] = new(ApiKeySecret: " ") })).Supports("acp:codex"));
    }

    [Fact]
    public void The_credential_a_pause_is_kept_on_is_the_runtime_so_one_agents_rejected_key_does_not_pause_the_other()
    {
        var runtime = Runtime(Keyed("codex", "gemini"));

        Assert.Equal("acp:codex", runtime.CredentialKey("acp:codex"));
        Assert.NotEqual(runtime.CredentialKey("acp:codex"), runtime.CredentialKey("acp:gemini"));
    }

    private sealed class NoLauncher : IAcpLauncher
    {
        public Task<IAcpAgent> StartAsync(AcpAgentSpec agent, IReadOnlyDictionary<string, string> environment, string directory, string? model, AcpAgentCallbacks callbacks, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
