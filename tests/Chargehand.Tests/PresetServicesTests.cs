using Chargehand.Config;

namespace Chargehand.Tests;

public class PresetServicesTests
{
    private const string Docs = "    services:\n      - server: team-docs\n        tools: [search_docs, read_doc]\n";

    [Fact]
    public void A_node_kind_may_list_services_by_server_and_tool()
    {
        var services = PresetRoot.Load("docs", Docs).NodeKinds["worker"].Services!;

        Assert.Equal([new ServiceUse("team-docs", ["search_docs", "read_doc"])], services);
    }

    [Theory]
    [InlineData("    services:\n      - server: team-docs\n        tools: ['*']\n")]     // no whole-server grant
    [InlineData("    services:\n      - server: team-docs\n        tools: ['**']\n")]    // nor a spelling of one
    [InlineData("    services:\n      - server: team-docs\n        tools: []\n")]        // at least one tool
    [InlineData("    services:\n      - tools: [search_docs]\n")]                        // a server is required
    [InlineData("    services:\n      - server: Team_Docs\n        tools: [a]\n")]       // a name mcp_servers can hold
    [InlineData("    services:\n      - server: team-docs\n        tools: [a]\n        writes: true\n")] // no other keys
    public void An_invalid_services_block_fails_the_schema(string services) =>
        Assert.Throws<InvalidDataException>(() => PresetRoot.Load("docs", services));

    [Fact]
    public void A_preset_without_services_has_none() => Assert.Null(Preset.Load(Repo.Path("presets"), "cheap").NodeKinds["worker"].Services);
}
