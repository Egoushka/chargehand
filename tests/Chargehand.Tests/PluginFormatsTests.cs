using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chargehand.Tests;

/// <summary>Cycle A Task 6: plugins/chargehand is one directory that Claude Code and VS Code agent plugins both install.
/// VS Code reads the Claude layout (.claude-plugin/plugin.json, .mcp.json, skills/); these checks restate the rules its docs give
/// for that layout (code.visualstudio.com/docs/agents/reference/agent-plugin-reference, .../agent-customization/agent-skills).</summary>
public partial class PluginFormatsTests
{
    private static readonly string PluginDir = Repo.Path("plugins", "chargehand");

    private static JsonElement Json(params string[] path) => JsonDocument.Parse(File.ReadAllText(Repo.Path(path))).RootElement;

    [Fact]
    public void Claude_manifest_is_in_the_claude_location_with_a_name_and_a_semver()
    {
        var plugin = Json("plugins", "chargehand", ".claude-plugin", "plugin.json");
        Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", plugin.GetProperty("name").GetString());
        Assert.Matches(@"^\d+\.\d+\.\d+$", plugin.GetProperty("version").GetString());
        Assert.False(string.IsNullOrWhiteSpace(plugin.GetProperty("description").GetString()));
    }

    [Fact]
    public void Plugin_name_satisfies_the_agent_plugins_name_rules()
    {
        // 1 to 64 characters of a-z, 0-9, '-' and '.'; alphanumeric first and last; no '--' and no '..'.
        var name = Json("plugins", "chargehand", ".claude-plugin", "plugin.json").GetProperty("name").GetString()!;
        Assert.InRange(name.Length, 1, 64);
        Assert.Matches("^[a-z0-9][a-z0-9.-]*[a-z0-9]$|^[a-z0-9]$", name);
        Assert.DoesNotContain("--", name);
        Assert.DoesNotContain("..", name);
    }

    [Fact]
    public void A_root_manifest_does_not_switch_vs_code_to_the_agent_plugins_1_0_layout()
    {
        // A root plugin.json declaring the Agent Plugins $schema makes VS Code read mcp.json and ignore the Claude layout's .mcp.json.
        Assert.False(File.Exists(Path.Combine(PluginDir, "plugin.json")));
        Assert.False(File.Exists(Path.Combine(PluginDir, "mcp.json")));
    }

    [Fact]
    public void Each_skill_directory_holds_a_skill_named_like_the_directory()
    {
        // VS Code drops a skill whose frontmatter name differs from its directory, has a namespace prefix or breaks [a-z0-9-]{1,64};
        // the plugin name is the prefix it adds itself.
        var skills = Directory.GetDirectories(Path.Combine(PluginDir, "skills"));
        Assert.NotEmpty(skills);
        foreach (var dir in skills)
        {
            var folder = Path.GetFileName(dir);
            var front = Frontmatter(File.ReadAllText(Path.Combine(dir, "SKILL.md")));
            Assert.Equal(folder, front["name"]);
            Assert.Matches("^[a-z0-9-]{1,64}$", front["name"]);
            Assert.InRange(front["description"].Length, 1, 1024);
        }
    }

    [Fact]
    public void Mcp_servers_are_stdio_entries_with_string_arguments_and_no_embedded_secrets()
    {
        var servers = Json("plugins", "chargehand", ".mcp.json").GetProperty("mcpServers");
        Assert.NotEmpty(servers.EnumerateObject());
        foreach (var server in servers.EnumerateObject())
        {
            Assert.Equal(JsonValueKind.String, server.Value.GetProperty("command").ValueKind);
            Assert.All(server.Value.GetProperty("args").EnumerateArray(), a => Assert.Equal(JsonValueKind.String, a.ValueKind));
            foreach (var env in server.Value.GetProperty("env").EnumerateObject())
            {
                Assert.Equal(JsonValueKind.String, env.Value.ValueKind);
                Assert.DoesNotMatch("(?i)token|secret|key|password", env.Name);
            }
        }
    }

    [Fact]
    public void Marketplace_entry_resolves_to_the_plugin_directory()
    {
        // chat.plugins.marketplaces takes owner/repo and VS Code reads the Claude marketplace file in it.
        var entry = Json(".claude-plugin", "marketplace.json").GetProperty("plugins")[0];
        Assert.True(Directory.Exists(Path.GetFullPath(entry.GetProperty("source").GetString()!, Repo.Root)));
        Assert.Equal(Json("plugins", "chargehand", ".claude-plugin", "plugin.json").GetProperty("name").GetString(), entry.GetProperty("name").GetString());
    }

    private static Dictionary<string, string> Frontmatter(string text)
    {
        var lines = text.Split('\n');
        Assert.Equal("---", lines[0].TrimEnd());
        var map = new Dictionary<string, string>();
        foreach (var line in lines.Skip(1).TakeWhile(l => l.TrimEnd() != "---"))
        {
            var m = FrontmatterLine().Match(line);
            if (m.Success)
                map[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        }
        return map;
    }

    [GeneratedRegex(@"^([a-z-]+):\s*(.*)$")]
    private static partial Regex FrontmatterLine();
}
