using Chargehand.Config;
using Chargehand.Kernel;

namespace Chargehand.Tests;

/// <summary>ADR 0067: the profile's <c>automation.plane</c> section is checked when it loads.</summary>
public class PlaneSettingsTests
{
    private const string Valid = """
        {"url":"https://plane.example.test","api_key_secret":"plane-key","workspace":"ws","projects":{"p1":{"path":"/srv/repos/repo","remote":"octo/repo"}},
         "trigger_state":"Agent","started_state":"In Progress","review_state":"In Review"
        """;

    private static Profile Load(string plane, bool driven = true)
    {
        using var dir = new TempDir();
        return Profile.Load(dir.Write("local.json", "{\"schema\":\"profile/v1\",\"driven\":{\"enabled\":" + (driven ? "true" : "false") + "},\"automation\":{\"plane\":" + plane + "}}"));
    }

    [Fact]
    public void The_section_loads_with_its_defaults_and_needs_no_github_app()
    {
        var plane = Load(Valid + "}").Automation!.Plane!;

        Assert.Equal(("Agent", "In Progress", "In Review", (string?)null), (plane.TriggerState, plane.StartedState, plane.ReviewState, plane.FailedState));
        Assert.Equal((120, "M", 2, 10, 2_000_000L, 5m), (plane.PollSeconds, plane.BudgetClass, plane.MaxConcurrent, plane.MaxStartsPerDay, plane.MaxTokensTotal, plane.MaxUsdTotal));
        Assert.Equal(("/srv/repos/repo", "octo/repo"), (plane.Projects!["p1"].Path, plane.Projects["p1"].Remote));
    }

    [Fact]
    public void Without_driven_the_profile_is_refused() =>
        Assert.Contains("needs driven.enabled", Assert.Throws<ChargehandException>(() => Load(Valid + "}", driven: false)).Message);

    [Theory]
    [InlineData("\"url\":\"https://plane.example.test\"", "\"url\":\"plane.example.test\"", "url must be an absolute")]
    [InlineData("\"url\":\"https://plane.example.test\"", "\"url\":\"ftp://plane.example.test\"", "url must be an absolute")]
    [InlineData("\"api_key_secret\":\"plane-key\"", "\"api_key_secret\":\"\"", "api_key_secret")]
    [InlineData("\"workspace\":\"ws\"", "\"workspace\":\" \"", "workspace")]
    [InlineData("\"projects\":{\"p1\":{\"path\":\"/srv/repos/repo\",\"remote\":\"octo/repo\"}}", "\"projects\":{}", "projects must list")]
    [InlineData("\"path\":\"/srv/repos/repo\"", "\"path\":\"\"", "path must be")]
    [InlineData("\"remote\":\"octo/repo\"", "\"remote\":\"octo\"", "remote must be owner/repo")]
    [InlineData("\"trigger_state\":\"Agent\"", "\"trigger_state\":\"\"", "trigger_state")]
    [InlineData("\"started_state\":\"In Progress\"", "\"started_state\":\"agent\"", "trigger_state must differ")]
    [InlineData("\"review_state\":\"In Review\"", "\"review_state\":\"\"", "review_state")]
    [InlineData("\"review_state\":\"In Review\"", "\"review_state\":\"In Review\",\"failed_state\":\" \"", "failed_state")]
    [InlineData("\"review_state\":\"In Review\"", "\"review_state\":\"In Review\",\"poll_seconds\":5", "poll_seconds")]
    [InlineData("\"review_state\":\"In Review\"", "\"review_state\":\"In Review\",\"budget_class\":\"XL\"", "budget_class")]
    [InlineData("\"review_state\":\"In Review\"", "\"review_state\":\"In Review\",\"max_concurrent\":0", "max_concurrent")]
    [InlineData("\"review_state\":\"In Review\"", "\"review_state\":\"In Review\",\"max_starts_per_day\":0", "max_starts_per_day")]
    [InlineData("\"review_state\":\"In Review\"", "\"review_state\":\"In Review\",\"max_tokens_total\":0", "max_tokens_total")]
    [InlineData("\"review_state\":\"In Review\"", "\"review_state\":\"In Review\",\"max_usd_total\":0", "max_usd_total")]
    public void A_value_that_cannot_work_is_refused_when_the_profile_loads(string from, string to, string problem)
    {
        var e = Assert.Throws<ChargehandException>(() => Load((Valid + "}").Replace(from, to, StringComparison.Ordinal)));

        Assert.StartsWith("automation.plane:", e.Message);
        Assert.Contains(problem, e.Message);
        Assert.Contains("docs/guide/intake.md", e.Action);
    }

    [Fact]
    public void A_section_with_nothing_set_lists_every_missing_key() =>
        Assert.Contains("workspace", Assert.Throws<ChargehandException>(() => Load("{}")).Message);

    [Fact]
    public void The_state_is_kept_across_a_restart_and_a_missing_file_is_empty()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "intake", "plane.json");
        Assert.Empty(Chargehand.Automation.PlaneState.Load(path).Tasks);

        var state = Chargehand.Automation.PlaneState.Load(path);
        state.Tasks.Add(new Chargehand.Automation.PlaneTask("p1", "i1", "ABC-1", "run-1", "plane-abc-1", DateTimeOffset.UtcNow));
        state.Handled.Add("i1@t");
        state.Save(DateTimeOffset.UtcNow);

        var again = Chargehand.Automation.PlaneState.Load(path);
        Assert.Equal(("plane-abc-1", Chargehand.Automation.PlanePhase.Running), (again.Tasks.Single().TaskId, again.Tasks.Single().Phase));
        Assert.Contains("i1@t", again.Handled);
    }
}
