using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Memory.Api;

namespace Chargehand.Modules.Memory.Tests;

/// <summary>An <c>mcp_servers</c> entry of the profile (ADR 0034, ADR 0060): bound by the Memory module's context and validated there.</summary>
public class McpServerSettingsTests
{
    private static McpServerSettings Read(string json) =>
        JsonSerializer.Deserialize(json, MemorySettingsJsonContext.Default.McpServerSettings)!;

    [Fact]
    public void An_entry_reads_its_snake_case_keys_and_ignores_unknown_ones()
    {
        var http = Read("{\"url\":\"https://h.test/mcp\",\"headers\":{\"Authorization\":\"Bearer {secret:tok}\"},\"transport\":\"sse\",\"extra\":1}");
        var stdio = Read("// argv\n{\"command\":[\"npx\",\"srv\"],\"env\":{\"K\":\"{secret:k}\"}}");

        Assert.Equal(McpServerSettings.TransportSse, http.Transport);
        Assert.Equal("Bearer {secret:tok}", http.Headers!["Authorization"]);
        Assert.Equal(["npx", "srv"], stdio.Command);
        Assert.Null(stdio.Url);
        http.Validate("notes");
        stdio.Validate("local");
    }

    [Theory]
    [InlineData("notes", true)]
    [InlineData("a1-b", true)]
    [InlineData("Notes", false)]
    [InlineData("1a", false)]
    [InlineData("a_b", false)]
    [InlineData("", false)]
    public void A_server_name_is_lowercase_letters_digits_and_hyphens_starting_with_a_letter(string name, bool valid) =>
        Assert.Equal(valid, McpServerSettings.ValidName(name));

    [Theory]
    [InlineData("{\"url\":\"http://a.test\",\"command\":[\"x\"]}", "mcp_servers.s: set only one of url and command")]
    [InlineData("{}", "mcp_servers.s: set one of url and command")]
    [InlineData("{\"url\":\"ftp://a.test\"}", "mcp_servers.s: url must be an absolute http or https URL")]
    [InlineData("{\"url\":\"http://a.test/{secret:k}\"}", "mcp_servers.s: url must not hold {secret:...}; put the credential in a header")]
    [InlineData("{\"url\":\"http://a.test\",\"env\":{\"A\":\"b\"}}", "mcp_servers.s: env applies to a command server only")]
    [InlineData("{\"url\":\"http://a.test\",\"transport\":\"ws\"}", "mcp_servers.s: transport must be auto, streamable-http or sse")]
    [InlineData("{\"command\":[\"x\",\"{secret:k}\"]}", "mcp_servers.s: command must not hold {secret:...}; put the credential in env")]
    [InlineData("{\"command\":[\"x\"],\"headers\":{\"A\":\"b\"}}", "mcp_servers.s: headers apply to a url server only")]
    [InlineData("{\"command\":[\"x\"],\"transport\":\"sse\"}", "mcp_servers.s: transport applies to a url server only")]
    public void An_entry_that_cannot_work_is_refused_naming_the_server_and_never_quoting_its_values(string json, string message)
    {
        var refused = Assert.Throws<ChargehandException>(() => Read(json).Validate("s"));

        Assert.Equal((ErrorCode.InvalidRequest, message), (refused.Code, refused.Message));
        Assert.Equal("Fix the entry in the profile; profiles/example.json shows the shape.", refused.Action);
    }
}
