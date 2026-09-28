using System.Text.Json;
using System.Xml.Linq;
using Json.Schema;

namespace Chargehand.Tests;

/// <summary>ADR 0027: .mcp/server.json is the registry entry and ships inside the dnx package.</summary>
public class McpServerJsonTests
{
    private static readonly JsonElement Server = JsonDocument.Parse(File.ReadAllText(Repo.Path(".mcp", "server.json"))).RootElement;

    [Fact]
    public void Validates_against_the_pinned_registry_schema()
    {
        var schema = JsonSchema.FromText(File.ReadAllText(Repo.Path("docs", "mcp-server-2025-12-11.schema.json")));
        var result = schema.Evaluate(Server, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, string.Join("; ", (result.Details ?? []).Where(d => d.Errors is not null).SelectMany(d => d.Errors!.Values)));
        Assert.Equal("https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json", Server.GetProperty("$schema").GetString());
    }

    /// <summary>Directory.Build.props is the one version source: pack stamps it over the 0.0.0 placeholders.</summary>
    [Fact]
    public void Carries_the_version_placeholder_the_pack_stamps()
    {
        Assert.Equal("0.0.0", Server.GetProperty("version").GetString());
        Assert.All(Server.GetProperty("packages").EnumerateArray(), p => Assert.Equal("0.0.0", p.GetProperty("version").GetString()));
    }

    [Fact]
    public void Names_the_packed_tool_and_the_readme_ownership_line()
    {
        var csproj = XDocument.Load(Repo.Path("src", "Chargehand.Cli", "Chargehand.Cli.csproj"));
        var package = Server.GetProperty("packages")[0];
        Assert.Equal(csproj.Descendants("PackageId").Single().Value, package.GetProperty("identifier").GetString());
        Assert.Equal("mcp", package.GetProperty("packageArguments")[0].GetProperty("value").GetString());
        Assert.Contains($"<!-- mcp-name: {Server.GetProperty("name").GetString()} -->",
            File.ReadAllText(Repo.Path("src", "Chargehand.Cli", "README.package.md")), StringComparison.Ordinal);
    }
}
