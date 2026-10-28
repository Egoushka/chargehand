using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Runtimes.Acp.Api;

namespace Chargehand.Runtimes.Acp.Tests;

public class AgentCatalogTests
{
    private const string One = """
        {
          "schema": "acp-agents/v1",
          "agents": [
            { "id": "codex", "name": "Codex", "command": "npx", "args": ["-y", "@scope/codex-acp@2.3.0"], "version": "2.3.0",
              "api_key_env": "OPENAI_API_KEY", "base_url_env": "OPENAI_BASE_URL", "auth_method": "openai-api-key", "read_only_mode": "read-only",
              "reports_usage": true, "env": { "NO_COLOR": "1" } }
          ]
        }
        """;

    [Fact]
    public void An_agent_is_read_with_everything_the_runtime_needs()
    {
        var agent = AcpAgentCatalog.Parse(One, "test").Get("codex");

        Assert.Equal("Codex", agent.Name);
        Assert.Equal("npx", agent.Command);
        Assert.Equal(["-y", "@scope/codex-acp@2.3.0"], agent.Args);
        Assert.Equal("2.3.0", agent.Version);
        Assert.Equal("OPENAI_API_KEY", agent.ApiKeyEnv);
        Assert.Equal("openai-api-key", agent.AuthMethod);
        Assert.Equal("read-only", agent.ReadOnlyMode);
        Assert.True(agent.ReportsUsage);
    }

    [Fact]
    public void An_agent_that_is_not_pinned_is_refused_with_the_pinned_ones_named()
    {
        var e = Assert.Throws<ChargehandException>(() => AcpAgentCatalog.Parse(One, "test").Get("copilot"));

        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.Contains("codex", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "schema": "acp-agents/v2", "agents": [] }""", "schema")]
    [InlineData("""{ "schema": "acp-agents/v1", "agents": [ { "id": "a", "command": "x", "version": "1", "api_key_env": "K" }, { "id": "a", "command": "x", "version": "1", "api_key_env": "K" } ] }""", "twice")]
    [InlineData("""{ "schema": "acp-agents/v1", "agents": [ { "id": "a", "command": "x", "version": "1" } ] }""", "api_key_env")]
    [InlineData("""{ "schema": "acp-agents/v1", "agents": [ { "id": "a", "command": "x", "api_key_env": "K" } ] }""", "version")]
    [InlineData("""{ "schema": "acp-agents/v1", "agents": [ { "id": "a", "command": "x", "version": "1", "api_key_env": "K", "sha256": "abc" } ] }""", "sha256")]
    [InlineData("not json", "acp-agents.json")]
    public void A_pinned_list_that_is_wrong_is_refused_naming_the_problem(string json, string mentions)
    {
        var e = Assert.Throws<ChargehandException>(() => AcpAgentCatalog.Parse(json, "acp-agents.json"));

        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.Contains(mentions, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pinned_list_of_this_repository_is_valid_and_its_packages_carry_the_pinned_version()
    {
        var catalog = AcpAgentCatalog.Load(Path.Combine(Root(), AcpAgentCatalog.FileName));

        Assert.Contains(catalog.Agents, a => a.Id == "codex");
        Assert.Contains(catalog.Agents, a => a.Id == "gemini");
        foreach (var agent in catalog.Agents)
        {
            Assert.Contains(agent.Args, a => a.EndsWith("@" + agent.Version, StringComparison.Ordinal));
            Assert.False(string.IsNullOrWhiteSpace(agent.ApiKeyEnv));
        }
    }

    [Theory]
    [InlineData("codex", "api-key")]
    [InlineData("gemini", "gemini-api-key")]
    public void A_pinned_agent_signs_in_with_its_api_key_method_never_a_login(string id, string method)
    {
        var agent = AcpAgentCatalog.Load(Path.Combine(Root(), AcpAgentCatalog.FileName)).Get(id);

        // Probed on 2026-10-28 with an empty home: Codex 2.3.0 offers api-key and chat-gpt; Gemini CLI 0.66.0 offers oauth-personal, gemini-api-key, vertex-ai and gateway.
        Assert.Equal(method, agent.AuthMethod);
        Assert.DoesNotContain("oauth", agent.AuthMethod, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chat-gpt", agent.AuthMethod, StringComparison.OrdinalIgnoreCase);
    }

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Chargehand.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Chargehand.slnx not found above the test output.");
    }
}
