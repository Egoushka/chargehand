using System.Diagnostics;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Modules.Intake.Tests;

/// <summary>CHARGEHAND-192: the command that turns a webhook payload into the incident preset's request.</summary>
public class IncidentCliTests
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
        using var root = new TempRoot();
        var repo = root.GitRepo();

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
        using var root = new TempRoot();
        var repo = root.GitRepo();
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
}
