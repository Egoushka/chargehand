using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Conversations.Api;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>Stability plan Task 4 (docs/plans/2026-09-30-stability-1.0.md): the MCP tool "orchestrate" and the HTTP route table
/// are a frozen surface. A checked-in snapshot of each catches an unintended change; <see cref="SchemaCompat"/> keeps the tool's
/// input schema additive-only the same way <c>SchemaCompatTests</c> keeps a published schema major additive-only.</summary>
public class McpSurfaceTests
{
    private static readonly string ReferenceMd = File.ReadAllText(Repo.Path("docs", "guide", "reference.md"));

    [Fact]
    public void The_reference_documents_the_mcp_route_under_its_published_name()
    {
        Assert.Contains("/v1/mcp", ReferenceMd);
        Assert.Equal("orchestrate", OrchestrateTool.Name);
    }

    [Fact]
    public void The_tools_input_schema_changed_only_additively_since_the_snapshot()
    {
        var snapshot = File.ReadAllText(Repo.Path("tests", "Chargehand.Tests", "snapshots", "mcp-orchestrate-input-schema.json"));
        using var before = JsonDocument.Parse(snapshot);
        var after = OrchestrateTool.Create().ProtocolTool.InputSchema;
        var breaks = SchemaCompat.Breaks(before.RootElement, after);
        Assert.True(breaks.Count == 0, $"orchestrate's input schema narrowed what the snapshot accepted (update snapshots/mcp-orchestrate-input-schema.json if this is intended):\n{string.Join("\n", breaks)}");
    }

    [Fact]
    public void The_tools_input_schema_is_exactly_request_v1()
    {
        // request/v1 carries its own additive-only guard (SchemaCompatTests), so the tool inherits it as long as the two stay equal.
        using var expected = JsonDocument.Parse(ContractSchemas.Text(ContractSchemas.Request));
        Assert.Equal(expected.RootElement.GetRawText(), OrchestrateTool.Create().ProtocolTool.InputSchema.GetRawText());
    }

    [Fact]
    public async Task The_route_table_changed_only_by_adding_routes_since_the_snapshot()
    {
        var snapshot = File.ReadAllLines(Repo.Path("tests", "Chargehand.Tests", "snapshots", "http-routes.txt"))
            .Where(l => l.Length > 0).ToHashSet();

        await using var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), tweak: (root, settings) =>
            settings with { Conversations = new ConversationSettings(), ConversationsDirectory = Path.Combine(root, "conversations") });
        var current = server.Routes.Select(r => $"{r.Method} {r.Pattern}").ToHashSet();

        var removed = snapshot.Except(current).ToList();
        Assert.True(removed.Count == 0, $"routes in the snapshot are gone (update snapshots/http-routes.txt if this is intended):\n{string.Join("\n", removed)}");
    }
}
