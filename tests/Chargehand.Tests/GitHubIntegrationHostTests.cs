using System.Net;
using System.Text.Json;
using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary>ADR 0046: the integrator's calls to GitHub, as the App. Every request carries the installation token in its header and nowhere else; every merge is pinned to a head commit and is squash.</summary>
public class GitHubIntegrationHostTests
{
    private static readonly RemoteRepository Repo = new("o", "r");

    private sealed class FixedToken(string token = "ghs_secrettoken") : IInstallationTokens
    {
        public Task<string> GetAsync(string owner, string repository, CancellationToken ct) => Task.FromResult(token);
    }

    private sealed class Canned(Func<HttpRequestMessage, string?, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body, string? Auth)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));
            return answer(request, body);
        }
    }

    private static GitHubIntegrationHost Host(Canned handler, IInstallationTokens? tokens = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") }, tokens ?? new FixedToken());

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };

    private const string StateAnswer = """
        {"data":{"repository":{"pullRequest":{"id":"PR_node","number":7,"state":"OPEN","isDraft":true,"merged":false,"headRefOid":"abc1234","baseRefName":"main","mergeStateStatus":"BLOCKED",
         "autoMergeRequest":null,"comments":{"nodes":[{"body":"hello"},{"body":"<!-- chargehand-ci-fix attempt=1 jobs=0123456789ab -->"}]},
         "commits":{"nodes":[{"commit":{"statusCheckRollup":{"contexts":{"nodes":[
           {"__typename":"CheckRun","name":"build","status":"COMPLETED","conclusion":"FAILURE","databaseId":501,"isRequired":true},
           {"__typename":"CheckRun","name":"lint","status":"IN_PROGRESS","conclusion":null,"databaseId":502,"isRequired":true},
           {"__typename":"CheckRun","name":"docs","status":"COMPLETED","conclusion":"SUCCESS","databaseId":503,"isRequired":false},
           {"__typename":"CheckRun","name":"e2e","status":"COMPLETED","conclusion":"CANCELLED","databaseId":504,"isRequired":true},
           {"__typename":"StatusContext","context":"ci/legacy","state":"PENDING","isRequired":true}]}}}}]}}}}}
        """;

    [Fact]
    public async Task The_state_names_the_head_the_comments_and_each_check_with_whether_it_is_required()
    {
        var handler = new Canned((_, _) => Json(StateAnswer));
        var pr = await Host(handler).GetAsync(Repo, 7, default);

        Assert.Equal(("PR_node", 7, "abc1234", "main", true, false, false, "BLOCKED", false), (pr.NodeId, pr.Number, pr.HeadSha, pr.BaseBranch, pr.IsDraft, pr.Merged, pr.Closed, pr.MergeStatus, pr.AutoMerge));
        Assert.Equal(2, pr.Comments.Count);
        Assert.Equal(
            [("build", true, CheckOutcome.Failed, 501L), ("lint", true, CheckOutcome.Pending, 502L), ("docs", false, CheckOutcome.Passed, 503L), ("e2e", true, CheckOutcome.Other, 504L), ("ci/legacy", true, CheckOutcome.Pending, null)],
            pr.Checks.Select(c => (c.Name, c.Required, c.Outcome, c.JobId)).ToArray());
        var (method, url, body, auth) = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Post, "https://api.example.test/graphql", "Bearer ghs_secrettoken"), (method, url, auth));
        using var sent = JsonDocument.Parse(body!);
        Assert.Equal(7, sent.RootElement.GetProperty("variables").GetProperty("number").GetInt32());
        Assert.DoesNotContain("ghs_secrettoken", body);
    }

    [Fact]
    public async Task A_pull_request_without_checks_has_none()
    {
        var handler = new Canned((_, _) => Json("""
            {"data":{"repository":{"pullRequest":{"id":"N","number":1,"state":"MERGED","isDraft":false,"merged":true,"headRefOid":"a","baseRefName":"main","mergeStateStatus":"UNKNOWN",
             "autoMergeRequest":{"enabledAt":"2026-10-13T10:00:00Z"},"comments":{"nodes":[]},"commits":{"nodes":[{"commit":{"statusCheckRollup":null}}]}}}}}
            """));
        var pr = await Host(handler).GetAsync(Repo, 1, default);
        Assert.Empty(pr.Checks);
        Assert.True(pr.Merged);
        Assert.True(pr.AutoMerge);
    }

    [Fact]
    public async Task Marking_ready_and_auto_merge_are_graphql_mutations_and_auto_merge_is_squash_pinned_to_the_head()
    {
        var handler = new Canned((_, _) => Json("""{"data":{"ok":{}}}"""));
        var host = Host(handler);
        await host.MarkReadyAsync(Repo, "PR_node", default);
        await host.EnableAutoMergeAsync(Repo, "PR_node", "abc1234", default);

        Assert.All(handler.Requests, r => Assert.Equal("https://api.example.test/graphql", r.Url));
        Assert.Contains("markPullRequestReadyForReview", handler.Requests[0].Body);
        using var merge = JsonDocument.Parse(handler.Requests[1].Body!);
        var query = merge.RootElement.GetProperty("query").GetString()!;
        Assert.Contains("enablePullRequestAutoMerge", query);
        Assert.Contains("mergeMethod:SQUASH", query);
        Assert.Contains("expectedHeadOid", query);
        Assert.Equal("abc1234", merge.RootElement.GetProperty("variables").GetProperty("oid").GetString());
    }

    [Fact]
    public async Task A_merge_is_a_squash_of_the_pinned_head_and_the_other_calls_use_the_expected_routes()
    {
        var handler = new Canned((request, _) => request.RequestUri!.AbsolutePath.Contains("/compare/", StringComparison.Ordinal)
            ? Json("""{"behind_by":3,"ahead_by":1}""") : Json("{}", HttpStatusCode.Accepted));
        var host = Host(handler);
        Assert.Equal(3, await host.BehindByAsync(Repo, "main", "chargehand/run1", default));
        await host.UpdateBranchAsync(Repo, 7, "abc1234", default);
        await host.MergeNowAsync(Repo, 7, "abc1234", default);
        await host.CommentAsync(Repo, 7, "hello", default);

        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("https://api.example.test/repos/o/r/compare/main...chargehand%2Frun1", handler.Requests[0].Url);
        Assert.Equal((HttpMethod.Put, "https://api.example.test/repos/o/r/pulls/7/update-branch"), (handler.Requests[1].Method, handler.Requests[1].Url));
        Assert.Contains("\"expected_head_sha\":\"abc1234\"", handler.Requests[1].Body);
        Assert.Equal((HttpMethod.Put, "https://api.example.test/repos/o/r/pulls/7/merge"), (handler.Requests[2].Method, handler.Requests[2].Url));
        Assert.Contains("\"merge_method\":\"squash\"", handler.Requests[2].Body);
        Assert.Contains("\"sha\":\"abc1234\"", handler.Requests[2].Body);
        Assert.Equal((HttpMethod.Post, "https://api.example.test/repos/o/r/issues/7/comments"), (handler.Requests[3].Method, handler.Requests[3].Url));
        Assert.All(handler.Requests, r => Assert.Equal("Bearer ghs_secrettoken", r.Auth));
        Assert.All(handler.Requests.Skip(1), r => Assert.DoesNotContain("ghs_secrettoken", r.Body));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_refusal_is_an_integration_exception_with_the_status_and_without_the_token(HttpStatusCode status)
    {
        var handler = new Canned((_, _) => Json("""{"message":"Head branch was modified. ghs_secrettoken"}""", status));
        var e = await Assert.ThrowsAsync<IntegrationException>(() => Host(handler).MergeNowAsync(Repo, 7, "abc1234", default));
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), e.Message);
        Assert.DoesNotContain("ghs_secrettoken", e.Message);
    }

    [Fact]
    public async Task A_graphql_error_is_an_integration_exception()
    {
        var handler = new Canned((_, _) => Json("""{"data":null,"errors":[{"message":"Pull request is in clean status"}]}"""));
        var e = await Assert.ThrowsAsync<IntegrationException>(() => Host(handler).EnableAutoMergeAsync(Repo, "PR_node", "abc1234", default));
        Assert.Contains("clean status", e.Message);
    }

    [Fact]
    public async Task A_token_that_cannot_be_made_is_an_integration_exception_and_nothing_is_sent()
    {
        var handler = new Canned((_, _) => Json("{}"));
        var e = await Assert.ThrowsAsync<IntegrationException>(() => Host(handler, new Failing()).CommentAsync(Repo, 7, "x", default));
        Assert.Contains("no key", e.Message);
        Assert.Empty(handler.Requests);
    }

    private sealed class Failing : IInstallationTokens
    {
        public Task<string> GetAsync(string owner, string repository, CancellationToken ct) => throw new GitHubAppException("no key");
    }

    /// <summary>An App installed on some owners only, each with a token of its own.</summary>
    private sealed class PerOwner(params string[] installedOn) : IInstallationTokens
    {
        public List<(string Owner, string Repository)> Asked { get; } = [];

        public Task<string> GetAsync(string owner, string repository, CancellationToken ct)
        {
            Asked.Add((owner, repository));
            return installedOn.Contains(owner, StringComparer.OrdinalIgnoreCase)
                ? Task.FromResult($"ghs_{owner}")
                : throw new GitHubAppException($"the GitHub App is not installed on {owner} (or has no access to {owner}/{repository}); install it there, or set the repository to off");
        }
    }

    [Fact]
    public async Task Every_call_acts_as_the_installation_on_the_owner_of_its_repository()
    {
        var handler = new Canned((request, _) => request.RequestUri!.AbsolutePath == "/graphql" ? Json("""{"data":{"ok":{}}}""") : Json("""{"behind_by":0}"""));
        var tokens = new PerOwner("o", "acme-org");
        var host = Host(handler, tokens);
        var other = new RemoteRepository("acme-org", "mobile");

        await host.MarkReadyAsync(other, "PR_other", default);
        await host.EnableAutoMergeAsync(other, "PR_other", "abc1234", default);
        await host.BehindByAsync(other, "main", "chargehand/run1", default);
        await host.CommentAsync(Repo, 7, "x", default);
        await host.MarkReadyAsync(Repo, "PR_node", default);

        Assert.Equal(["Bearer ghs_acme-org", "Bearer ghs_acme-org", "Bearer ghs_acme-org", "Bearer ghs_o", "Bearer ghs_o"], handler.Requests.Select(r => r.Auth));
        Assert.Equal([("acme-org", "mobile"), ("acme-org", "mobile"), ("acme-org", "mobile"), ("o", "r"), ("o", "r")], tokens.Asked);
    }

    [Fact]
    public async Task A_repository_the_app_is_not_installed_on_is_said_so_and_nothing_is_sent_to_github()
    {
        var handler = new Canned((_, _) => Json("{}"));
        var host = Host(handler, new PerOwner("o"));
        var e = await Assert.ThrowsAsync<IntegrationException>(() => host.MarkReadyAsync(new RemoteRepository("acme-org", "mobile"), "PR_node", default));

        Assert.Contains("the GitHub App is not installed on acme-org", e.Message);
        Assert.DoesNotContain("Resource not accessible", e.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_job_log_is_read_from_the_redirect_without_the_token_and_cut_to_its_end()
    {
        var log = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"2026-10-13T09:00:{i % 60:00}.1234567Z \u001b[31mline {i} of the failing test run\u001b[0m"));
        var handler = new Canned((request, _) => request.RequestUri!.Host == "api.example.test"
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://blob.example.test/logs/501?sig=abc") } }
            : Json(log));
        var tail = await Host(handler).JobLogTailAsync(Repo, 501, 500, default);

        Assert.Equal("https://api.example.test/repos/o/r/actions/jobs/501/logs", handler.Requests[0].Url);
        Assert.Equal("Bearer ghs_secrettoken", handler.Requests[0].Auth);
        Assert.Equal("https://blob.example.test/logs/501?sig=abc", handler.Requests[1].Url);
        Assert.Null(handler.Requests[1].Auth);
        Assert.True(tail.Length <= 500);
        Assert.EndsWith("line 200 of the failing test run", tail);
        Assert.StartsWith("line ", tail);
        Assert.DoesNotContain('\u001b', tail);
        Assert.DoesNotContain("2026-10-13T", tail);
    }

    [Fact]
    public void A_short_log_is_returned_whole()
    {
        Assert.Equal("a\nb", GitHubIntegrationHost.TailOf("2026-10-13T09:00:00.1Z a\r\n2026-10-13T09:00:01.1Z b\n", 100));
    }
}
