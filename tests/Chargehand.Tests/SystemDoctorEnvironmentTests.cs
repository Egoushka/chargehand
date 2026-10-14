using System.Net;
using Chargehand.Config;
using Chargehand.Onboarding;

namespace Chargehand.Tests;

/// <summary>The doctor's real environment, with the network replaced by a handler.</summary>
public class SystemDoctorEnvironmentTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private const string Key = "probe-key-value-77";

    private static Profile ProfileWithKeyFromCommand(TempDir dir) =>
        Profile.Load(dir.Write("p.json", """
            {"schema":"profile/v1","secrets":[{"command":["printf","%s","@@KEY@@"]}],
             "http":{"api_key_secret":"chargehand-api-key","port":4399},
             "driven":{"runner":{"url":"http://runner.example:4310/","api_key_secret":"runner-key"}}}
            """.Replace("@@KEY@@", Key)));

    [Fact]
    public async Task The_server_probe_reads_the_run_list_with_the_bearer_key()
    {
        using var dir = new TempDir();
        var profile = ProfileWithKeyFromCommand(dir);
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var outcome = await new SystemDoctorEnvironment(handler).ProbeServerAsync(profile, profile.Http!, default);

        Assert.Equal(200, outcome.Status);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://127.0.0.1:4399/v1/runs?limit=1", request.RequestUri!.ToString());
        Assert.Equal($"Bearer {Key}", request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task The_runner_probe_reads_count_and_reports_a_refused_key_as_a_status()
    {
        using var dir = new TempDir();
        var profile = ProfileWithKeyFromCommand(dir);
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var outcome = await new SystemDoctorEnvironment(handler).ProbeRunnerAsync(profile, profile.Driven!.Runner!, default);

        Assert.Equal(401, outcome.Status);
        Assert.Equal("http://runner.example:4310/count", Assert.Single(handler.Requests).RequestUri!.ToString());
    }

    [Fact]
    public async Task A_connection_error_is_an_outcome_with_a_reason_not_an_exception()
    {
        using var dir = new TempDir();
        var profile = ProfileWithKeyFromCommand(dir);
        var handler = new Handler(_ => throw new HttpRequestException("Connection refused"));

        var outcome = await new SystemDoctorEnvironment(handler).ProbeServerAsync(profile, profile.Http!, default);

        Assert.Null(outcome.Status);
        Assert.Equal("Connection refused", outcome.Failure);
    }

    [Fact]
    public async Task A_key_that_does_not_resolve_sends_no_request()
    {
        using var dir = new TempDir();
        var profile = Profile.Load(dir.Write("p.json", """{"schema":"profile/v1","secrets":[],"http":{"api_key_secret":"chargehand-api-key"}}"""));
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var outcome = await new SystemDoctorEnvironment(handler).ProbeServerAsync(profile, profile.Http!, default);

        Assert.Null(outcome.Status);
        Assert.Contains("chargehand-api-key", outcome.Failure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_whole_run_prints_item_names_and_never_the_value_the_store_returns()
    {
        using var dir = new TempDir();
        var profile = ProfileWithKeyFromCommand(dir);
        var env = new SystemDoctorEnvironment(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        var results = await Doctor.RunAsync(Path.Combine(dir.Path, "p.json"), env, [], default);

        var text = string.Join('\n', results.Select(Doctor.Format));
        Assert.DoesNotContain(Key, text);
        Assert.Contains("ok   secret chargehand-api-key: present", text);
        Assert.Contains("ok   server: answers at http://127.0.0.1:4399/ and accepts the key", text);
        Assert.NotNull(profile);
    }

    [Fact]
    public void A_secret_that_no_source_resolves_is_false_not_an_exception()
    {
        var profile = new Profile("profile/v1", Secrets: []);

        Assert.False(new SystemDoctorEnvironment().SecretResolves(profile, "nothing-here"));
    }

    [Fact]
    public void A_root_that_is_a_repository_is_one_repository()
    {
        using var dir = new TempDir();
        dir.Write(".git/HEAD", "ref: refs/heads/main\n");
        dir.Write("go.mod", "module x\n");

        var found = Assert.Single(new SystemDoctorEnvironment().Repositories(dir.Path));

        Assert.Equal(["go"], found.Scan.Ecosystems.Select(e => e.Name));
    }

    [Fact]
    public void A_root_of_repositories_lists_each_child_that_is_one_in_name_order()
    {
        using var dir = new TempDir();
        dir.Write("zeta/.git/HEAD", "x");
        dir.Write("zeta/Cargo.toml", "");
        dir.Write("alpha/.git/HEAD", "x");
        dir.Write("alpha/App.sln", "");
        dir.Write("not-a-repo/App.sln", "");

        var found = new SystemDoctorEnvironment().Repositories(dir.Path);

        Assert.Equal(["alpha", "zeta"], found.Select(r => r.Name));
        Assert.Equal(["dotnet"], found[0].Scan.Ecosystems.Select(e => e.Name));
    }

    [Fact]
    public void A_root_with_markers_and_no_git_directory_counts_as_one_repository_and_an_empty_one_as_none()
    {
        using var marked = new TempDir();
        marked.Write("pom.xml", "");
        using var empty = new TempDir();

        Assert.Single(new SystemDoctorEnvironment().Repositories(marked.Path));
        Assert.Empty(new SystemDoctorEnvironment().Repositories(empty.Path));
    }
}
