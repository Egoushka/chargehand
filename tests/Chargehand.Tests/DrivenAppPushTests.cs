using System.Globalization;
using System.Net;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Tests;

/// <summary>CHARGEHAND-247: without <c>driven.push_secret</c> a batch on a GitHub repository the App is installed on pushes with the App's installation token, asked for at each use.
/// <c>driven.push_secret</c> wins when it is set. GitHub is a fake host; nothing reaches a network.</summary>
public class DrivenAppPushTests
{
    private const string Commit = "abc1234def5678abc1234def5678abc1234def56";

    private static string Token(TaskGrant g) => $"tok-{g.RunId}";

    private static RunRequest One() =>
        new("request/v1", "Run the tasks.", new RequestContext(false, "driven", Repository: new RepositoryRef("/srv/repo", Commit)),
            Driven: new RequestDriven([new DrivenTask("t1", Goal: "Add a retry")], MaxParallel: 1, MaxTokensTotal: 10_000_000, MaxUsdTotal: 5m));

    /// <summary>GitHub's App endpoints: an installation for each owner in <paramref name="installedOn"/>, and a new token at each exchange. Each token expires within
    /// <see cref="GitHubInstallationTokens.Margin"/>, so the token source never keeps one: every call is a fresh mint.</summary>
    private sealed class FakeGitHub(params string[] installedOn) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        public List<string> Issued { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            var parts = path.Trim('/').Split('/');
            if (parts is ["repos", var owner, _, "installation"] && installedOn.Contains(owner))
                return Task.FromResult(Json($$$"""{"id":5,"account":{"login":"{{{owner}}}"}}""", HttpStatusCode.OK));
            if (parts is ["app", "installations", "5", "access_tokens"])
            {
                var token = $"ghs_app_token_{Issued.Count + 1}";
                Issued.Add(token);
                var expires = DateTimeOffset.UtcNow.AddMinutes(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                return Task.FromResult(Json($$"""{"token":"{{token}}","expires_at":"{{expires}}"}""", HttpStatusCode.Created));
            }
            return Task.FromResult(Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status) => new(status) { Content = new StringContent(body) };
    }

    private sealed class App : IDisposable
    {
        private readonly System.Security.Cryptography.RSA _rsa = System.Security.Cryptography.RSA.Create(2048);
        private readonly GitHubInstallationTokens _tokens;

        public App(params string[] installedOn)
        {
            Host = new FakeGitHub(installedOn);
            _tokens = new GitHubInstallationTokens(new HttpClient(Host) { BaseAddress = new Uri("https://api.example.test/") }, 1, null, () => _rsa.ExportRSAPrivateKeyPem(), TimeProvider.System);
        }

        public FakeGitHub Host { get; }

        public Func<RemoteRepository, CancellationToken, Task<string>> Mint => (remote, ct) => _tokens.GetAsync(remote.Owner, remote.Name, ct);

        public void Dispose()
        {
            _tokens.Dispose();
            _rsa.Dispose();
        }
    }

    [Fact]
    public async Task Without_a_push_secret_the_app_pushes_with_a_token_minted_at_each_use_that_no_result_or_log_shows()
    {
        using var app = new App("o");
        using var rig = new DrivenRunTests.Rig(pushSecret: null, appToken: app.Mint);

        var result = await rig.Run.RunAsync(One(), "run-batch-app", Token, default);

        Assert.Equal(ResultStatus.Completed, result.Status);
        // One token at the batch's start shows the App is installed on the repository, before any session runs.
        Assert.Equal(["ghs_app_token_1"], app.Host.Issued);
        Assert.Equal("/repos/o/r/installation", app.Host.Paths[0]);
        var credential = Assert.Single(rig.Handover.Inputs).Credential!;
        Assert.Equal("", credential.Value);
        Assert.Equal("ghs_app_token_2", await credential.TokenAsync(default));
        Assert.Equal("ghs_app_token_3", await credential.TokenAsync(default));

        var log = await File.ReadAllTextAsync(Path.Combine(rig.Dir.Path, "log.jsonl"));
        Assert.DoesNotContain("ghs_app_token", log);
        Assert.DoesNotContain("ghs_app_token", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("ghs_app_token", JsonSerializer.Serialize(rig.Engine.Started));
    }

    [Fact]
    public async Task A_push_secret_wins_over_the_app()
    {
        using var app = new App("o");
        using var rig = new DrivenRunTests.Rig(appToken: app.Mint);

        var result = await rig.Run.RunAsync(One(), "run-batch-secret", Token, default);

        Assert.Equal(ResultStatus.Completed, result.Status);
        var credential = Assert.Single(rig.Handover.Inputs).Credential!;
        Assert.Equal("value-of-secret-push", credential.Value);
        Assert.Null(credential.Mint);
        Assert.Empty(app.Host.Paths);
    }

    [Fact]
    public async Task Without_a_push_secret_or_an_app_the_batch_is_refused_and_says_how_to_fix_it()
    {
        using var rig = new DrivenRunTests.Rig(pushSecret: null);

        var result = await rig.Run.RunAsync(One(), "run-batch-none", Token, default);

        Assert.Equal(ErrorCode.CredentialUnavailable, result.Error!.Code);
        Assert.Contains("driven.push_secret is not set and there is no GitHub App", result.Error.Message);
        Assert.Contains("driven.integrator", result.Error.Action);
        Assert.Empty(rig.Engine.Calls);
    }

    [Fact]
    public async Task A_repository_the_app_is_not_installed_on_is_refused_before_any_container_exists()
    {
        using var app = new App("someone-else");
        using var rig = new DrivenRunTests.Rig(pushSecret: null, appToken: app.Mint);

        var result = await rig.Run.RunAsync(One(), "run-batch-uninstalled", Token, default);

        Assert.Equal(ErrorCode.CredentialUnavailable, result.Error!.Code);
        Assert.Contains("the GitHub App cannot push to o/r", result.Error.Message);
        Assert.Empty(app.Host.Issued);
        Assert.Empty(rig.Engine.Calls);
    }

    [Fact]
    public async Task The_app_does_not_push_to_a_remote_that_is_not_on_github()
    {
        using var app = new App("o");
        using var rig = new DrivenRunTests.Rig(pushSecret: null, appToken: app.Mint, remote: "https://gitlab.com/o/r.git");

        var result = await rig.Run.RunAsync(One(), "run-batch-gitlab", Token, default);

        Assert.Equal(ErrorCode.CredentialUnavailable, result.Error!.Code);
        Assert.Contains("only to a GitHub remote", result.Error.Message);
        Assert.Empty(app.Host.Paths);
        Assert.Empty(rig.Engine.Calls);
    }
}
