using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Chargehand.Kernel;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Modules.Sessions.Tests;

/// <summary>ADR 0060: the session's files and lines are written without reflection, byte for byte as the reflection serializer wrote them with the same options.</summary>
public sealed class SessionsJsonContextTests
{
    // Characters the default encoder escapes, so a difference in encoders shows.
    private const string Text = "a <b> & \"c\" — ü";

    private static readonly DateTimeOffset At = new(2026, 10, 14, 9, 30, 0, TimeSpan.FromHours(3));

    private static JsonSerializerOptions TaskOptions() => new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    private static JsonSerializerOptions OutcomeOptions() => new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    private static JsonSerializerOptions LineOptions() => new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static JsonSerializerOptions EnvironmentOptions() => new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    private static readonly GovernorDecision Decision = new(At, 7, GovernorMode.Enforce, GovernorAction.Checkpoint, "spend", "checkpoint_spend",
        new Dictionary<string, string> { ["spent"] = Text }, 1.25m, 2m, "M", true, new BudgetSource("history", 23, 80, "repository"));

    private static readonly SessionEnvironment Environment = new(["dotnet", "node"], ["dotnet", "test"], "detected", ["chargehand-cache-npm-0123456789ab"]);

    [Fact]
    public void The_task_file_is_written_and_read_as_before()
    {
        var full = new SessionTask("r1", Text, "chargehand/r1", 80, 45, 600, 100_000, "model-a", 1.5m,
            new Dictionary<string, ModelPrice> { ["model-a"] = new(3m, 15m, 0.3m, 3.75m) }, new SessionGovernor(GovernorMode.Enforce, BudgetUsd: 2m), Environment);
        var bare = new SessionTask("r2", "g", "chargehand/r2", 80, 45, 600);
        foreach (var task in new[] { full, bare })
        {
            Same(task, TaskOptions(), Context(TaskOptions()).SessionTask);
            Assert.Equal(JsonSerializer.Serialize(task, TaskOptions()), JsonSerializer.Serialize(task, SessionCli.Context.SessionTask));
        }

        // A field the session image does not know yet is skipped, as before.
        var json = JsonSerializer.Serialize(full, TaskOptions()).Replace("\"run_id\"", "\"later_field\":1,\"run_id\"", StringComparison.Ordinal);
        Assert.Equal(JsonSerializer.Serialize(JsonSerializer.Deserialize<SessionTask>(json, TaskOptions()), TaskOptions()),
            JsonSerializer.Serialize(JsonSerializer.Deserialize(json, SessionCli.Context.SessionTask), TaskOptions()));
    }

    [Fact]
    public void The_usage_file_is_written_and_read_as_before()
    {
        var usage = new SessionUsage(1234, 5, "write", 10, 20, 0.5m, true);
        Same(usage, OutcomeOptions(), Context(OutcomeOptions()).SessionUsage);
        Same(new SessionUsage(0), OutcomeOptions(), Context(OutcomeOptions()).SessionUsage);
        var json = JsonSerializer.Serialize(usage, OutcomeOptions());
        Assert.Equal(JsonSerializer.Deserialize<SessionUsage>(json, TaskOptions()), JsonSerializer.Deserialize(json, SessionCli.Context.SessionUsage));
    }

    [Fact]
    public void The_outcome_file_is_written_as_before()
    {
        var full = new SessionOutcome(SessionStatus.RateLimited, "rate_limited", 12, 100, 200, 300, 1.5m, 0, [Text], true, "abc", 400, "s1", "model-a",
            new Dictionary<string, TokenCounts> { ["model-a"] = new(1, 2, 0, 3, 4) }, 0.75m, At, true, 3, Text, [Decision], "def");
        var bare = new SessionOutcome(SessionStatus.Completed, "", 1, 0, 0, 0, null, null, [], false);
        Same(full, OutcomeOptions(), Context(OutcomeOptions()).SessionOutcome);
        Same(bare, OutcomeOptions(), Context(OutcomeOptions()).SessionOutcome);
    }

    [Fact]
    public void A_governor_line_is_written_as_before() =>
        Same(Decision, LineOptions(), Context(LineOptions()).GovernorDecision);

    [Fact]
    public void The_manifest_is_written_and_read_as_before()
    {
        var manifest = new ToolchainManifest(new Dictionary<string, string> { ["dotnet"] = "10.0.100", ["node"] = Text }, ["go"]);
        Assert.Equal(JsonSerializer.Serialize(manifest, EnvironmentOptions()), manifest.ToJson());
        Same(manifest, EnvironmentOptions(), Context(EnvironmentOptions()).ToolchainManifest);
        var json = JsonSerializer.Serialize(manifest, EnvironmentOptions());
        Assert.Equal(JsonSerializer.Serialize(JsonSerializer.Deserialize<ToolchainManifest>(json, EnvironmentOptions()), EnvironmentOptions()),
            JsonSerializer.Serialize(JsonSerializer.Deserialize(json, Context(EnvironmentOptions()).ToolchainManifest), EnvironmentOptions()));
    }

    [Fact]
    public void The_mcp_config_is_written_as_before()
    {
        const string url = "http://chargehand-driven:4300/v1/mcp";
        const string token = "tok<&>\"";
        var before = JsonSerializer.Serialize(new { mcpServers = new { chargehand = new { type = "http", url, headers = new { Authorization = $"Bearer {token}" } } } });
        Assert.Equal(before, SessionDriver.McpConfig(url, token));
    }

    [Fact]
    public void The_handoff_document_is_written_as_before()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var document = new HandoffBuilder.Document(1, "turn_limit", 9, "abc", null, [Text], [new ChangedPath("src/a.cs", 3, 1)], 0, new TestRun("dotnet test", 1, true),
            [new StepState("research", true)], [new TodoItem(Text, "pending")], [new ToolCall("Bash", new Dictionary<string, string> { ["command"] = Text })], [new HandoffFinding("key", "calls")]);
        Same(document, options, Context(options).Document);
    }

    [Fact]
    public void The_span_details_are_written_as_before()
    {
        var tokens = new Dictionary<string, long> { ["input"] = 1, ["cache_read_input_tokens"] = 2 };
        var cost = new Dictionary<string, decimal> { ["total"] = 0.125m };
        Assert.Equal(JsonSerializer.Serialize(tokens), JsonSerializer.Serialize(tokens, SessionsJsonContext.Default.DictionaryStringInt64));
        Assert.Equal(JsonSerializer.Serialize(cost), JsonSerializer.Serialize(cost, SessionsJsonContext.Default.DictionaryStringDecimal));
    }

    private static SessionsJsonContext Context(JsonSerializerOptions options) => new(options);

    // The reflection serializer with a fresh copy of the same options, as the code wrote before ADR 0060.
    private static void Same<T>(T value, JsonSerializerOptions reflection, JsonTypeInfo<T> generated) =>
        Assert.Equal(JsonSerializer.Serialize(value, reflection), JsonSerializer.Serialize(value, generated));
}
