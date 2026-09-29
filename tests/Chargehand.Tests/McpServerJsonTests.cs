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

    /// <summary>
    /// The registry's GitHub OIDC login grants io.github.OWNER/* and compares it with the server name as
    /// written, case included, so `io.github.egoushka/...` would be refused for the owner Egoushka (ADR 0027, note of
    /// 2026-09-29). The README line is matched the same way.
    /// </summary>
    [Fact]
    public void Name_is_in_the_repository_owners_namespace_spelled_as_GitHub_spells_it()
    {
        var csproj = XDocument.Load(Repo.Path("src", "Chargehand.Cli", "Chargehand.Cli.csproj"));
        var repository = Server.GetProperty("repository");
        Assert.Equal("github", repository.GetProperty("source").GetString());
        Assert.Equal(csproj.Descendants("RepositoryUrl").Single().Value, repository.GetProperty("url").GetString());
        var owner = new Uri(repository.GetProperty("url").GetString()!).Segments[1].TrimEnd('/');
        Assert.StartsWith($"io.github.{owner}/", Server.GetProperty("name").GetString(), StringComparison.Ordinal);
    }

    /// <summary>The release job picks the nuget package out of packages and the registry accepts nuget.org only.</summary>
    [Fact]
    public void Lists_one_package_from_nuget_org_that_dnx_starts_over_stdio()
    {
        var package = Assert.Single(Server.GetProperty("packages").EnumerateArray());
        Assert.Equal("nuget", package.GetProperty("registryType").GetString());
        Assert.Equal("https://api.nuget.org/v3/index.json", package.GetProperty("registryBaseUrl").GetString());
        Assert.Equal("dnx", package.GetProperty("runtimeHint").GetString());
        Assert.Equal("stdio", package.GetProperty("transport").GetProperty("type").GetString());
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
