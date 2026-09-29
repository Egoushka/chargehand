using Chargehand.Config;
using Chargehand.Mcp;
using Chargehand.Memory;
using static Chargehand.Tests.MemoryConfigTests;

namespace Chargehand.Tests;

/// <summary>Goal 0.6: the profile's memory becomes the stack a run recalls from, the list through MCP and the object form as before.</summary>
public class MemoryStacksTests
{
    private static Profile ProfileWith(string memory, string servers = """{"gw":{"url":"https://mcp.example.internal/mcp"}}""")
    {
        using var dir = new TempDir();
        return Profile.Load(dir.Write("p.json", $$"""{"schema":"profile/v1","mcp_servers":{{servers}},"memory":{{memory}}}"""));
    }

    private static McpConnectionPool NeverConnects(Profile profile) =>
        new(profile.McpServers ?? new Dictionary<string, McpServerSettings>(), _ => "", (_, _, _) => throw new InvalidOperationException("not connected in this test"));

    private static readonly Func<MemorySettings, IMemoryProvider> NoObjectForm = _ => throw new InvalidOperationException("the object form is not expected here");

    [Fact]
    public async Task The_list_becomes_one_source_per_entry_in_order_with_the_entrys_limits()
    {
        var notes = """{"name":"notes","server":"gw","namespace":"n","max_facts":3,"max_chars":900,"max_fact_chars":200,"timeout_seconds":4,"retain_tags":["team"],"tools":{"recall":{"tool":"search_notes","arguments":{"q":"{query}"}}}}""";
        var profile = ProfileWith($"[{Hindsight},{notes}]");
        await using var pool = NeverConnects(profile);

        var stack = MemoryStacks.From(profile, pool, NoObjectForm)!;

        Assert.Equal(["hindsight", "notes"], stack.Sources.Select(s => s.Name));
        Assert.Equal([true, false], stack.Sources.Select(s => s.Retain));
        Assert.Equal(new MemoryLimits(3, 900, TimeSpan.FromSeconds(4), 200), stack.Sources[1].Limits);
        Assert.Equal(["team"], stack.Sources[1].RetainTags);
        Assert.Equal(["chargehand"], stack.Sources[0].RetainTags);
        Assert.Equal(new MemoryScope("notes", "n"), stack.Sources[1].Scope);
        Assert.All(stack.Sources, s => Assert.IsType<McpMemoryProvider>(s.Provider));
    }

    [Fact]
    public async Task A_recall_only_entry_without_a_namespace_scopes_to_its_name_and_never_retains()
    {
        var profile = ProfileWith($"[{Chronicle}]", """{"chronicle":{"url":"http://localhost:8031/sse","transport":"sse"}}""");
        await using var pool = NeverConnects(profile);

        var source = Assert.Single(MemoryStacks.From(profile, pool, NoObjectForm)!.Sources);

        Assert.Equal(("chronicle", false), (source.Name, source.Retain));
        Assert.Equal(new MemoryScope("chronicle", "chronicle"), source.Scope);
    }

    [Fact]
    public async Task No_memory_or_an_empty_list_gives_no_stack()
    {
        await using var pool = new McpConnectionPool(new Dictionary<string, McpServerSettings>(), _ => "");

        Assert.Null(MemoryStacks.From(new Profile("profile/v1"), pool, NoObjectForm));
        Assert.Null(MemoryStacks.From(ProfileWith("[]"), pool, NoObjectForm));
    }

    [Fact]
    public async Task The_object_form_still_builds_its_one_source_through_the_factory()
    {
        var profile = ProfileWith("""{"backend":"hindsight","url":"http://memory.example.internal:8888","namespace":"ns","retain":true}""");
        await using var pool = NeverConnects(profile);
        MemorySettings? given = null;
        var provider = new MemoryStackTests.Fake();

        var stack = MemoryStacks.From(profile, pool, settings =>
        {
            given = settings;
            return provider;
        })!;

        Assert.Equal("ns", given!.Namespace);
        var source = Assert.Single(stack.Sources);
        Assert.Same(provider, source.Provider);
        Assert.Equal(("hindsight", true, new MemoryScope("hindsight", "ns")), (source.Name, source.Retain, source.Scope));
    }
}
