using System.Net;
using Chargehand.Automation;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Tests;

/// <summary>CHARGEHAND-190: open code-scanning and Dependabot alerts become driven tasks through the <c>security-alerts</c> template: read as the App, deduplicated across runs, capped per run.</summary>
public class SecurityAlertTests
{
    private sealed class FixedToken : IInstallationTokens
    {
        public Task<string> GetAsync(string owner, string repository, CancellationToken ct) => Task.FromResult("ghs_secrettoken");
    }

    private sealed class Routes(Func<string, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            var (status, body) = answer(request.RequestUri.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private const string CodeScanning = """
        [{"number":12,"html_url":"https://github.com/o/r/security/code-scanning/12","rule":{"id":"js/sql-injection","severity":"error","security_severity_level":"high","description":"Database query built from\nuser-controlled sources"},
          "tool":{"name":"CodeQL"},"most_recent_instance":{"location":{"path":"src/db.js","start_line":42},"message":{"text":"This query depends on a user-provided value."}}}]
        """;

    private const string Dependabot = """
        [{"number":7,"html_url":"https://github.com/o/r/security/dependabot/7","dependency":{"manifest_path":"package-lock.json"},
          "security_advisory":{"ghsa_id":"GHSA-aaaa-bbbb-cccc","cve_id":"CVE-2026-0001","summary":"Prototype pollution in lodash","severity":"critical"},
          "security_vulnerability":{"package":{"ecosystem":"npm","name":"lodash"},"severity":"critical","vulnerable_version_range":"< 4.17.21","first_patched_version":{"identifier":"4.17.21"}}}]
        """;

    private static GitHubIntakeHost Host(Routes routes) => new(new HttpClient(routes) { BaseAddress = new Uri("https://api.example.test/") }, new FixedToken());

    [Fact]
    public async Task Both_kinds_are_read_open_and_described_with_where_and_what()
    {
        var routes = new Routes(path => (HttpStatusCode.OK, path.Contains("code-scanning", StringComparison.Ordinal) ? CodeScanning : Dependabot));
        var read = await Host(routes).OpenAlertsAsync("o/r", default);

        Assert.Equal(["/repos/o/r/code-scanning/alerts?state=open&per_page=100", "/repos/o/r/dependabot/alerts?state=open&per_page=100"], routes.Paths);
        Assert.Empty(read.Skipped);
        var (code, dep) = (read.Alerts[0], read.Alerts[1]);
        Assert.Equal(("code-scanning-12", "high", "Database query built from user-controlled sources"), (code.Key, code.Severity, code.Title));
        Assert.Equal("Rule js/sql-injection (CodeQL) at src/db.js:42: This query depends on a user-provided value.", code.Detail);
        Assert.Equal(("dependabot-7", "critical", "Prototype pollution in lodash"), (dep.Key, dep.Severity, dep.Title));
        Assert.Equal("Package npm lodash in package-lock.json, vulnerable < 4.17.21, first patched 4.17.21 (GHSA-aaaa-bbbb-cccc, CVE-2026-0001)", dep.Detail);
    }

    [Fact]
    public async Task A_kind_GitHub_refuses_is_skipped_with_its_reason_and_an_outage_is_an_error()
    {
        var refused = await Host(new Routes(path => path.Contains("code-scanning", StringComparison.Ordinal)
            ? (HttpStatusCode.Forbidden, """{"message":"Advanced Security must be enabled for this repository to use code scanning."}""")
            : (HttpStatusCode.OK, Dependabot))).OpenAlertsAsync("o/r", default);
        Assert.Equal(["dependabot-7"], refused.Alerts.Select(a => a.Key));
        Assert.StartsWith("code-scanning: GitHub answered 403", Assert.Single(refused.Skipped));

        await Assert.ThrowsAsync<IntakeException>(() => Host(new Routes(_ => (HttpStatusCode.BadGateway, "{}"))).OpenAlertsAsync("o/r", default));
    }

    private static SecurityAlert Alert(string kind, int number, string severity) => new(kind, number, severity, $"alert {number}", "detail", null);

    [Fact]
    public void Fresh_alerts_are_the_untaken_ones_most_severe_first_up_to_the_cap()
    {
        SecurityAlert[] open = [Alert("dependabot", 1, "low"), Alert("code-scanning", 2, "critical"), Alert("dependabot", 3, "moderate"), Alert("code-scanning", 4, "error"), Alert("dependabot", 5, "critical")];
        var fresh = SecurityAlerts.Fresh(open, new HashSet<string> { "dependabot-5" }, 3);
        Assert.Equal(["code-scanning-2", "code-scanning-4", "dependabot-3"], fresh.Select(a => a.Key));
    }

    [Fact]
    public void A_task_carries_the_template_and_the_alert_with_a_one_line_title()
    {
        var task = SecurityAlerts.TaskFor(new SecurityAlert("dependabot", 7, "critical", "Prototype pollution in lodash", "Package npm lodash", "https://x.test/7"), "GOAL", "S");
        Assert.Equal(("alert-dependabot-7", "S", "Fix Dependabot alert #7: Prototype pollution in lodash"), (task.Id, task.Size, task.Title));
        Assert.Equal("GOAL\n\nDependabot alert #7, severity critical: Prototype pollution in lodash\nPackage npm lodash\nhttps://x.test/7", task.Goal);
    }

    private sealed class FakeAlerts : ISecurityAlerts
    {
        public List<SecurityAlert> Open { get; set; } = [];

        public List<string> Skipped { get; set; } = [];

        public List<string> Asked { get; } = [];

        public Task<SecurityAlertsRead> OpenAlertsAsync(string repository, CancellationToken ct)
        {
            Asked.Add(repository);
            return Task.FromResult(new SecurityAlertsRead([.. Open], [.. Skipped]));
        }
    }

    /// <summary>A rig whose entry reads a real checkout with a GitHub origin, so the repository comes from git as on a server.</summary>
    private sealed class AlertRig : IDisposable
    {
        private readonly TempDir _dir = new();

        public AlertRig(int? maxTasks = null, bool withReader = true)
        {
            var repo = Runs.GitRepo(_dir.Path);
            Runs.SetOrigin(repo, "https://github.com/o/r.git");
            Entry = new ScheduleEntry("alerts", "daily 04:00", Template: ScheduleTemplates.SecurityAlerts, Repository: new AutomationRepository(repo.Path), MaxTasks: maxTasks);
            Rig = new ScheduleRig(Entry);
            Rig.UseAlerts(withReader ? Alerts : null);
        }

        public ScheduleEntry Entry { get; }

        public ScheduleRig Rig { get; }

        public FakeAlerts Alerts { get; } = new();

        public async Task Occurrence(int day)
        {
            if (day == 0)
                await Rig.Tick();
            await Rig.Wake(day == 0 ? TimeSpan.FromHours(4) : TimeSpan.FromDays(1));
        }

        public void Dispose()
        {
            Rig.Dispose();
            _dir.Dispose();
        }
    }

    [Fact]
    public async Task One_run_is_a_batch_of_one_task_per_new_alert_and_the_next_run_takes_only_new_ones()
    {
        using var a = new AlertRig(maxTasks: 2);
        a.Alerts.Open = [Alert("dependabot", 1, "high"), Alert("code-scanning", 2, "critical"), Alert("dependabot", 3, "low")];

        await a.Occurrence(0);
        Assert.Equal(["o/r"], a.Alerts.Asked);
        var first = Assert.Single(a.Rig.Starter.Started);
        Assert.Equal(("driven", 1), (first.Context.Preset, first.Driven!.MaxParallel));
        Assert.Equal(["alert-code-scanning-2", "alert-dependabot-1"], first.Driven.Tasks.Select(t => t.Id));
        Assert.All(first.Driven.Tasks, t => Assert.StartsWith(ScheduleTemplates.Find(ScheduleTemplates.SecurityAlerts)!.Goal!, t.Goal));

        a.Rig.Log.End("run-1", ScheduleRig.Result());
        await a.Occurrence(1);
        Assert.Equal(["alert-dependabot-3"], a.Rig.Starter.Started[1].Driven!.Tasks.Select(t => t.Id));
        Assert.Equal(["code-scanning-2", "dependabot-1", "dependabot-3"], a.Rig.State.Taken["alerts"]);
    }

    [Fact]
    public async Task No_new_alert_starts_nothing_and_does_not_use_the_cap()
    {
        using var a = new AlertRig();
        await a.Occurrence(0);
        Assert.Empty(a.Rig.Starter.Started);
        Assert.Equal(0, a.Rig.State.StartsIn("alerts", a.Rig.Time.GetUtcNow()));
        Assert.Contains(a.Rig.Warnings, w => w.Contains("no open alert that is not already a task", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_closed_alert_is_forgotten_so_a_reopened_one_is_taken_again_but_an_unread_kind_keeps_its_alerts()
    {
        using var a = new AlertRig();
        a.Alerts.Open = [Alert("dependabot", 1, "high"), Alert("code-scanning", 2, "high")];
        await a.Occurrence(0);
        a.Rig.Log.End("run-1", ScheduleRig.Result());

        a.Alerts.Open = [];
        a.Alerts.Skipped = ["code-scanning: GitHub answered 403 to the code-scanning alerts: no permission"];
        await a.Occurrence(1);
        Assert.Equal(["code-scanning-2"], a.Rig.State.Taken["alerts"]);
        Assert.Contains(a.Rig.Warnings, w => w.EndsWith("code-scanning: GitHub answered 403 to the code-scanning alerts: no permission", StringComparison.Ordinal));

        a.Alerts.Open = [Alert("dependabot", 1, "high"), Alert("code-scanning", 2, "high")];
        a.Alerts.Skipped = [];
        await a.Occurrence(2);
        Assert.Equal(["alert-dependabot-1"], a.Rig.Starter.Started[1].Driven!.Tasks.Select(t => t.Id));
    }

    [Fact]
    public async Task Without_the_App_the_template_starts_nothing_and_says_why()
    {
        using var a = new AlertRig(withReader: false);
        await a.Occurrence(0);
        Assert.Empty(a.Rig.Starter.Started);
        Assert.Contains(a.Rig.Warnings, w => w.Contains("driven.integrator", StringComparison.Ordinal));
    }
}
