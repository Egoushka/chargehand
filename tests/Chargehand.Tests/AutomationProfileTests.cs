using System.Text.Json;
using Chargehand.Automation;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Tests;

/// <summary>ADR 0051: the profile's <c>automation.github</c> section is checked when it loads and by the schema, and intake's state survives a restart.</summary>
public class AutomationProfileTests
{
    private const string Driven = "\"enabled\":true,\"integrator\":{\"app_id\":1,\"installation_id\":2,\"private_key_secret\":\"app-key\"}";

    private static Profile Load(string github, string driven = Driven)
    {
        using var dir = new TempDir();
        return Profile.Load(dir.Write("local.json", "{\"schema\":\"profile/v1\",\"driven\":{" + driven + "},\"automation\":{\"github\":" + github + "}}"));
    }

    [Fact]
    public void The_section_loads_with_its_defaults()
    {
        var github = Load("""{"repositories":{"octo/repo":{"path":"/srv/repos/repo"}},"allowed_authors":["alice"]}""").Automation!.Github!;

        Assert.Equal(("chargehand", "@chargehand", 120, "M", 2, (int?)null), (github.Label, github.Mention, github.PollSeconds, github.BudgetClass, github.MaxConcurrent, github.MaxStartsPerDay));
        Assert.Equal((2_000_000L, 5m, 3), (github.MaxTokensTotal, github.MaxUsdTotal, github.MaxReviewRounds));
        Assert.Equal("/srv/repos/repo", github.Repositories!["octo/repo"].Path);
    }

    [Fact]
    public void An_empty_list_of_allowed_authors_is_valid_and_means_nothing_triggers() =>
        Assert.Empty(Load("""{"repositories":{"octo/repo":{"path":"/p"}},"allowed_authors":[]}""").Automation!.Github!.AllowedAuthors!);

    [Theory]
    [InlineData("""{"repositories":{"octo/repo":{"path":"/p"}}}""", "allowed_authors is required")]
    [InlineData("""{"allowed_authors":["alice"]}""", "repositories must list")]
    [InlineData("""{"repositories":{"octo":{"path":"/p"}},"allowed_authors":["alice"]}""", "must be owner/repo")]
    [InlineData("""{"repositories":{"octo/repo":{"path":""}},"allowed_authors":["alice"]}""", "path must be")]
    [InlineData("""{"repositories":{"octo/repo":{"path":"/p"}},"allowed_authors":["-bad-"]}""", "must be a GitHub login")]
    [InlineData("""{"repositories":{"octo/repo":{"path":"/p"}},"allowed_authors":["a"],"poll_seconds":5}""", "poll_seconds")]
    [InlineData("""{"repositories":{"octo/repo":{"path":"/p"}},"allowed_authors":["a"],"mention":"chargehand"}""", "mention")]
    [InlineData("""{"repositories":{"octo/repo":{"path":"/p"}},"allowed_authors":["a"],"budget_class":"XL"}""", "budget_class")]
    [InlineData("""{"repositories":{"octo/repo":{"path":"/p"}},"allowed_authors":["a"],"max_concurrent":0}""", "max_concurrent")]
    [InlineData("""{"repositories":{"octo/repo":{"path":"/p"}},"allowed_authors":["a"],"max_review_rounds":4}""", "max_review_rounds")]
    [InlineData("""{"repositories":{"octo/repo":{"path":"/p"}},"allowed_authors":["a"],"max_review_rounds":-1}""", "max_review_rounds")]
    public void A_value_that_cannot_work_is_refused_when_the_profile_loads(string github, string problem)
    {
        var e = Assert.Throws<ChargehandException>(() => Load(github));
        Assert.Contains(problem, e.Message);
        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
    }

    [Fact]
    public void The_daily_caps_of_earlier_releases_still_load_are_ignored_and_are_named_once_as_deprecated()
    {
        // ADR 0081: a profile written for 0.19.2 keeps loading, whatever it set.
        using var dir = new TempDir();
        var profile = Profile.Load(dir.Write("local.json", """
            {"schema":"profile/v1","driven":{"enabled":true,"integrator":{"app_id":1,"private_key_secret":"app-key"}},"automation":{
              "github":{"repositories":{"octo/repo":{"path":"/p"}},"allowed_authors":["a"],"max_starts_per_day":0,"max_reviews_per_day":5000},
              "plane":{"url":"https://plane.example.test","api_key_secret":"k","workspace":"w","projects":{"p":{"path":"/p"}},"trigger_state":"Agent","started_state":"In Progress","review_state":"In Review","max_starts_per_day":40}}}
            """));

        var deprecated = profile.Automation!.Deprecations().ToList();
        Assert.Equal(3, deprecated.Count);
        Assert.All(deprecated, d => Assert.Contains("deprecated and ignored since 0.19.4: spend is bounded by automation.account's dollar caps (ADR 0076)", d));
        Assert.Equal(["automation.github.max_starts_per_day", "automation.github.max_reviews_per_day", "automation.plane.max_starts_per_day"], deprecated.Select(d => d[..d.IndexOf(' ')]));
        Assert.Empty(Load("""{"repositories":{"octo/repo":{"path":"/p"}},"allowed_authors":["a"]}""").Automation!.Deprecations());
    }

