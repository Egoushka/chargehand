using System.Net;
using System.Text.Json;
using Chargehand.Modules.Forge.Api;
using Chargehand.Onboarding;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>CHARGEHAND-316: <c>/v1/health?deep=1</c> also asks what the profile configures, so a revoked token or a stopped runner shows before a task fails on it.</summary>
public class DependencyHealthTests
{
    private const string Secret = "ghp_secretvalue";

    private sealed class Canned(Func<HttpRequestMessage, HttpStatusCode> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Auth, string? ApiKey)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), request.Headers.TryGetValues("X-API-Key", out var key) ? key.Single() : null));
            return Task.FromResult(new HttpResponseMessage(answer(request)));
        }
    }

    private sealed class Tokens(Func<IReadOnlyList<string>> accounts) : IInstallationTokens
    {
        public Task<string> GetAsync(string owner, string repository, CancellationToken ct) => throw new InvalidOperationException("a token is not minted by a health check");

        public Task<IReadOnlyList<string>> AccountsAsync(CancellationToken ct) => Task.FromResult(accounts());
    }

    private static DependencyProbe Ok(string name) => new(name, _ => Task.FromResult<string?>(null));

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Deep(params DependencyProbe[] probes)
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), tweak: (_, settings) => settings with { Probes = probes });
        var response = await s.Http.GetAsync("/v1/health?deep=1");
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
    }

    [Fact]
    public async Task Every_configured_check_is_named_in_the_report_and_all_ok_is_200()
    {
        var (status, body) = await Deep(Ok("runner"), Ok("plane key"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["runner", "plane key"], body.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.All(body.GetProperty("checks").EnumerateArray(), c => Assert.True(c.GetProperty("ok").GetBoolean()));
    }

    [Fact]
    public async Task A_failing_configured_check_makes_the_report_503_and_names_the_reason()
    {
        var (status, body) = await Deep(Ok("runner"), DependencyHealth.Plane(new Uri("https://plane.example.test/"), "ws", "p1", () => "plane-key", new Canned(_ => HttpStatusCode.Unauthorized)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("failing", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("probe").GetProperty("ok").GetBoolean());
        var failed = Assert.Single(body.GetProperty("checks").EnumerateArray(), c => !c.GetProperty("ok").GetBoolean());
        Assert.Equal("plane key", failed.GetProperty("name").GetString());
        Assert.Contains("refuses the key", failed.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_configured_checks_the_report_is_the_model_probe_alone()
    {
        var (status, body) = await Deep();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.TryGetProperty("checks", out var checks) && checks.ValueKind == JsonValueKind.Array);
    }

    [Fact]
    public async Task The_default_health_runs_no_check()
    {
        var ran = false;
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply),
            tweak: (_, settings) => settings with { Probes = [new DependencyProbe("runner", _ => { ran = true; return Task.FromResult<string?>(null); })] });

        var live = await s.Http.GetAsync("/v1/health");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.False(ran);
    }

    [Fact]
    public async Task The_checks_run_at_once_and_a_silent_one_is_cut_off_at_its_own_bound()
    {
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        var probes = new[]
        {
            new DependencyProbe("a", async ct => { first.SetResult(); await second.Task.WaitAsync(ct); return null; }),
            new DependencyProbe("b", async ct => { second.SetResult(); await first.Task.WaitAsync(ct); return null; }),
            new DependencyProbe("silent", async ct => { await Task.Delay(Timeout.Infinite, ct); return null; }),
        };

        var checks = await DependencyHealth.RunAsync(probes, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal([true, true, false], checks.Select(c => c.Ok));
        Assert.Equal("no answer within 1 s", checks[2].Reason);
    }

    [Fact]
    public async Task A_check_that_throws_is_a_failing_check_and_not_a_failed_report()
    {
        var checks = await DependencyHealth.RunAsync([new DependencyProbe("boom", _ => throw new HttpRequestException("connection refused"))], TimeSpan.FromSeconds(1), CancellationToken.None);

        var check = Assert.Single(checks);
        Assert.Equal(("boom", false, "connection refused"), (check.Name, check.Ok, check.Reason));
    }

    [Theory]
    [InlineData(200, null, null)]
    [InlineData(401, null, "the runner refuses the key (HTTP 401)")]
    [InlineData(500, null, "the runner answered HTTP 500")]
    [InlineData(null, "connection refused", "the runner does not answer: connection refused")]
    public async Task The_runner_check_reads_what_chargehand_doctor_probes(int? status, string? failure, string? reason)
    {
        var check = (await DependencyHealth.RunAsync([DependencyHealth.Runner("runner", _ => Task.FromResult(new ProbeOutcome(status, failure)))], TimeSpan.FromSeconds(1), CancellationToken.None)).Single();

        Assert.Equal(reason, check.Reason);
        Assert.Equal(reason is null, check.Ok);
    }

    [Fact]
    public async Task The_github_app_check_lists_its_installations_and_fails_when_it_has_none_or_cannot()
    {
        var working = await DependencyHealth.GitHubApp(new Tokens(() => ["octo"])).CheckAsync(CancellationToken.None);
        var empty = await DependencyHealth.GitHubApp(new Tokens(() => [])).CheckAsync(CancellationToken.None);
        var refused = (await DependencyHealth.RunAsync([DependencyHealth.GitHubApp(new Tokens(() => throw new GitHubAppException("GitHub answered 401 to the list of the App's installations: Bad credentials")))],
            TimeSpan.FromSeconds(1), CancellationToken.None)).Single();

        Assert.Null(working);
        Assert.Equal("the App is installed on no account", empty);
        Assert.False(refused.Ok);
        Assert.Contains("401", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_push_token_check_reads_the_user_with_the_token_and_never_reports_it()
    {
        var handler = new Canned(_ => HttpStatusCode.Unauthorized);
        var probe = DependencyHealth.PushToken(new Uri("https://api.example.test/"), () => Secret, handler);

        var check = (await DependencyHealth.RunAsync([probe], TimeSpan.FromSeconds(1), CancellationToken.None)).Single();

        Assert.False(check.Ok);
        Assert.DoesNotContain(Secret, check.Reason, StringComparison.Ordinal);
        var request = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Get, "https://api.example.test/user", $"Bearer {Secret}"), (request.Method, request.Url, request.Auth));

        var good = new Canned(_ => HttpStatusCode.OK);
        Assert.Null(await DependencyHealth.PushToken(new Uri("https://api.example.test/"), () => Secret, good).CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_plane_check_is_one_get_of_the_project_with_the_key_in_the_header()
    {
        var handler = new Canned(_ => HttpStatusCode.OK);

        Assert.Null(await DependencyHealth.Plane(new Uri("https://plane.example.test/"), "ws", "p1", () => "plane-key", handler).CheckAsync(CancellationToken.None));

        var request = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Get, "https://plane.example.test/api/v1/workspaces/ws/projects/p1/", "plane-key"), (request.Method, request.Url, request.ApiKey));
    }

    [Fact]
    public async Task A_key_that_cannot_be_read_fails_its_check_with_the_reason_and_no_secret()
    {
        var probe = DependencyHealth.Plane(new Uri("https://plane.example.test/"), "ws", "p1", () => throw new InvalidOperationException("secret item 'PLANE_KEY' is not set"), new Canned(_ => HttpStatusCode.OK));

        var check = (await DependencyHealth.RunAsync([probe], TimeSpan.FromSeconds(1), CancellationToken.None)).Single();

        Assert.False(check.Ok);
        Assert.Contains("PLANE_KEY", check.Reason, StringComparison.Ordinal);
    }
}
