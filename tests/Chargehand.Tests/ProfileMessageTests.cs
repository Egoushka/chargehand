using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Tests;

/// <summary>What the owner reads when a profile cannot load: every section's message, its problems in order, and the hint, word for word. Written by the
/// reflection-based loader before binding moved to source generation (ADR 0060); a change here is a change users see.</summary>
public class ProfileMessageTests
{
    [Theory]
    [InlineData("accounts", """{"schema":"profile/v1","http":{"api_key_secret":"k","accounts":[{"name":"Bad_Name","max_usd_per_day":0,"repositories":["rel"],"model":{"oauth_token_secret":"t","base_url":"ftp://x"}},{"name":"owner"},{"name":"twin"},{"name":"twin","max_concurrent_runs":0}]}}""",
        """http: accounts.Bad_Name: name must be lowercase letters, digits and hyphens, starting with a letter, at most 32 characters; accounts.Bad_Name: max_usd_per_day must be above 0; accounts.Bad_Name: repositories must be absolute paths; accounts.Bad_Name: model: oauth_token_secret is not allowed for an account: a subscription token is the owner's plan, cannot be priced or capped in dollars, and lending it out spends the owner's limits; use api_key_secret; accounts.Bad_Name: model: api_key_secret is required; accounts.Bad_Name: model: base_url must be an absolute http or https URL; accounts.owner: owner is the account of api_key_secret; pick another name; accounts.twin: max_concurrent_runs must be at least 1; accounts: name 'twin' is used more than once""",
        """Fix http.accounts in the profile; docs/guide/server.md lists the keys.""")]
    [InlineData("admission", """{"schema":"profile/v1","driven":{"admission":{"reserve_mb":-1,"max_load_per_cpu":0,"default_request_mb":10,"max_memory_pressure":0}}}""",
        """driven.admission: reserve_mb must be at least 0; max_load_per_cpu must be above 0; default_request_mb must be at least 512; max_memory_pressure must be above 0 and at most 100""",
        """Fix driven.admission in the profile; docs/guide/driven.md lists the keys.""")]
    [InlineData("automation", """{"schema":"profile/v1","automation":{"github":{}}}""",
        """automation.github: needs driven.enabled and the GitHub App of driven.integrator (app_id, private_key_secret), which reads and comments; repositories must list at least one owner/repo, or discover must be true; allowed_authors is required (an empty list means nothing triggers)""",
        """Fix automation.github in the profile; docs/guide/intake.md lists the keys.""")]
    [InlineData("enhancer", """{"schema":"profile/v1","prompt_enhancer":{"server":"nope"}}""",
        """prompt_enhancer.server 'nope' is not defined in mcp_servers""",
        """Add the server to mcp_servers, or remove prompt_enhancer; docs/guide/reference.md lists the fields.""")]
    [InlineData("forge", """{"schema":"profile/v1","driven":{"enabled":true,"forge":{"kind":"bitbucket","base_url":"http://x.test","token_secret":" "}}}""",
        """driven.forge: kind must be github or gitlab; base_url must be an https URL without credentials, query or fragment; token_secret must name a secret item""",
        """Fix driven.forge in the profile; docs/guide/driven.md lists the keys.""")]
    [InlineData("governor", """{"schema":"profile/v1","driven":{"governor":{"mode":"enforce","repeat_calls":0,"context_tokens":0,"checkpoint_at":2,"budgets":{"X":1,"S":0},"history":{"min_tasks":0,"percentile":101},"probe_patterns":["(","(?=x)"]}}}""",
        """driven.governor: repeat_calls must be at least 1; context_tokens must be at least 1; checkpoint_at must be above 0 and at most 1; budgets.X must be one of S, M, L with a value above 0; budgets.S must be one of S, M, L with a value above 0; history.min_tasks must be at least 1; history.percentile must be 1 to 100; pattern '(' is not a regular expression; pattern '(?=x)' uses a lookaround, backreference or atomic group, which the governor does not match""",
        """Fix driven.governor in the profile; docs/guide/driven.md lists the keys.""")]
    [InlineData("integrator", """{"schema":"profile/v1","driven":{"integrator":{"app_id":0,"installation_id":0,"private_key_secret":"k","repositories":{"bad":"maybe"},"ci_fix":{"max_attempts":99,"max_minutes":0}}}}""",
        """driven.integrator: app_id must be the App's numeric id; installation_id must be the installation's numeric id; repositories key 'bad' must be owner/repo (group/subgroup/project on GitLab); repositories.bad must be merge or ask; ci_fix.max_attempts must be 0 to 3; ci_fix.max_minutes must be 1 to 120""",
        """Fix driven.integrator in the profile; docs/guide/driven.md lists the keys.""")]
    [InlineData("learning", """{"schema":"profile/v1","driven":{"learning":{"retro_preset":"","pr_days":0,"pr_check_minutes":0}}}""",
        """driven.learning: retro_preset must not be empty; pr_days must be at least 1; pr_check_minutes must be at least 1""",
        """Fix driven.learning in the profile; docs/guide/driven.md lists the keys.""")]
    [InlineData("limits", """{"schema":"profile/v1","driven":{"limits":{"max_minutes":0,"memory_mb":10,"cpus":64,"pids":1,"max_continuations":9,"check_retries":6,"goal_reminder_minutes":-1,"goal_reminder_turns":0}}}""",
        """driven.limits: max_minutes must be at least 1; memory_mb must be 256 to 65536; cpus must be 0.1 to 32; pids must be 16 to 4096; max_continuations must be 0 to 5; check_retries must be 0 to 5; goal_reminder_minutes must be 0 (off) or more; goal_reminder_turns must be at least 1""",
        """Fix driven.limits in the profile; docs/guide/driven.md lists the keys.""")]
    [InlineData("mcp", """{"schema":"profile/v1","mcp_servers":{"ok":{"url":"http://ok.test"},"bad":{"url":"http://b.test","command":["x"]}}}""",
        """mcp_servers.bad: set only one of url and command""",
        """Fix the entry in the profile; profiles/example.json shows the shape.""")]
    [InlineData("memory-map", """{"schema":"profile/v1","mcp_servers":{"h":{"url":"http://h.test"}},"memory":[{"name":"Bad","server":"nope","retain":true,"tools":{"recall":{"tool":"","arguments":{"q":"{nope}"}}}},{"name":"Bad","server":"h","tools":{"recall":{"tool":"r","arguments":{}}}}]}""",
        """memory.Bad: the name must be lowercase letters, digits and hyphens, starting with a letter; memory.Bad: server 'nope' is not defined in mcp_servers (defined: h); memory.Bad: recall.tool must name the MCP tool to call; memory.Bad: recall argument 'q' uses {nope}, which recall does not offer (offered: {max_facts}, {namespace}, {query}); memory.Bad: retain is true but tools.retain is not set; a provider without a retain tool is recall-only and retain must stay false; memory.Bad: the name must be lowercase letters, digits and hyphens, starting with a letter; memory.Bad: duplicate name; every memory needs its own""",
        """Fix memory in the profile; docs/guide/reference.md lists the fields.""")]
    [InlineData("memory-object", """{"schema":"profile/v1","memory":{"backend":"hindsight","url":"http://x.test"}}""",
        """memory is a list now: the object form (backend, url, namespace) was removed""",
        """Move url and api_key_secret into mcp_servers, and list the provider under memory with its tools mapping. docs/guide/memory-and-services.md has the Hindsight entry.""")]
    [InlineData("planning", """{"schema":"profile/v1","driven":{"planning":{"preset":" "}}}""",
        """driven.planning: preset must not be empty""",
        """Fix driven.planning in the profile; docs/guide/driven.md lists the keys.""")]
    [InlineData("runners", """{"schema":"profile/v1","driven":{"runners":[{"name":"a","url":"x"},{"name":"a","url":"x"}]}}""",
        """driven: runners.a: url must be an absolute http or https URL; runners.a: api_key_secret is required; runners.a: name is used twice; runners.a: url must be an absolute http or https URL; runners.a: api_key_secret is required""",
        """Fix driven.runners and driven.image_variants in the profile; docs/guide/driven.md lists the keys.""")]
    public void A_section_that_cannot_work_is_refused_with_its_problems_in_order(string section, string profile, string message, string action)
    {
        using var dir = new TempDir();
        var e = Assert.Throws<ChargehandException>(() => Profile.Load(dir.Write($"{section}.json", profile)));
        Assert.Equal((ErrorCode.InvalidRequest, message, action), (e.Code, e.Message, e.Action));
    }

