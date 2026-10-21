using System.Net;
using System.Text.Json;
using Chargehand.Modules.Forge.Api;

namespace Chargehand.Modules.Forge.Tests;

/// <summary>The one thing chargehand does on a code host: open a draft pull request (ADR 0039). It has no way to merge, and no way to open anything else.</summary>
public class GitHubPullRequestsTests
{
    private sealed class Recorder(HttpStatusCode status = HttpStatusCode.Created, string body = """{"html_url":"https://example.test/o/r/pull/7","number":7}""") : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body, string? Auth)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(ct), request.Headers.Authorization?.ToString()));
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private static GitHubPullRequests Client(Recorder handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") }, "the-token");

    [Fact]
    public async Task A_draft_is_opened_once_with_draft_true_and_the_token_in_the_header_only()
    {
        var handler = new Recorder();
        var pr = await Client(handler).CreateDraftAsync(new RemoteRepository("o", "r"), "chargehand/run1", "main", "Add a retry", "Body text", default);
        Assert.Equal(new PullRequestRef("https://example.test/o/r/pull/7", 7), pr);
        var (method, url, body, auth) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("https://api.example.test/repos/o/r/pulls", url);
        Assert.Equal("Bearer the-token", auth);
        using var doc = JsonDocument.Parse(body!);
        Assert.True(doc.RootElement.GetProperty("draft").GetBoolean());
        Assert.Equal("chargehand/run1", doc.RootElement.GetProperty("head").GetString());
        Assert.Equal("main", doc.RootElement.GetProperty("base").GetString());
        Assert.DoesNotContain("the-token", body);
    }

    [Fact]
    public async Task A_token_source_is_asked_at_each_call_and_one_that_fails_is_a_pull_request_failure_without_a_token()
    {
        // CHARGEHAND-247: the App's installation token lapses after an hour, so a long batch asks for it at each call.
        var handler = new Recorder();
        var asked = 0;
        var client = new GitHubPullRequests(new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") }, _ => Task.FromResult($"ghs_{++asked}"));
        await client.CreateDraftAsync(new RemoteRepository("o", "r"), "chargehand/run1", "main", "Add a retry", "Body", default);
        await client.EditAsync(new RemoteRepository("o", "r"), 7, "Add a retry", "Body", default);
        Assert.Equal(["Bearer ghs_1", "Bearer ghs_2"], handler.Requests.Select(r => r.Auth));

        var failing = new GitHubPullRequests(new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") }, _ => throw new GitHubAppException("the GitHub App is not installed on o"));
        var e = await Assert.ThrowsAsync<PullRequestException>(() => failing.CreateDraftAsync(new RemoteRepository("o", "r"), "chargehand/run1", "main", "t", "b", default));
        Assert.Equal("no credential for the pull request: the GitHub App is not installed on o", e.Message);
        Assert.Null(await failing.FindByHeadAsync(new RemoteRepository("o", "r"), "chargehand/run1", default));
        Assert.Equal(ChecksGate.Unknown, await failing.ChecksGateAsync(new RemoteRepository("o", "r"), "main", default));
        Assert.Equal(2, handler.Requests.Count);
    }

    private sealed class Sequence : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString()));
            return Task.FromResult(request.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new StringContent("""{"message":"Validation Failed","errors":[{"message":"A pull request already exists for o:chargehand/run1."}]}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""[{"html_url":"https://example.test/o/r/pull/3","number":3}]""") });
        }
    }

    [Fact]
    public async Task A_pull_request_that_already_exists_is_returned_not_failed()
    {
        var handler = new Sequence();
        var pr = await new GitHubPullRequests(new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") }, "the-token")
            .CreateDraftAsync(new RemoteRepository("o", "r"), "chargehand/run1", "main", "Add a retry", "Body", default);

        Assert.Equal(new PullRequestRef("https://example.test/o/r/pull/3", 3, Reused: true), pr);
        Assert.Equal("https://api.example.test/repos/o/r/pulls?head=o%3Achargehand%2Frun1&state=open", handler.Requests[1].Url);
    }

    [Fact]
    public void The_client_can_open_a_draft_edit_its_title_find_one_by_branch_and_read_branch_rules_and_nothing_else()
    {
        var methods = typeof(GitHubPullRequests).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(m => m.Name).ToList();
        Assert.Equal(["ChecksGateAsync", "CreateDraftAsync", "EditAsync", "FindByHeadAsync"], methods.Order(StringComparer.Ordinal));
        var all = typeof(GitHubPullRequests).Assembly.GetTypes().Where(t => t.Name.Contains("PullRequest", StringComparison.Ordinal)).SelectMany(t => t.GetMethods()).Select(m => m.Name);
        Assert.DoesNotContain(all, n => n.Contains("Merge", StringComparison.OrdinalIgnoreCase) || n.Contains("AutoMerge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_title_and_body_are_patched_on_the_pull_request_with_the_token_in_the_header_only()
    {
        var handler = new Recorder(HttpStatusCode.OK, "{}");
        await Client(handler).EditAsync(new RemoteRepository("o", "r"), 7, "Add a retry", "Done.", default);

        var (method, url, body, auth) = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Patch, "https://api.example.test/repos/o/r/pulls/7", "Bearer the-token"), (method, url, auth));
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal(["title", "body"], doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(("Add a retry", "Done."), (doc.RootElement.GetProperty("title").GetString(), doc.RootElement.GetProperty("body").GetString()));
        Assert.DoesNotContain("the-token", body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_refused_edit_is_a_pull_request_exception_without_the_token(HttpStatusCode status)
    {
        var handler = new Recorder(status, """{"message":"Bad credentials the-token"}""");
        var e = await Assert.ThrowsAsync<PullRequestException>(() => Client(handler).EditAsync(new RemoteRepository("o", "r"), 7, "t", "b", default));
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), e.Message);
        Assert.DoesNotContain("the-token", e.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_refusal_is_a_pull_request_exception_without_the_token(HttpStatusCode status)
    {
        var handler = new Recorder(status, """{"message":"Bad credentials the-token"}""");
        var e = await Assert.ThrowsAsync<PullRequestException>(() => Client(handler).CreateDraftAsync(new RemoteRepository("o", "r"), "b", "main", "t", "b", default));
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), e.Message);
        Assert.DoesNotContain("the-token", e.Message);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo.git", "owner", "repo")]
    [InlineData("https://github.com/owner/repo", "owner", "repo")]
    [InlineData("git@github.com:owner/repo.git", "owner", "repo")]
    [InlineData("ssh://git@github.com/owner/repo.git", "owner", "repo")]
    public void A_github_remote_url_names_its_owner_and_repository(string url, string owner, string repo) =>
        Assert.Equal(new RemoteRepository(owner, repo), RemoteRepository.Parse(url));

    [Theory]
    [InlineData("https://example.test/only-one")]
    [InlineData("file:///tmp/x.git")]
    [InlineData("/tmp/x.git")]
    [InlineData("https://github.com/o/r/extra/segments")]
    [InlineData("")]
    public void Anything_else_is_not_a_remote_we_open_pull_requests_on(string url) => Assert.Null(RemoteRepository.Parse(url));
}
