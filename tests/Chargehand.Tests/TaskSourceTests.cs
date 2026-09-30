using System.Text.Json;
using Chargehand.Config;
using Chargehand.Driven;
using Chargehand.Mcp;

namespace Chargehand.Tests;

/// <summary>A tracker item id becomes a goal through a mapped MCP tool (ADR 0039, in the style of ADR 0034's memory mapping).</summary>
public class TaskSourceTests
{
    private static DrivenTaskSource Settings(string json) => JsonSerializer.Deserialize<DrivenTaskSource>(json, Profile.Json)!;

    private static McpConnectionPool PoolFor(FakeMcpServer server) =>
        new(new Dictionary<string, McpServerSettings> { ["tracker"] = new(Url: "https://mcp.example.internal/mcp") }, _ => "", async (_, _, _) => await server.TransportAsync());

    private const string Mapping = """{"server":"tracker","tool":"workitem","arguments":{"action":"retrieve_by_identifier","workitem_identifier":"{ref}"},"title":"name","body":"description_stripped"}""";

    [Fact]
    public async Task The_ref_is_put_in_the_mapped_call_and_title_and_body_become_the_goal()
    {
        await using var server = new FakeMcpServer(new FakeTool("workitem", _ => FakeMcpServer.Text("""{"name":"Add a retry","description_stripped":"The fetch client should retry twice."}""")));
        await using var pool = PoolFor(server);
        var goal = await new McpTaskSource(Settings(Mapping), pool).ResolveAsync("ITEM-12", default);
        Assert.Equal("Add a retry\n\nThe fetch client should retry twice.", goal);
        var (tool, args) = Assert.Single(server.Calls);
        Assert.Equal("workitem", tool);
        Assert.Equal("retrieve_by_identifier", args["action"].GetString());
        Assert.Equal("ITEM-12", args["workitem_identifier"].GetString());
    }

    [Fact]
    public async Task Paths_may_be_dotted_and_the_body_may_be_left_out()
    {
        var mapping = """{"server":"tracker","tool":"get","arguments":{"id":"{ref}"},"title":"result.issue.title"}""";
        await using var server = new FakeMcpServer(new FakeTool("get", _ => FakeMcpServer.Text("""{"result":{"issue":{"title":"Fix the flake","body":"ignored"}}}""")));
        await using var pool = PoolFor(server);
        Assert.Equal("Fix the flake", await new McpTaskSource(Settings(mapping), pool).ResolveAsync("X-1", default));
    }

    [Fact]
    public async Task A_missing_or_empty_title_a_tool_error_and_a_non_json_answer_are_task_source_failures()
    {
        foreach (var answer in new[] { FakeMcpServer.Text("""{"description_stripped":"no title"}"""), FakeMcpServer.Text("""{"name":"   "}"""), FakeMcpServer.Error("no such item"), FakeMcpServer.Text("not json") })
        {
            await using var server = new FakeMcpServer(new FakeTool("workitem", _ => answer));
            await using var pool = PoolFor(server);
            await Assert.ThrowsAsync<TaskSourceException>(() => new McpTaskSource(Settings(Mapping), pool).ResolveAsync("ITEM-1", default));
        }
    }

    [Fact]
    public async Task A_long_body_is_cut_and_the_cut_is_marked()
    {
        await using var server = new FakeMcpServer(new FakeTool("workitem", _ =>
            FakeMcpServer.Text(JsonSerializer.Serialize(new { name = "T", description_stripped = new string('x', 20_000) }))));
        await using var pool = PoolFor(server);
        var goal = await new McpTaskSource(Settings(Mapping), pool).ResolveAsync("ITEM-1", default);
        Assert.True(goal.Length < 8_200);
        Assert.EndsWith("[cut]", goal);
    }

    [Fact]
    public async Task The_ref_is_never_a_way_to_add_arguments()
    {
        await using var server = new FakeMcpServer(new FakeTool("workitem", _ => FakeMcpServer.Text("""{"name":"T"}""")));
        await using var pool = PoolFor(server);
        await new McpTaskSource(Settings(Mapping), pool).ResolveAsync("""X","extra":"1""", default);
        var args = Assert.Single(server.Calls).Arguments;
        Assert.Equal(2, args.Count);
        Assert.Equal("""X","extra":"1""", args["workitem_identifier"].GetString());
    }
}
