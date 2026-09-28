using System.Text.Json;
using System.Xml.Linq;

namespace Chargehand.Tests;

/// <summary>Goal 0.5: the plugin carries the one version (Directory.Build.props) and starts the packed tool.</summary>
public class PluginManifestTests
{
    private static readonly string Version = XDocument.Load(Repo.Path("Directory.Build.props")).Descendants("Version").Single().Value;

    private static JsonElement Json(params string[] path) => JsonDocument.Parse(File.ReadAllText(Repo.Path(path))).RootElement;

    [Fact]
    public void Plugin_version_is_the_repository_version()
    {
        var plugin = Json("plugins", "chargehand", ".claude-plugin", "plugin.json");
        Assert.Equal("chargehand", plugin.GetProperty("name").GetString());
        Assert.Equal(Version, plugin.GetProperty("version").GetString());
    }

    [Fact]
    public void Mcp_entry_runs_the_packed_tool_at_the_repository_version()
    {
        var server = Json("plugins", "chargehand", ".mcp.json").GetProperty("mcpServers").GetProperty("chargehand");
        var args = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToList();
        var packageId = XDocument.Load(Repo.Path("src", "Chargehand.Cli", "Chargehand.Cli.csproj")).Descendants("PackageId").Single().Value;
        Assert.Equal("dotnet", server.GetProperty("command").GetString());
        Assert.Equal(["dnx", $"{packageId}@{Version}", "--yes", "--", "mcp"], args);
    }

    [Fact]
    public void Marketplace_lists_the_plugin_directory()
    {
        var entry = Json(".claude-plugin", "marketplace.json").GetProperty("plugins")[0];
        Assert.Equal("chargehand", entry.GetProperty("name").GetString());
        Assert.Equal("./plugins/chargehand", entry.GetProperty("source").GetString());
    }
}
