using System.Net;
using System.Text.Json;
using Chargehand.Automation;
using Chargehand.Driven;
using Chargehand.Modules.Forge.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0051: intake's calls to GitHub, as the App. They read issues, events and comments and write comments; the installation token is in the header and nowhere else.</summary>
public class GitHubIntakeHostTests
{
    private sealed class FixedToken : IInstallationTokens
    {
        public Task<string> GetAsync(string owner, string repository, CancellationToken ct) => Task.FromResult("ghs_secrettoken");

        public Task<IReadOnlyList<string>> AccountsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["octo", "acme"]);
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

    private static GitHubIntakeHost Host(Canned handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") }, new FixedToken());

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };

    [Fact]
    public async Task Labelled_issues_are_the_open_ones_with_the_label_and_a_pull_request_says_so()
    {
        var handler = new Canned((_, _) => Json("""
            [{"number":7,"title":"Add a retry","body":"The client gives up.","user":{"login":"bob"},"labels":[{"name":"chargehand"},{"name":"size:S"}],"updated_at":"2026-10-13T10:00:00Z"},
             {"number":8,"title":"A change","body":null,"user":null,"labels":[],"updated_at":"2026-10-13T11:00:00Z","pull_request":{"url":"x"}}]
            """));
        var issues = await Host(handler).LabeledAsync("octo/repo", "chargehand", default);

        Assert.Equal(["7:Add a retry:bob:False:chargehand,size:S", "8:A change::True:"], issues.Select(i => $"{i.Number}:{i.Title}:{i.Author}:{i.IsPullRequest}:{string.Join(',', i.Labels)}"));
        Assert.Equal("", issues[1].Body);
        var (method, url, _, auth) = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Get, "https://api.example.test/repos/octo/repo/issues?state=open&labels=chargehand&per_page=100&page=1", "Bearer ghs_secrettoken"), (method, url, auth));
    }

    [Fact]
    public async Task A_full_page_is_followed_by_the_next_one()
    {
        var handler = new Canned((request, _) => Json(request.RequestUri!.Query.EndsWith("&page=1", StringComparison.Ordinal)
            ? "[" + string.Join(',', Enumerable.Range(1, 100).Select(n => $$"""{"number":{{n}},"title":"t","body":"","user":{"login":"a"},"labels":[],"updated_at":"2026-10-13T10:00:00Z"}""")) + "]"
            : """[{"number":101,"title":"t","body":"","user":{"login":"a"},"labels":[],"updated_at":"2026-10-13T10:00:00Z"}]"""));
        var issues = await Host(handler).LabeledAsync("octo/repo", "chargehand", default);

        Assert.Equal(101, issues.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("&page=2", handler.Requests[1].Url);
    }

    [Fact]
    public async Task The_label_event_is_the_latest_one_that_applied_this_label()
    {
        var handler = new Canned((_, _) => Json("""
            [{"id":1,"event":"labeled","label":{"name":"chargehand"},"actor":{"login":"alice"}},
             {"id":2,"event":"labeled","label":{"name":"bug"},"actor":{"login":"carol"}},
             {"id":3,"event":"unlabeled","label":{"name":"chargehand"},"actor":{"login":"alice"}},
             {"id":4,"event":"labeled","label":{"name":"Chargehand"},"actor":{"login":"bob"}},
             {"id":5,"event":"commented","actor":{"login":"dave"}}]
            """));
        var applied = await Host(handler).LastLabelEventAsync("octo/repo", 7, "chargehand", default);

        Assert.Equal(new LabelEvent(4, "bob"), applied);
        Assert.StartsWith("https://api.example.test/repos/octo/repo/issues/7/events?", handler.Requests.Single().Url);
    }

    [Fact]
    public async Task Comments_are_asked_for_from_a_time_oldest_first_and_carry_their_issue()
    {
        var handler = new Canned((_, _) => Json("""
            [{"id":11,"issue_url":"https://api.example.test/repos/octo/repo/issues/7","user":{"login":"alice"},"body":"@chargehand go","updated_at":"2026-10-13T10:00:05Z"},
             {"id":12,"issue_url":"https://api.example.test/repos/octo/repo/issues/9","user":null,"body":null,"updated_at":"2026-10-13T10:00:06Z"}]
            """));
        var comments = await Host(handler).CommentsSinceAsync("octo/repo", new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.Zero), default);

        Assert.Equal(["11:7:alice:@chargehand go", "12:9::"], comments.Select(c => $"{c.Id}:{c.IssueNumber}:{c.Author}:{c.Body}"));
        Assert.Equal("https://api.example.test/repos/octo/repo/issues/comments?sort=created&direction=asc&since=2026-10-13T10%3A00%3A00Z&per_page=100&page=1", handler.Requests.Single().Url);
    }

    [Fact]
    public async Task A_pull_request_names_its_head_branch_and_where_it_lives_and_whether_it_is_open()
    {
        var handler = new Canned((_, _) => Json("""
            {"number":9,"state":"open","head":{"ref":"chargehand/run-1","repo":{"full_name":"octo/repo"}},"base":{"ref":"main","repo":{"full_name":"octo/repo"}}}
            """));
        var pull = await Host(handler).GetPullAsync("octo/repo", 9, default);

        Assert.Equal(new IntakePull(9, "chargehand/run-1", "octo/repo", "octo/repo", true), pull);
        Assert.Equal("https://api.example.test/repos/octo/repo/pulls/9", handler.Requests.Single().Url);

        var gone = new Canned((_, _) => Json("""{"number":9,"state":"closed","head":{"ref":"x","repo":null},"base":{"ref":"main","repo":{"full_name":"octo/repo"}}}"""));
        Assert.Equal(new IntakePull(9, "x", null, "octo/repo", false), await Host(gone).GetPullAsync("octo/repo", 9, default));
    }

    [Fact]
    public async Task Review_comments_carry_their_pull_request_file_line_and_whether_they_reply()
    {
        var handler = new Canned((_, _) => Json("""
            [{"id":31,"pull_request_url":"https://api.example.test/repos/octo/repo/pulls/9","user":{"login":"alice"},"body":"Use a constant.","path":"src/Retry.cs","line":12,"original_line":10,"updated_at":"2026-10-13T10:00:05Z"},
             {"id":32,"pull_request_url":"https://api.example.test/repos/octo/repo/pulls/9","user":{"login":"bob"},"body":"Why?","path":"src/Retry.cs","line":null,"original_line":7,"in_reply_to_id":31,"updated_at":"2026-10-13T10:00:06Z"},
             {"id":33,"pull_request_url":"https://api.example.test/repos/octo/repo/pulls/9","user":null,"body":null,"path":"README.md","line":null,"updated_at":"2026-10-13T10:00:07Z"}]
            """));
        var comments = await Host(handler).ReviewCommentsSinceAsync("octo/repo", new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.Zero), default);

        Assert.Equal(["31:9:alice:src/Retry.cs:12:False", "32:9:bob:src/Retry.cs:7:True", "33:9::README.md::False"], comments.Select(c => $"{c.Id}:{c.PullNumber}:{c.Author}:{c.Path}:{c.Line}:{c.IsReply}"));
        Assert.Equal("https://api.example.test/repos/octo/repo/pulls/comments?sort=created&direction=asc&since=2026-10-13T10%3A00%3A00Z&per_page=100&page=1", handler.Requests.Single().Url);
    }

    [Fact]
    public async Task The_conversation_of_a_pull_request_is_its_comment_bodies_in_order()
    {
        var handler = new Canned((_, _) => Json("""[{"id":1,"body":"first"},{"id":2,"body":null},{"id":3,"body":"third"}]"""));
        var bodies = await Host(handler).CommentBodiesAsync("octo/repo", 9, default);

        Assert.Equal(["first", "", "third"], bodies);
        Assert.StartsWith("https://api.example.test/repos/octo/repo/issues/9/comments?", handler.Requests.Single().Url);
    }

    [Fact]
    public async Task A_comment_is_posted_as_json_and_its_id_comes_back()
    {
        var handler = new Canned((_, _) => Json("""{"id":99,"body":"hi"}""", HttpStatusCode.Created));
        var id = await Host(handler).CommentAsync("octo/repo", 7, "hello \"there\"", default);

        Assert.Equal(99, id);
        var (method, url, body, auth) = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Post, "https://api.example.test/repos/octo/repo/issues/7/comments", "Bearer ghs_secrettoken"), (method, url, auth));
        Assert.Equal("hello \"there\"", JsonDocument.Parse(body!).RootElement.GetProperty("body").GetString());
        Assert.DoesNotContain("ghs_secrettoken", body);
    }

    [Fact]
    public async Task A_refusal_carries_its_status_and_never_the_token()
    {
        var handler = new Canned((_, _) => Json("""{"message":"Resource not accessible; token ghs_secrettoken"}""", HttpStatusCode.Forbidden));
        var e = await Assert.ThrowsAsync<IntakeException>(() => Host(handler).CommentAsync("octo/repo", 7, "x", default));

        Assert.Equal(403, e.Status);
        Assert.Contains("403", e.Message);
        Assert.DoesNotContain("ghs_secrettoken", e.Message);
    }

    [Fact]
    public async Task A_body_that_is_not_what_intake_reads_is_an_error_not_a_crash()
    {
        var handler = new Canned((_, _) => Json("""{"not":"a list"}"""));
        await Assert.ThrowsAsync<IntakeException>(() => Host(handler).LabeledAsync("octo/repo", "chargehand", default));
        var bad = new Canned((_, _) => Json("""[{"number":"seven"}]"""));
        await Assert.ThrowsAsync<IntakeException>(() => Host(bad).LabeledAsync("octo/repo", "chargehand", default));
    }

    [Fact]
    public async Task The_installed_repositories_are_read_for_every_account_the_App_is_installed_on()
    {
        var handler = new Canned((request, _) => Json(request.RequestUri!.Query.Contains("page=1", StringComparison.Ordinal)
            ? """{"total_count":2,"repositories":[{"full_name":"octo/repo","clone_url":"https://github.example.test/octo/repo.git","archived":false},{"full_name":"octo/old","clone_url":"https://github.example.test/octo/old.git","archived":true}]}"""
            : """{"repositories":[]}"""));
        var repositories = await Host(handler).InstalledRepositoriesAsync(default);

        Assert.Equal(["octo/repo:False", "octo/old:True", "octo/repo:False", "octo/old:True"], repositories.Select(r => $"{r.FullName}:{r.Archived}"));
        Assert.All(handler.Requests, r => Assert.Equal("https://api.example.test/installation/repositories?per_page=100&page=1", r.Url));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_missing_label_is_created_and_one_that_is_there_is_left_alone()
    {
        var missing = new Canned((request, _) => request.Method == HttpMethod.Get ? Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound) : Json("{}", HttpStatusCode.Created));
        Assert.True(await Host(missing).EnsureLabelAsync("octo/repo", "chargehand", default));
        Assert.Equal(["GET https://api.example.test/repos/octo/repo/labels/chargehand", "POST https://api.example.test/repos/octo/repo/labels"], missing.Requests.Select(r => $"{r.Method} {r.Url}"));
        Assert.Equal("chargehand", JsonDocument.Parse(missing.Requests[1].Body!).RootElement.GetProperty("name").GetString());

        var there = new Canned((_, _) => Json("""{"name":"chargehand"}"""));
        Assert.False(await Host(there).EnsureLabelAsync("octo/repo", "chargehand", default));
        Assert.Single(there.Requests);
    }

    [Fact]
    public async Task A_call_that_times_out_is_an_intake_error_and_a_cancelled_one_is_not()
    {
        var slow = new Canned((_, _) => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException()));
        var e = await Assert.ThrowsAsync<IntakeException>(() => Host(slow).LabeledAsync("octo/repo", "chargehand", default));
        Assert.Contains("the labelled issues could not be reached", e.Message);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var stopped = new Canned((_, _) => throw new TaskCanceledException("A task was canceled.", null, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Host(stopped).LabeledAsync("octo/repo", "chargehand", cancelled.Token));
    }
}
