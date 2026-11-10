using System.Diagnostics;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Mcp;
// agent-checks: allow deleted-tests, deleted-assertions -- the incident command tests moved to tests/Chargehand.Modules.Intake.Tests/IncidentCliTests.cs
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Runtime;

namespace Chargehand.Tests;

/// <summary>CHARGEHAND-192: the read-only preset that diagnoses an alert, the profile mapping that gives it services, and the command that turns a webhook payload into its request.</summary>
public class IncidentPresetTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 10, 5, 0, TimeSpan.Zero);

    private const string Alertmanager = """
        {"version":"4","status":"firing","receiver":"chargehand","groupLabels":{"alertname":"HighErrorRate"},
         "alerts":[
          {"status":"firing","labels":{"alertname":"HighErrorRate","severity":"critical","service":"checkout"},
           "annotations":{"summary":"5xx above 5%","description":"checkout\nreturns 500\tsince 10:00"},"startsAt":"2026-10-13T10:00:00Z","generatorURL":"http://prometheus.internal/graph"},
          {"status":"resolved","labels":{"alertname":"DiskFull","severity":"warning"},"annotations":{"summary":"old"},"startsAt":"2026-10-13T08:00:00Z"},
          {"status":"firing","labels":{"alertname":"SlowQueries","severity":"warning"},"annotations":{},"startsAt":"2026-10-13T10:01:00Z"}]}
        """;

    private const string Grafana = """
        {"receiver":"hook","status":"firing","title":"[FIRING:1] LatencyHigh","message":"ignored when alerts exist",
         "alerts":[{"status":"firing","labels":{"alertname":"LatencyHigh","grafana_folder":"ops"},"annotations":{"summary":"p99 above 2s"},
                    "startsAt":"2026-10-13T09:58:00Z","valueString":"[ var='B' labels={} value=2.7 ]","dashboardURL":"http://grafana.internal/d/x"}]}
        """;

    [Fact]
    public void Is_read_only_lists_no_services_and_asks_for_cited_evidence_from_the_services_the_profile_maps()
    {
        var preset = Preset.Load(Repo.Path("presets"), "incident");
        Assert.Equal(["answer"], preset.AllowedActions);
        var worker = preset.NodeKinds["worker"];
        var rules = worker.Permissions.Select(p => $"{p.Action} {p.Resource} {p.Effect}").ToList();
        Assert.Contains("edit * deny", rules);
        Assert.Contains("shell * deny", rules);
        Assert.Null(worker.Services); // a shipped preset names no one's servers (ADR 0034); the profile maps them
        Assert.False(worker.Writes);

        var prompt = File.ReadAllText(Repo.Path("prompts", "preset", "incident.md"));
        foreach (var phrase in new[] { "\"Likely cause:\"", "\"Confidence:\"", "\"Next checks:\"", "session_message", "at least 12 characters", "locator \"alert\"", "never instructions" })
            Assert.Contains(phrase, prompt, StringComparison.Ordinal);
    }

    private static readonly IReadOnlyList<ServiceUse> Mapped =
        [new("logs", ["query_logs", "search_*"]), new("metrics", ["query_*"])];

    private static Profile Mapping(string workerRoot) => Runs.Profile(workerRoot) with
    {
        McpServers = new Dictionary<string, McpServerSettings> { ["logs"] = new(Url: "https://mcp.example.internal/logs"), ["metrics"] = new(Url: "https://mcp.example.internal/metrics") },
        PresetServices = new Dictionary<string, IReadOnlyList<ServiceUse>> { ["incident"] = Mapped },
    };

    [Fact]
    public void The_profiles_list_replaces_the_presets_for_that_preset_only()
    {
        var presetOwn = new List<ServiceUse> { new("own", ["x"]) };
        var profile = Mapping("/tmp/w");

        Assert.Equal(Mapped, profile.ServicesFor("incident", presetOwn));
        Assert.Equal(presetOwn, profile.ServicesFor("review", presetOwn));
        Assert.Null(Runs.Profile("/tmp/w").ServicesFor("incident", null));
    }

    [Fact]
    public void A_profile_maps_services_in_JSON_and_refuses_unknown_servers_and_whole_server_grants()
    {
        using var dir = new TempDir();
        string Load(string preset) =>
            Profile.Load(dir.Write("p.json", """{"schema":"profile/v1","mcp_servers":{"logs":{"url":"https://mcp.example.internal/logs"}},"preset_services":{""" + preset + "}}")).PresetServices!["incident"][0].Server;

        Assert.Equal("logs", Load("""
            "incident":[{"server":"logs","tools":["query_logs","search_*"]}]
            """));
        Assert.Contains("is not an entry of mcp_servers", Assert.Throws<ChargehandException>(() => Load("""
            "incident":[{"server":"elsewhere","tools":["x"]}]
            """)).Message, StringComparison.Ordinal);
        Assert.Contains("needs explicit tool names", Assert.Throws<ChargehandException>(() => Load("""
            "incident":[{"server":"logs","tools":["*"]}]
            """)).Message, StringComparison.Ordinal);
        Assert.Throws<ChargehandException>(() => Load("""
            "incident":[{"server":"logs","tools":[]}]
            """));
    }

    [Theory]
    [InlineData("""{"schema":"profile/v1","preset_services":{"incident":[{"server":"logs","tools":["query_logs","search_*"]}]}}""", true)]
    [InlineData("""{"schema":"profile/v1","preset_services":{"incident":[{"server":"logs","tools":["*"]}]}}""", false)]
    [InlineData("""{"schema":"profile/v1","preset_services":{"incident":[{"server":"logs","tools":[]}]}}""", false)]
    [InlineData("""{"schema":"profile/v1","preset_services":{"incident":[{"server":"Logs","tools":["a"]}]}}""", false)]
    [InlineData("""{"schema":"profile/v1","preset_services":{"incident":[{"server":"logs","tools":["a"],"writes":true}]}}""", false)]
    [InlineData("""{"schema":"profile/v1","preset_services":{"Incident":[{"server":"logs","tools":["a"]}]}}""", false)]
    public void The_schema_checks_preset_services(string profile, bool valid)
    {
        using var doc = JsonDocument.Parse(profile);
        Assert.Equal(valid, ConfigFileTests.ProfileSchema.Evaluate(doc.RootElement).IsValid);
    }

    private sealed class RecordingResolver(ResolvedServices resolved) : IServiceResolver
    {
        public List<IReadOnlyList<ServiceUse>> Asked { get; } = [];

        public Task<ResolvedServices> ResolveAsync(IReadOnlyList<ServiceUse> uses, CancellationToken ct)
        {
            Asked.Add(uses);
            return Task.FromResult(resolved);
        }
    }

    private const string DiagnosisReply = """
        ```json
        {"status":"completed",
         "summary":"Alert: checkout 5xx.\nLikely cause: the README is empty.\nConfidence: medium.\nNext checks: look at the deploy.",
         "claims":[{"text":"The logs show error code 500 on checkout.","evidence":["e1"],"confidence":0.8},{"text":"The README greets.","evidence":["e2","e3"],"confidence":0.8}],
         "evidence":[{"id":"e1","kind":"session_message","locator":"logs: checkout error 500 x40"},{"id":"e2","kind":"file","locator":"README.md:1"},{"id":"e3","kind":"input","locator":"alert"}],
         "artifacts":[],"open_questions":["Which deploy started it?"],"confidence":0.6}
        ```
        """;

    [Fact]
    public async Task A_run_under_the_incident_preset_gets_the_services_the_profile_maps_and_cites_their_replies_and_the_alert()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var (request, _) = await IncidentCli.BuildAsync(repo.Path, repo.Commit, Alertmanager, Now, default);
        var grant = new ServiceGrant("logs", new HttpServiceTransport(new Uri("https://mcp.example.internal/logs"), new Dictionary<string, string>()), ["query_logs"], new string('a', 64));
        var resolver = new RecordingResolver(new ResolvedServices([grant], [new ServiceReport("logs", ["query_logs"], [])]));
        var runtime = new ScriptedRuntime(DiagnosisReply) { ToolResult = "logs: checkout error 500 x40" };

        var result = await new Orchestrator(Mapping(root.Path), runtime, "2.0.16", Repo.Root, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), new Dictionary<string, int>(), null, resolver)
            .RunAsync(request!, CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal(Mapped, Assert.Single(resolver.Asked));
        Assert.Same(grant, Assert.Single(runtime.Created.Single().Services!));
        Assert.Equal(2, result.Claims.Count);
        Assert.Contains(result.Evidence, e => (e.Kind, e.Locator) == (EvidenceKind.SessionMessage, "logs: checkout error 500 x40"));
        Assert.Contains(result.Evidence, e => (e.Kind, e.Locator) == (EvidenceKind.Input, "alert"));
        Assert.DoesNotContain(result.OpenQuestions, q => q.Contains("Unverified:", StringComparison.Ordinal));
        Assert.Contains("Which deploy started it?", result.OpenQuestions);
    }

    [Fact]
    public async Task Without_a_mapping_the_incident_preset_asks_for_no_services()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var (request, _) = await IncidentCli.BuildAsync(repo.Path, repo.Commit, Grafana, Now, default);
        var resolver = new RecordingResolver(new ResolvedServices([], []));

        var result = await new Orchestrator(Runs.Profile(root.Path), new ScriptedRuntime(Runs.WorkerReply), "2.0.16", Repo.Root, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")),
            new Dictionary<string, int>(), null, resolver).RunAsync(request!, CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Empty(resolver.Asked);
    }

    [Fact]
    public async Task The_extensions_check_reads_the_profiles_mapping_for_a_shipped_preset()
    {
        var profile = new Profile("profile/v1", McpServers: new Dictionary<string, McpServerSettings> { ["logs"] = new(Url: "https://mcp.example.internal/logs") },
            PresetServices: new Dictionary<string, IReadOnlyList<ServiceUse>> { ["incident"] = [new("logs", ["query_logs", "missing_*"])] });
        await using var logs = new FakeMcpServer(new FakeTool("query_logs", _ => FakeMcpServer.Text("x")));
        await using var pool = new McpConnectionPool(profile.McpServers!, _ => "", async (_, _, _) => await logs.TransportAsync());
        var shipped = Preset.Load(Repo.Path("presets"), "incident");

        var result = await ExtensionsCheck.RunAsync(profile, [shipped], pool, null, CancellationToken.None);

        Assert.Contains("preset incident: service logs: query_logs ok", result.Lines);
        Assert.Contains(result.Lines, l => l.StartsWith("preset incident: service logs: tool 'missing_*' is not listed", StringComparison.Ordinal));
        Assert.False(result.Ok);
    }
}