    [Fact]
    public void The_github_app_needs_no_installation_id_because_each_owners_installation_is_looked_up()
    {
        var github = Load("""{"repositories":{"octo/repo":{"path":"/p"}},"allowed_authors":["a"]}""", driven: "\"enabled\":true,\"integrator\":{\"app_id\":1,\"private_key_secret\":\"app-key\"}").Automation!.Github!;
        Assert.Contains("octo/repo", github.Repositories!.Keys);
    }

    [Fact]
    public void The_section_needs_driven_sessions_and_their_github_app()
    {
        var e = Assert.Throws<ChargehandException>(() => Load("""{"repositories":{"octo/repo":{"path":"/p"}},"allowed_authors":["a"]}""", driven: "\"enabled\":true"));
        Assert.Contains("driven.enabled and the GitHub App of driven.integrator", e.Message);
    }

    [Fact]
    public void An_integrator_for_gitlab_only_has_no_app_to_read_and_comment_with()
    {
        var e = Assert.Throws<ChargehandException>(() => Load("""{"repositories":{"octo/repo":{"path":"/p"}},"allowed_authors":["a"]}""", driven: "\"enabled\":true,\"integrator\":{\"repositories\":{\"group/project\":\"ask\"}}"));
        Assert.Contains("the GitHub App of driven.integrator", e.Message);
    }

    [Theory]
    [InlineData("""{"schema":"profile/v1","automation":{"github":{"repositories":{"o/r":{"path":"/p"}},"allowed_authors":["alice","bot[bot]"],"label":"chargehand","mention":"@chargehand","poll_seconds":120,"budget_class":"S","max_tokens_total":1000000,"max_usd_total":2.5,"max_concurrent":2,"max_starts_per_day":10,"max_review_rounds":0}}}""", true)]
    [InlineData("""{"schema":"profile/v1","automation":{"github":{"repositories":{"o/r":{"path":"/p"}},"allowed_authors":[]}}}""", true)]
    [InlineData("""{"schema":"profile/v1","automation":{"github":{"repositories":{"o/r":{"path":"/p"}}}}}""", false)] // allowed_authors has no default
    [InlineData("""{"schema":"profile/v1","automation":{"github":{"allowed_authors":["a"]}}}""", false)]
    [InlineData("""{"schema":"profile/v1","automation":{"github":{"allowed_authors":["a"],"discover":true}}}""", true)] // the App's repositories
    [InlineData("""{"schema":"profile/v1","automation":{"github":{"allowed_authors":["a"],"discover":false}}}""", false)]
    [InlineData("""{"schema":"profile/v1","automation":{"github":{"repositories":{"o/r":{"path":"/p"}},"allowed_authors":["a"],"poll_seconds":10}}}""", false)]
    [InlineData("""{"schema":"profile/v1","automation":{"github":{"repositories":{"o/r":{"path":"/p"}},"allowed_authors":["a"],"token":"x"}}}""", false)] // the App is driven.integrator
    [InlineData("""{"schema":"profile/v1","automation":{"github":{"repositories":{"o/r":"/p"},"allowed_authors":["a"]}}}""", false)]
    [InlineData("""{"schema":"profile/v1","automation":{"github":{"repositories":{"o/r":{"path":"/p"}},"allowed_authors":["a"],"max_review_rounds":4}}}""", false)] // three rounds at most, like the CI fixer
    public void The_schema_checks_automation_github(string profile, bool valid)
    {
        using var doc = JsonDocument.Parse(profile);
        Assert.Equal(valid, ConfigFileTests.ProfileSchema.Evaluate(doc.RootElement).IsValid);
    }

    [Fact]
    public void The_state_survives_a_save_and_a_load()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "state", "github.json");
        var state = IntakeState.Load(path);
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        state.Handled.Add("comment:octo/repo:5");
        state.ReviewCursor["octo/repo"] = now;
        state.Pending.Add(new PendingReview("r31", "octo/repo", 9, "alice", "Use a constant.", "src/Retry.cs", 12, now));
        state.Pending.Add(new PendingReview("c40", "octo/repo", 9, "alice", "@chargehand go on", null, null, now));
        state.CommentCursor["octo/repo"] = now;
        state.Tasks.Add(new IntakeTask("octo/repo", 7, "run-1", "issue-7", now, "M", IntakePhase.Asked, 99, "https://github.example.test/octo/repo/pull/1", "chargehand/x", 2));
        state.Waiting.Add(new WaitingTrigger("label:octo/repo#8:3", "octo/repo", 8, now, "admission", "account 'a': budget_exceeded"));
        state.Save(now);

        var loaded = IntakeState.Load(path);
        Assert.Equal(["comment:octo/repo:5"], loaded.Handled);
        Assert.Equal(now, loaded.CommentCursor["octo/repo"]);
        Assert.Equal(state.Tasks, loaded.Tasks);
        Assert.Equal(state.Pending, loaded.Pending);
        Assert.Equal(now, loaded.ReviewCursor["octo/repo"]);
        Assert.Equal(state.Waiting, loaded.Waiting);
        Assert.False(File.Exists(path + ".tmp"));
    }
}
