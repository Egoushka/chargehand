using System.Net;
using System.Text.Json;
using Chargehand.Driven;

namespace Chargehand.Tests;

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
    public void The_client_can_open_a_draft_and_do_nothing_else()
    {
        var methods = typeof(GitHubPullRequests).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(m => m.Name).ToList();
        Assert.Equal(["CreateDraftAsync"], methods);
        var all = typeof(GitHubPullRequests).Assembly.GetTypes().Where(t => t.Name.Contains("PullRequest", StringComparison.Ordinal)).SelectMany(t => t.GetMethods()).Select(m => m.Name);
        Assert.DoesNotContain(all, n => n.Contains("Merge", StringComparison.OrdinalIgnoreCase) || n.Contains("AutoMerge", StringComparison.OrdinalIgnoreCase));
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