    [Theory]
    [InlineData("badjson", """{"schema":"profile/v1","run_cap_usd":"x"}""",
        """The JSON value could not be converted to Chargehand.Config.Profile. Path: $.run_cap_usd | LineNumber: 0 | BytePositionInLine: 40.""")]
    [InlineData("governor-mode", """{"schema":"profile/v1","driven":{"governor":{"mode":"loud"}}}""",
        """The JSON value could not be converted to Chargehand.Modules.Oversight.Api.DrivenGovernor. Path: $.driven.governor.mode | LineNumber: 0 | BytePositionInLine: 58.""")]
    [InlineData("memory-null", """{"schema":"profile/v1","memory":[null]}""",
        """memory: every entry must be an object""")]
    [InlineData("memory-string", """{"schema":"profile/v1","memory":"x"}""",
        """memory must be a list of providers""")]
    [InlineData("memory-text", """{"schema":"profile/v1","mcp_servers":{"h":{"url":"http://h.test"}},"memory":[{"name":"m","server":"h","tools":{"recall":{"tool":"recall","arguments":{},"results":{"text":5}}}}]}""",
        """expected a string or an array of strings""")]
    [InlineData("workspace", """{"schema":"profile/v1","driven":{"runners":[{"name":"a","url":"http://r.test","api_key_secret":"k","workspace":"shared-ish"}]}}""",
        """The JSON value could not be converted to Chargehand.Modules.Runners.Api.DrivenRunnerEntry. Path: $.driven.runners[0].workspace | LineNumber: 0 | BytePositionInLine: 123.""")]
    public void A_value_of_the_wrong_shape_fails_as_a_json_error_naming_its_path(string section, string profile, string message)
    {
        using var dir = new TempDir();
        Assert.Equal(message, Assert.Throws<JsonException>(() => Profile.Load(dir.Write($"{section}.json", profile))).Message);
    }
}
