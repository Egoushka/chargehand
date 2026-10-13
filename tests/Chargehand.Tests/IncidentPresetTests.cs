using System.Diagnostics;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Incident;
using Chargehand.Mcp;
using Chargehand.Modules.Ledger.Api;
using Chargehand.RunLog;
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
    public void An_Alertmanager_payload_lists_its_firing_alerts_with_labels_and_annotations_and_leaves_resolved_ones_out()
    {
        var (alert, title, problem) = IncidentCli.Describe(Alertmanager, Now);

        Assert.Null(problem);
        Assert.Equal("HighErrorRate and 1 more", title);
        Assert.StartsWith("Received: 2026-10-13T10:05:00Z\nGroup status: firing\n\n[1] firing: HighErrorRate, started 2026-10-13T10:00:00Z\n", alert, StringComparison.Ordinal);
        Assert.Contains("labels: alertname=HighErrorRate, service=checkout, severity=critical", alert, StringComparison.Ordinal);
        Assert.Contains("description: checkout returns 500 since 10:00", alert, StringComparison.Ordinal); // whitespace collapsed
        Assert.Contains("[2] firing: SlowQueries", alert, StringComparison.Ordinal);
        Assert.DoesNotContain("DiskFull", alert, StringComparison.Ordinal);
        Assert.Contains("Resolved alerts left out: 1.", alert, StringComparison.Ordinal);
        Assert.DoesNotContain("prometheus.internal", alert, StringComparison.Ordinal); // addresses of the alerting system are not the worker's business
    }

    [Fact]
    public void A_Grafana_payload_keeps_the_values_that_fired_the_rule()
    {
        var (alert, title, _) = IncidentCli.Describe(Grafana, Now);

        Assert.Equal("LatencyHigh", title);
        Assert.Contains("summary: p99 above 2s", alert, StringComparison.Ordinal);
        Assert.Contains("values: [ var='B' labels={} value=2.7 ]", alert, StringComparison.Ordinal);
        Assert.DoesNotContain("grafana.internal", alert, StringComparison.Ordinal);
    }

    [Fact]
    public void More_firing_alerts_than_the_cap_say_how_many_are_left_out()
    {
        var many = JsonSerializer.Serialize(new { alerts = Enumerable.Range(1, IncidentCli.MaxAlerts + 3).Select(i => new { status = "firing", labels = new { alertname = $"A{i}" } }) });

        var (alert, _, _) = IncidentCli.Describe(many, Now);

        Assert.Contains($"[{IncidentCli.MaxAlerts}] firing: A{IncidentCli.MaxAlerts}", alert, StringComparison.Ordinal);
        Assert.DoesNotContain($"[{IncidentCli.MaxAlerts + 1}]", alert, StringComparison.Ordinal);
        Assert.Contains("3 more firing alerts are not listed.", alert, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_text_is_the_alert_and_its_first_line_is_the_title()
    {
        var (alert, title, problem) = IncidentCli.Describe("  Checkout 5xx above 5%\nsince 10:00  ", Now);

        Assert.Null(problem);
        Assert.Equal("Checkout 5xx above 5%", title);
        Assert.Equal("Received: 2026-10-13T10:05:00Z\nCheckout 5xx above 5%\nsince 10:00", alert);
    }

    [Theory]
    [InlineData("", "the alert is empty")]
    [InlineData("   \n", "the alert is empty")]
    [InlineData("""{"status":"firing"}""", "no alerts array")]
    [InlineData("""{"alerts":[{"status":"resolved","labels":{"alertname":"X"}}]}""", "no firing alert")]
    [InlineData("""{"alerts":[]}""", "no firing alert")]
    public void A_payload_with_nothing_to_diagnose_is_refused_with_the_reason(string payload, string expected)
    {
        var (alert, _, problem) = IncidentCli.Describe(payload, Now);

        Assert.Null(alert);
        Assert.Contains(expected, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_that_only_starts_like_JSON_is_still_text()
    {
        var (alert, title, problem) = IncidentCli.Describe("{not json} error rate up", Now);

        Assert.Null(problem);
        Assert.Equal("{not json} error rate up", title);
        Assert.EndsWith("{not json} error rate up", alert, StringComparison.Ordinal);
    }

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)} failed");
    }

    [Fact]
    public async Task The_request_pins_the_commit_carries_the_alert_as_an_input_and_matches_the_schema()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);

        var (request, problem) = await IncidentCli.BuildAsync(repo.Path, "HEAD", Alertmanager, Now, default);

        Assert.Null(problem);
        Assert.Equal("incident", request!.Context.Preset);
        Assert.False(request.Context.Interactive);
        Assert.Equal(repo, request.Context.Repository);
        Assert.Equal("Diagnose this alert: HighErrorRate and 1 more", request.Text);
        var input = Assert.Single(request.Inputs!);
        Assert.Equal(("alert", "alert"), (input.Id, input.Kind));
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.Request, JsonSerializer.SerializeToElement(request, ContractJson.Options)));

        var (none, unknown) = await IncidentCli.BuildAsync(repo.Path, "no-such-ref", Alertmanager, Now, default);
        Assert.Null(none);
        Assert.Contains("is not a commit of", unknown, StringComparison.Ordinal);
        Assert.Equal(repo.Commit, Git(repo.Path, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task The_command_reads_stdin_prints_a_request_and_exits_1_on_a_bad_alert_and_2_on_a_usage_error()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(0, await IncidentCli.RunAsync([repo.Path, "--commit", repo.Commit], root.Path, new StringReader(Grafana), output, error, Now, default));
        var request = JsonSerializer.Deserialize<RunRequest>(output.ToString(), ContractJson.Options)!;
        Assert.Equal("incident", request.Context.Preset);
        Assert.Equal(repo.Commit, request.Context.Repository!.Commit);
        Assert.Equal("", error.ToString());

        Assert.Equal(1, await IncidentCli.RunAsync([repo.Path], root.Path, new StringReader(""), new StringWriter(), error, Now, default));
        Assert.Equal(2, await IncidentCli.RunAsync([], root.Path, new StringReader(Grafana), new StringWriter(), error, Now, default));
        Assert.Equal(2, await IncidentCli.RunAsync([repo.Path, "--bogus"], root.Path, new StringReader(Grafana), new StringWriter(), error, Now, default));
        Assert.Equal(1, await IncidentCli.RunAsync([repo.Path, "--commit", "--output=x"], root.Path, new StringReader(Grafana), new StringWriter(), error, Now, default));
    }

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
