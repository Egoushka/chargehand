using System.Text.Json;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace Chargehand.Tests;

/// <summary>Cycle A Task 6: docs/guide/clients.md gives each client a configuration it can read, says whether the snippet was run,
/// and carries placeholders only (the repository is public).</summary>
public partial class ClientsGuideTests
{
    private static readonly string[] Clients =
    [
        "Claude Code", "Claude Desktop", "Cursor", "VS Code", "Visual Studio", "JetBrains AI Assistant", "Junie",
        "Codex", "Gemini CLI", "Warp", "Zed", "Android Studio", "ChatGPT",
    ];

    private static readonly string[] Lines = File.ReadAllLines(Repo.Path("docs", "guide", "clients.md"));

    private sealed record Block(string Section, string Language, string Body, string Label, int Line);

    private static readonly List<Block> Blocks = ReadBlocks();

    private static List<Block> ReadBlocks()
    {
        var blocks = new List<Block>();
        var section = "";
        for (var i = 0; i < Lines.Length; i++)
        {
            if (Lines[i].StartsWith("## ", StringComparison.Ordinal))
                section = Lines[i][3..].Trim();
            if (!Lines[i].StartsWith("```", StringComparison.Ordinal))
                continue;
            var language = Lines[i][3..].Trim();
            var label = "";
            for (var back = i - 1; back >= 0 && label.Length == 0; back--)
                label = Lines[back].Trim();
            var start = i + 1;
            for (i = start; i < Lines.Length && !Lines[i].StartsWith("```", StringComparison.Ordinal); i++) { }
            blocks.Add(new Block(section, language, string.Join('\n', Lines[start..i]), label, start));
        }
        return blocks;
    }

    private static IEnumerable<Block> InClientSections => Blocks.Where(b => Clients.Contains(b.Section));

    [Fact]
    public void The_guide_has_a_section_for_each_client()
    {
        var headings = Lines.Where(l => l.StartsWith("## ", StringComparison.Ordinal)).Select(l => l[3..].Trim()).ToList();
        Assert.Empty(Clients.Except(headings));
    }

    [Fact]
    public void The_front_matter_places_the_guide_in_the_guides_section()
    {
        Assert.Equal("---", Lines[0]);
        Assert.Contains(Lines, l => l == "section: \"Guides\"");
        Assert.Contains(Lines, l => l.StartsWith("title: \"", StringComparison.Ordinal));
        Assert.Contains(Lines, l => l.StartsWith("description: \"", StringComparison.Ordinal));
    }

    [Fact]
    public void Index_and_the_mcp_page_link_to_the_guide()
    {
        Assert.Contains("(clients.md)", File.ReadAllText(Repo.Path("docs", "guide", "index.md")));
        Assert.Contains("(clients.md", File.ReadAllText(Repo.Path("docs", "guide", "mcp.md")));
    }

    [Fact]
    public void Every_json_snippet_parses()
    {
        var json = Blocks.Where(b => b.Language == "json").ToList();
        Assert.NotEmpty(json);
        foreach (var block in json)
        {
            var error = Record.Exception(() => JsonDocument.Parse(block.Body));
            Assert.True(error is null, $"clients.md line {block.Line}: {error?.Message}");
        }
    }

    [Fact]
    public void Every_toml_snippet_parses()
    {
        var toml = Blocks.Where(b => b.Language == "toml").ToList();
        Assert.NotEmpty(toml);
        foreach (var block in toml)
        {
            var error = Record.Exception(() => TomlSerializer.Deserialize<TomlTable>(block.Body));
            Assert.True(error is null, $"clients.md line {block.Line}: {error?.Message}");
        }
    }

    [Fact]
    public void Every_snippet_in_a_client_section_says_whether_it_was_run()
    {
        // The label is the nearest non-blank line above the fence: **Run** (the client was started and a chargehand tool called)
        // or **Vendor docs** (taken from the vendor's page).
        var inClients = InClientSections.ToList();
        Assert.NotEmpty(inClients);
        foreach (var block in inClients)
            Assert.True(StatusLabel().IsMatch(block.Label), $"clients.md line {block.Line}: no **Run** or **Vendor docs** label above the snippet: '{block.Label}'");
    }

    [Fact]
    public void Every_client_section_carries_a_status_label()
    {
        foreach (var client in Clients)
        {
            var from = Array.FindIndex(Lines, l => l == $"## {client}");
            var to = Array.FindIndex(Lines, from + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
            var section = string.Join('\n', Lines[from..(to < 0 ? Lines.Length : to)]);
            Assert.True(StatusLabel().IsMatch(section), $"clients.md: section '{client}' has no **Run** or **Vendor docs** label");
        }
    }

    [Fact]
    public void Json_snippets_that_define_a_server_reach_the_mcp_endpoint_or_the_packed_tool()
    {
        var servers = Blocks.Where(b => b.Language == "json").Select(b => (Block: b, Doc: JsonDocument.Parse(b.Body).RootElement))
            .Where(x => x.Doc.ValueKind == JsonValueKind.Object
                && (x.Doc.TryGetProperty("chargehand", out _)
                    || x.Doc.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("chargehand", out _))))
            .ToList();
        Assert.NotEmpty(servers);
        foreach (var (block, doc) in servers)
        {
            var strings = Strings(doc).ToList();
            var http = strings.Any(s => s.EndsWith("/v1/mcp", StringComparison.Ordinal) || s.Contains("/v1/mcp ", StringComparison.Ordinal));
            var stdio = strings.Any(s => s.StartsWith("Chargehand@", StringComparison.Ordinal)) && strings.Contains("mcp");
            Assert.True(http || stdio, $"clients.md line {block.Line}: neither the /v1/mcp endpoint nor Chargehand@<version> ... mcp");
        }
    }

    [Fact]
    public void The_guide_uses_placeholders_only()
    {
        var text = string.Join('\n', Lines);
        Assert.DoesNotMatch(@"\b\d{1,3}(\.\d{1,3}){3}\b", text);
        Assert.DoesNotMatch(@"(?i)/(users|home)/\w", text);
        Assert.DoesNotContain(".ts.net", text);
        Assert.DoesNotMatch(@"chk1\.[0-9a-z]", text);
        foreach (Match m in Regex.Matches(text, @"Bearer\s+(\S+)"))
            Assert.Matches(@"^(<your-key>|\$\{?[A-Z_]+\}?|\$\{env:[A-Z_]+\}|\$\{input:[a-z-]+\})", m.Groups[1].Value);
    }

    [GeneratedRegex(@"\*\*(Run|Vendor docs)\b")]
    private static partial Regex StatusLabel();

    private static IEnumerable<string> Strings(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                yield return e.GetString()!;
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                    foreach (var s in Strings(item))
                        yield return s;
                break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                    foreach (var s in Strings(p.Value))
                        yield return s;
                break;
        }
    }
}
