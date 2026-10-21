using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0068: the profile's <c>automation.schedules</c> section is checked when it loads.</summary>
public class ScheduleSettingsTests
{
    private const string Driven = "\"enabled\":true,\"integrator\":{\"app_id\":1,\"installation_id\":2,\"private_key_secret\":\"app-key\"}";

    private static Profile Load(string schedules, string driven = Driven)
    {
        using var dir = new TempDir();
        return Profile.Load(dir.Write("local.json", "{\"schema\":\"profile/v1\",\"driven\":{" + driven + "},\"automation\":{\"schedules\":" + schedules + "}}"));
    }

    private static string Entry(string rest) => "{\"entries\":[{\"id\":\"nightly\",\"at\":\"daily 04:00\",\"repository\":{\"path\":\"/srv/repos/repo\"}," + rest + "}]}";

    [Fact]
    public void An_entry_loads_with_its_defaults()
    {
        var schedules = Load(Entry("\"template\":\"doc-drift\"")).Automation!.Schedules!;
        var entry = Assert.Single(schedules.Entries!);
        Assert.Equal(("nightly", ScheduleKind.ReadOnly, 4, "S"), (entry.Id, entry.Kind(), entry.Cap(), entry.Size()));
        Assert.Equal("/srv/repos/repo", entry.Repository!.Path);
        Assert.Null(schedules.NotifyUrl);
    }

    [Fact]
    public void Every_template_has_its_kind_and_the_report_a_monthly_cap_of_a_month_of_mornings()
    {
        Assert.Equal(ScheduleKind.Driven, Load(Entry("\"template\":\"dependency-upgrades\"")).Automation!.Schedules!.Entries![0].Kind());
        Assert.Equal(ScheduleKind.ReadOnly, Load(Entry("\"template\":\"flaky-tests\"")).Automation!.Schedules!.Entries![0].Kind());
        var alerts = Load(Entry("\"template\":\"security-alerts\",\"max_tasks\":5")).Automation!.Schedules!.Entries![0];
        Assert.Equal((ScheduleKind.Driven, 5, 3), (alerts.Kind(), alerts.Tasks(), (alerts with { MaxTasks = null }).Tasks()));
        var report = Load("{\"notify_url\":\"https://ntfy.example/topic\",\"entries\":[{\"id\":\"am\",\"at\":\"daily 06:00\",\"template\":\"morning-report\"}]}").Automation!.Schedules!.Entries![0];
        Assert.Equal((ScheduleKind.Report, 31), (report.Kind(), report.Cap()));
        Assert.Null(report.Text());
    }

    [Fact]
    public void A_free_goal_is_read_only_unless_it_says_driven()
    {
        Assert.Equal(ScheduleKind.ReadOnly, Load(Entry("\"goal\":\"List the TODOs\"")).Automation!.Schedules!.Entries![0].Kind());
        Assert.Equal(ScheduleKind.Driven, Load(Entry("\"goal\":\"Remove dead code\",\"driven\":true")).Automation!.Schedules!.Entries![0].Kind());
    }

    [Theory]
    [InlineData("""{}""", "entries must list")]
    [InlineData("""{"entries":[]}""", "entries must list")]
    [InlineData("""{"entries":[{"id":"Bad Id","at":"daily 04:00","template":"doc-drift","repository":{"path":"/p"}}]}""", "id 'Bad Id'")]
    [InlineData("""{"entries":[{"id":"a","at":"daily 04:00","template":"doc-drift","repository":{"path":"/p"}},{"id":"a","at":"daily 05:00","template":"doc-drift","repository":{"path":"/p"}}]}""", "used more than once")]
    [InlineData("""{"entries":[{"id":"a","at":"every day","template":"doc-drift","repository":{"path":"/p"}}]}""", "at must be")]
    [InlineData("""{"entries":[{"id":"a","at":"monthly 31 04:00","template":"doc-drift","repository":{"path":"/p"}}]}""", "must be 1 to 28")]
    [InlineData("""{"entries":[{"id":"a","at":"daily 04:00","repository":{"path":"/p"}}]}""", "exactly one of template and goal")]
    [InlineData("""{"entries":[{"id":"a","at":"daily 04:00","template":"doc-drift","goal":"x","repository":{"path":"/p"}}]}""", "exactly one of template and goal")]
    [InlineData("""{"entries":[{"id":"a","at":"daily 04:00","template":"nope","repository":{"path":"/p"}}]}""", "template 'nope' must be one of")]
    [InlineData("""{"entries":[{"id":"a","at":"daily 04:00","goal":" ","repository":{"path":"/p"}}]}""", "goal must be")]
    [InlineData("""{"entries":[{"id":"a","at":"daily 04:00","template":"doc-drift","driven":true,"repository":{"path":"/p"}}]}""", "driven applies to a goal")]
    [InlineData("""{"entries":[{"id":"a","at":"daily 04:00","template":"doc-drift","repository":{"path":"/p"},"max_per_month":0}]}""", "max_per_month")]
    [InlineData("""{"entries":[{"id":"a","at":"daily 04:00","template":"doc-drift","repository":{"path":"/p"},"max_per_month":101}]}""", "max_per_month")]
    [InlineData("""{"entries":[{"id":"a","at":"daily 04:00","template":"doc-drift","repository":{"path":"/p"},"budget_class":"XL"}]}""", "budget_class")]
    [InlineData("""{"entries":[{"id":"a","at":"daily 04:00","template":"doc-drift","repository":{"path":"/p"},"max_tasks":2}]}""", "max_tasks applies to the security-alerts template")]
    [InlineData("""{"entries":[{"id":"a","at":"daily 04:00","template":"security-alerts","repository":{"path":"/p"},"max_tasks":11}]}""", "max_tasks must be 1 to 10")]
    [InlineData("""{"entries":[{"id":"a","at":"daily 04:00","template":"doc-drift"}]}""", "repository.path")]
    [InlineData("""{"entries":[{"id":"a","at":"daily 04:00","template":"morning-report"}]}""", "needs notify_url")]
    [InlineData("""{"notify_url":"ntfy.example/topic","entries":[{"id":"a","at":"daily 04:00","template":"morning-report"}]}""", "notify_url must be")]
    public void A_value_that_cannot_work_is_refused_when_the_profile_loads(string schedules, string problem)
    {
        var e = Assert.Throws<ChargehandException>(() => Load(schedules));
        Assert.StartsWith("automation.schedules: ", e.Message);
        Assert.Contains(problem, e.Message);
        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.Contains("docs/guide/intake.md", e.Action);
    }

    [Fact]
    public void A_driven_entry_needs_driven_enabled_and_a_read_only_one_does_not()
    {
        var e = Assert.Throws<ChargehandException>(() => Load(Entry("\"template\":\"dependency-upgrades\""), driven: "\"enabled\":false"));
        Assert.Contains("needs driven.enabled", e.Message);
        Assert.NotNull(Load(Entry("\"template\":\"doc-drift\""), driven: "\"enabled\":false").Automation!.Schedules);
    }

    [Fact]
    public void Schedules_are_valid_beside_a_profile_with_no_github_section() =>
        Assert.Null(Load(Entry("\"template\":\"doc-drift\"")).Automation!.Github);
}
