using System.Net;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Driven;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;

namespace Chargehand.Tests;

/// <summary>The GitLab forge (ADR 0046, 0047): every call it makes, as the requests a fake host sees. The token is a <c>PRIVATE-TOKEN</c> header and nowhere else; every merge is pinned to a head commit and squash.</summary>
public class GitLabForgeTests
{
    private const string Token = "glpat-secrettoken";
    private const string Api = "https://gitlab.example.test/api/v4/";
    private const string Project = "projects/group%2Fsub%2Fproject";

    private static readonly RemoteRepository Repo = new("group/sub", "project", ForgeKind.GitLab, "gitlab.example.test");

    private sealed record Seen(HttpMethod Method, string Url, string? Body, string? Token, string? Authorization);

    private sealed class Canned(Func<HttpRequestMessage, string?, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Seen(request.Method, request.RequestUri!.AbsoluteUri, body, request.Headers.TryGetValues("PRIVATE-TOKEN", out var t) ? t.Single() : null, request.Headers.Authorization?.ToString()));
            return answer(request, body);
        }
    }

    private static GitLabForge Forge(Canned handler) => new(new HttpClient(handler) { BaseAddress = new Uri(Api) }, Token);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };

    private static string Path(Seen seen) => seen.Url[Api.Length..];

    // The merge request, its pipeline's jobs and its notes (newest first, as GitLab sorts them for sort=desc).
    private static HttpResponseMessage StateAnswer(HttpRequestMessage request, string? mergeRequest = null, string? jobs = null, string? notes = null)
    {
        var path = request.RequestUri!.PathAndQuery;
        return path.Contains("/jobs", StringComparison.Ordinal) ? Json(jobs ?? Jobs)
            : path.Contains("/notes", StringComparison.Ordinal) ? Json(notes ?? """[{"body":"<!-- chargehand-ci-fix attempt=1 jobs=0123456789ab -->"},{"body":"hello"}]""")
            : Json(mergeRequest ?? MergeRequest());
    }

    private static string MergeRequest(string state = "opened", bool draft = true, string detailed = "draft_status", string sha = "abc1234", string pipelineSha = "abc1234", bool auto = false, string title = "Draft: Add a retry") =>
        $$$"""
        {"iid":7,"state":"{{{state}}}","draft":{{{(draft ? "true" : "false")}}},"title":"{{{title}}}","sha":"{{{sha}}}","target_branch":"main","detailed_merge_status":"{{{detailed}}}",
         "merge_when_pipeline_succeeds":{{{(auto ? "true" : "false")}}},"head_pipeline":{"id":55,"sha":"{{{pipelineSha}}}","status":"running"}}
        """;

    private const string Jobs = """
        [{"id":501,"name":"build","status":"failed","allow_failure":false},
         {"id":502,"name":"lint","status":"running","allow_failure":false},
         {"id":503,"name":"docs","status":"success","allow_failure":true},
         {"id":504,"name":"e2e","status":"canceled","allow_failure":false},
         {"id":505,"name":"deploy","status":"manual","allow_failure":false},
         {"id":506,"name":"flaky","status":"failed","allow_failure":true},
         {"id":507,"name":"unit","status":"success","allow_failure":false},
         {"id":508,"name":"later","status":"created","allow_failure":false}]
        """;

    [Fact]
    public async Task A_draft_merge_request_is_opened_once_with_a_draft_title_and_the_token_in_the_header_only()
    {
        var handler = new Canned((_, _) => Json("""{"web_url":"https://gitlab.example.test/group/sub/project/-/merge_requests/7","iid":7}""", HttpStatusCode.Created));
        var mr = await Forge(handler).CreateDraftAsync(Repo, "chargehand/run1", "main", "Add a retry", "Body text", default);

        Assert.Equal(new PullRequestRef("https://gitlab.example.test/group/sub/project/-/merge_requests/7", 7), mr);
        var seen = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Post, $"{Api}{Project}/merge_requests", Token, null), (seen.Method, seen.Url, seen.Token, seen.Authorization));
        using var doc = JsonDocument.Parse(seen.Body!);
        Assert.Equal(("chargehand/run1", "main", "Draft: Add a retry", "Body text"), (doc.RootElement.GetProperty("source_branch").GetString(), doc.RootElement.GetProperty("target_branch").GetString(),
            doc.RootElement.GetProperty("title").GetString(), doc.RootElement.GetProperty("description").GetString()));
        Assert.DoesNotContain(Token, seen.Body);
    }

    [Theory]
    [InlineData("Draft: Add a retry", "Draft: Add a retry")]
    [InlineData("[draft] Add a retry", "[draft] Add a retry")]
    [InlineData("Add a retry", "Draft: Add a retry")]
    public async Task A_title_that_already_says_draft_is_not_prefixed_twice(string title, string sent)
    {
        var handler = new Canned((_, _) => Json("""{"web_url":"https://x.test/1","iid":1}""", HttpStatusCode.Created));
        await Forge(handler).CreateDraftAsync(Repo, "chargehand/run1", "main", title, "b", default);

        using var doc = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal(sent, doc.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_merge_request_that_already_exists_is_returned_not_failed()
    {
        var handler = new Canned((request, _) => request.Method == HttpMethod.Post
            ? Json("""{"message":["Another open merge request already exists for this source branch: !3"]}""", HttpStatusCode.Conflict)
            : Json("""[{"web_url":"https://gitlab.example.test/group/sub/project/-/merge_requests/3","iid":3}]"""));
        var mr = await Forge(handler).CreateDraftAsync(Repo, "chargehand/run1", "main", "Add a retry", "Body", default);

        Assert.Equal(new PullRequestRef("https://gitlab.example.test/group/sub/project/-/merge_requests/3", 3, Reused: true), mr);
        Assert.Equal((HttpMethod.Get, $"{Api}{Project}/merge_requests?source_branch=chargehand%2Frun1&state=opened", Token), (handler.Requests[1].Method, handler.Requests[1].Url, handler.Requests[1].Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"message":"401 Unauthorized the-token"}""")]
    [InlineData(HttpStatusCode.Forbidden, """{"error":"insufficient_scope","error_description":"the-token"}""")]
    [InlineData(HttpStatusCode.UnprocessableEntity, """{"message":{"base":["the-token is no good"]}}""")]
    [InlineData(HttpStatusCode.InternalServerError, "<html>the-token</html>")]
    public async Task A_refusal_is_a_pull_request_exception_without_the_token(HttpStatusCode status, string body)
    {
        var handler = new Canned((_, _) => Json(body.Replace("the-token", Token, StringComparison.Ordinal), status));
        var e = await Assert.ThrowsAsync<PullRequestException>(() => Forge(handler).CreateDraftAsync(Repo, "b", "main", "t", "b", default));
        Assert.Contains($"GitLab answered {(int)status}", e.Message);
        Assert.DoesNotContain(Token, e.Message);
    }

    [Theory]
    [InlineData("Draft: [partial] Add a retry", "Add a retry", "Draft: Add a retry")]
    [InlineData("Draft: [partial] Add a retry", "Draft: Add a retry", "Draft: Add a retry")]
    [InlineData("Add a retry", "Add a retry", "Add a retry")]
    public async Task An_edit_sets_the_title_and_description_and_keeps_a_draft_a_draft(string current, string title, string sent)
    {
        var handler = new Canned((request, _) => Json(request.Method == HttpMethod.Get ? MergeRequest(title: current, draft: current.StartsWith("Draft", StringComparison.Ordinal)) : "{}"));
        await Forge(handler).EditAsync(Repo, 7, title, "Done.", default);

        Assert.Equal([(HttpMethod.Get, $"{Project}/merge_requests/7"), (HttpMethod.Put, $"{Project}/merge_requests/7")], handler.Requests.Select(r => (r.Method, Path(r))).ToArray());
        Assert.Equal((Token, null), (handler.Requests[1].Token, handler.Requests[1].Authorization));
        using var doc = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.Equal(["title", "description"], doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal((sent, "Done."), (doc.RootElement.GetProperty("title").GetString(), doc.RootElement.GetProperty("description").GetString()));
    }

    [Fact]
    public async Task A_refused_edit_is_a_pull_request_exception_without_the_token()
    {
        var handler = new Canned((_, _) => Json($$"""{"message":"403 Forbidden {{Token}}"}""", HttpStatusCode.Forbidden));
        var e = await Assert.ThrowsAsync<PullRequestException>(() => Forge(handler).EditAsync(Repo, 7, "t", "b", default));

        Assert.Contains("GitLab answered 403", e.Message);
        Assert.DoesNotContain(Token, e.Message);
    }

    [Fact]
    public async Task The_state_names_the_head_the_comments_and_each_job_of_the_head_pipeline_with_whether_it_is_required()
    {
        var handler = new Canned((request, _) => StateAnswer(request));
        var pr = await Forge(handler).GetAsync(Repo, 7, default);

        Assert.Equal(("group/sub/project!7", 7, "abc1234", "main", true, false, false, "DRAFT", false), (pr.NodeId, pr.Number, pr.HeadSha, pr.BaseBranch, pr.IsDraft, pr.Merged, pr.Closed, pr.MergeStatus, pr.AutoMerge));
        Assert.Equal(["hello", "<!-- chargehand-ci-fix attempt=1 jobs=0123456789ab -->"], pr.Comments);
        Assert.Equal(
            [("build", true, CheckOutcome.Failed, 501L), ("lint", true, CheckOutcome.Pending, 502L), ("docs", false, CheckOutcome.Passed, 503L), ("e2e", true, CheckOutcome.Other, 504L),
                ("deploy", true, CheckOutcome.Other, 505L), ("flaky", false, CheckOutcome.Failed, 506L), ("unit", true, CheckOutcome.Passed, 507L), ("later", true, CheckOutcome.Pending, 508L)],
            pr.Checks.Select(c => (c.Name, c.Required, c.Outcome, c.JobId)).ToArray());
        Assert.Equal([$"{Project}/merge_requests/7", $"{Project}/pipelines/55/jobs?per_page=100", $"{Project}/merge_requests/7/notes?order_by=created_at&sort=desc&per_page=50"],
            handler.Requests.Select(Path).ToArray());
        Assert.All(handler.Requests, r => Assert.Equal((HttpMethod.Get, Token, null), (r.Method, r.Token, r.Authorization)));
    }

    [Fact]
    public async Task A_pipeline_of_an_older_head_is_not_this_heads_checks()
    {
        var handler = new Canned((request, _) => StateAnswer(request, MergeRequest(sha: "newhead1", pipelineSha: "abc1234")));
        var pr = await Forge(handler).GetAsync(Repo, 7, default);

        Assert.Empty(pr.Checks);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/pipelines/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_merge_request_with_no_pipeline_has_no_checks()
    {
        var handler = new Canned((request, _) => StateAnswer(request, """{"iid":7,"state":"opened","draft":false,"title":"t","sha":"abc1234","target_branch":"main","detailed_merge_status":"mergeable","head_pipeline":null}"""));
        var pr = await Forge(handler).GetAsync(Repo, 7, default);

        Assert.Empty(pr.Checks);
        Assert.Equal("CLEAN", pr.MergeStatus);
    }

    [Fact]
    public async Task A_pipeline_that_ended_without_a_job_stands_for_itself()
    {
        var handler = new Canned((request, _) => StateAnswer(request, MergeRequest().Replace("\"running\"", "\"failed\"", StringComparison.Ordinal), jobs: "[]"));
        var check = Assert.Single((await Forge(handler).GetAsync(Repo, 7, default)).Checks);

        Assert.Equal(("pipeline", true, CheckOutcome.Failed, (long?)null), (check.Name, check.Required, check.Outcome, check.JobId));
    }

    [Theory]
    [InlineData("opened", false, false)]
    [InlineData("merged", true, false)]
    [InlineData("closed", false, true)]
    [InlineData("locked", false, false)]
    public async Task The_state_says_whether_the_merge_request_is_merged_or_closed(string state, bool merged, bool closed)
    {
        var pr = await Forge(new Canned((request, _) => StateAnswer(request, MergeRequest(state: state)))).GetAsync(Repo, 7, default);
        Assert.Equal((merged, closed), (pr.Merged, pr.Closed));
    }

    [Theory]
    [InlineData("mergeable", "CLEAN")]
    [InlineData("conflict", "DIRTY")]
    [InlineData("need_rebase", "BEHIND")]
    [InlineData("draft_status", "DRAFT")]
    [InlineData("checking", "UNKNOWN")]
    [InlineData("unchecked", "UNKNOWN")]
    [InlineData("ci_must_pass", "BLOCKED")]
    [InlineData("ci_still_running", "BLOCKED")]
    [InlineData("not_approved", "BLOCKED")]
    [InlineData("discussions_not_resolved", "BLOCKED")]
    [InlineData("a_status_gitlab_adds_later", "BLOCKED")]
    public async Task GitLabs_merge_status_is_read_as_the_integrator_reads_GitHubs(string detailed, string expected)
    {
        var pr = await Forge(new Canned((request, _) => StateAnswer(request, MergeRequest(detailed: detailed)))).GetAsync(Repo, 7, default);
        Assert.Equal(expected, pr.MergeStatus);
    }

    [Fact]
    public async Task Auto_merge_shows_when_gitlab_will_merge_on_the_pipeline()
    {
        var pr = await Forge(new Canned((request, _) => StateAnswer(request, MergeRequest(auto: true)))).GetAsync(Repo, 7, default);
        Assert.True(pr.AutoMerge);
    }

    [Fact]
    public async Task A_body_that_is_not_a_merge_request_is_an_integration_exception()
    {
        var e = await Assert.ThrowsAsync<IntegrationException>(() => Forge(new Canned((_, _) => Json("""{"unexpected":true}"""))).GetAsync(Repo, 7, default));
        Assert.Contains("not a merge request's state", e.Message);
    }

    [Fact]
    public async Task How_far_behind_is_the_number_of_commits_the_base_has_that_the_branch_lacks()
    {
        var handler = new Canned((_, _) => Json("""{"commits":[{"id":"a"},{"id":"b"}],"compare_timeout":false}"""));

        Assert.Equal(2, await Forge(handler).BehindByAsync(Repo, "main", "chargehand/run1", default));
        Assert.Equal($"{Project}/repository/compare?from=chargehand%2Frun1&to=main", Path(handler.Requests[0]));
    }

    [Fact]
    public async Task A_branch_that_is_not_behind_has_no_commits_to_catch_up_on()
    {
        Assert.Equal(0, await Forge(new Canned((_, _) => Json("""{"commits":[]}"""))).BehindByAsync(Repo, "main", "b", default));
    }

    [Fact]
    public async Task The_branch_is_rebased_when_its_head_is_the_one_expected()
    {
        var handler = new Canned((request, _) => request.Method == HttpMethod.Get ? Json(MergeRequest()) : Json("""{"rebase_in_progress":true}""", HttpStatusCode.Accepted));
        await Forge(handler).UpdateBranchAsync(Repo, 7, "abc1234", default);

        Assert.Equal([(HttpMethod.Get, $"{Project}/merge_requests/7"), (HttpMethod.Put, $"{Project}/merge_requests/7/rebase")], handler.Requests.Select(r => (r.Method, Path(r))).ToArray());
    }

    [Fact]
    public async Task A_branch_whose_head_moved_is_not_rebased()
    {
        var handler = new Canned((_, _) => Json(MergeRequest(sha: "other99")));
        var e = await Assert.ThrowsAsync<IntegrationException>(() => Forge(handler).UpdateBranchAsync(Repo, 7, "abc1234", default));

        Assert.Contains("head moved", e.Message);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("Draft: Add a retry", "Add a retry")]
    [InlineData("[Draft] Add a retry", "Add a retry")]
    [InlineData("(draft) Add a retry", "Add a retry")]
    [InlineData("draft: Draft: Add a retry", "Add a retry")]
    public async Task Marking_ready_takes_the_draft_prefix_off_the_title(string title, string ready)
    {
        var handler = new Canned((request, _) => Json(request.Method == HttpMethod.Get ? MergeRequest(title: title) : "{}"));
        await Forge(handler).MarkReadyAsync(Repo, "group/sub/project!7", default);

        Assert.Equal([(HttpMethod.Get, $"{Project}/merge_requests/7"), (HttpMethod.Put, $"{Project}/merge_requests/7")], handler.Requests.Select(r => (r.Method, Path(r))).ToArray());
        using var doc = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.Equal(ready, doc.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_merge_request_that_is_ready_is_left_alone()
    {
        var handler = new Canned((_, _) => Json(MergeRequest(title: "Add a retry", draft: false)));
        await Forge(handler).MarkReadyAsync(Repo, "group/sub/project!7", default);

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("no-bang")]
    [InlineData("project!7")]
    [InlineData("group/project!seven")]
    public async Task A_handle_that_is_not_a_project_and_a_number_is_refused(string handle)
    {
        var handler = new Canned((_, _) => Json("{}"));
        await Assert.ThrowsAsync<IntegrationException>(() => Forge(handler).MarkReadyAsync(Repo, handle, default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Auto_merge_is_squash_on_the_pipeline_pinned_to_the_head_commit()
    {
        var handler = new Canned((_, _) => Json("{}"));
        await Forge(handler).EnableAutoMergeAsync(Repo, "group/sub/project!7", "abc1234", default);

        var seen = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Put, $"{Project}/merge_requests/7/merge", Token, null), (seen.Method, Path(seen), seen.Token, seen.Authorization));
        using var doc = JsonDocument.Parse(seen.Body!);
        Assert.True(doc.RootElement.GetProperty("merge_when_pipeline_succeeds").GetBoolean());
        Assert.Equal("abc1234", doc.RootElement.GetProperty("sha").GetString());
        Assert.True(doc.RootElement.GetProperty("squash").GetBoolean());
    }

    [Fact]
    public async Task A_merge_now_is_squash_pinned_to_the_head_commit_and_does_not_wait_on_a_pipeline()
    {
        var handler = new Canned((_, _) => Json("{}"));
        await Forge(handler).MergeNowAsync(Repo, 7, "abc1234", default);

        var seen = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Put, $"{Project}/merge_requests/7/merge"), (seen.Method, Path(seen)));
        using var doc = JsonDocument.Parse(seen.Body!);
        Assert.Equal("abc1234", doc.RootElement.GetProperty("sha").GetString());
        Assert.True(doc.RootElement.GetProperty("squash").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("merge_when_pipeline_succeeds", out _));
    }

    [Fact]
    public async Task A_comment_is_a_note_on_the_merge_request()
    {
        var handler = new Canned((_, _) => Json("{}", HttpStatusCode.Created));
        await Forge(handler).CommentAsync(Repo, 7, "<!-- marker -->\nhello", default);

        var seen = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Post, $"{Project}/merge_requests/7/notes"), (seen.Method, Path(seen)));
        using var doc = JsonDocument.Parse(seen.Body!);
        Assert.Equal("<!-- marker -->\nhello", doc.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_job_log_is_its_trace_cut_to_the_end_without_colour_codes_and_section_markers()
    {
        var trace = "section_start:1700000000:step_script\r\u001b[0K\u001b[36;1mRunning tests\u001b[0;m\nline two\nsection_end:1700000009:step_script\r\u001b[0K\n\u001b[31;1mFAILED Retry.Tests: expected 3\u001b[0;m\n";
        var handler = new Canned((_, _) => Json(trace));

        var tail = await Forge(handler).JobLogTailAsync(Repo, 501, 1000, default);

        Assert.Equal("Running tests\nline two\n\nFAILED Retry.Tests: expected 3", tail);
        Assert.Equal((HttpMethod.Get, $"{Project}/jobs/501/trace", Token), (handler.Requests[0].Method, Path(handler.Requests[0]), handler.Requests[0].Token));
        Assert.Equal("FAILED Retry.Tests: expected 3", await Forge(handler).JobLogTailAsync(Repo, 501, 31, default));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.NotAcceptable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_refused_call_is_an_integration_exception_without_the_token(HttpStatusCode status)
    {
        var handler = new Canned((_, _) => Json($$"""{"message":"405 Method Not Allowed {{Token}}"}""", status));
        var e = await Assert.ThrowsAsync<IntegrationException>(() => Forge(handler).EnableAutoMergeAsync(Repo, "group/sub/project!7", "abc1234", default));

        Assert.Contains($"GitLab answered {(int)status} to enabling auto-merge", e.Message);
        Assert.DoesNotContain(Token, e.Message);
    }

    [Fact]
    public async Task A_host_that_cannot_be_reached_is_an_integration_exception_without_the_token()
    {
        var handler = new Canned((_, _) => throw new HttpRequestException($"connection to {Token} refused"));
        var e = await Assert.ThrowsAsync<IntegrationException>(() => Forge(handler).CommentAsync(Repo, 7, "hello", default));

        Assert.Contains("could not be reached", e.Message);
        Assert.DoesNotContain(Token, e.Message);
    }

    [Theory]
    [InlineData("gitlab.example.test", null, "https://gitlab.example.test/api/v4/")]
    [InlineData("gitlab.example.test", "https://git.example.test", "https://git.example.test/api/v4/")]
    [InlineData("gitlab.example.test", "https://git.example.test/", "https://git.example.test/api/v4/")]
    [InlineData("gitlab.example.test", "https://git.example.test/api/v4", "https://git.example.test/api/v4/")]
    [InlineData("gitlab.example.test", "https://example.test/gitlab", "https://example.test/gitlab/api/v4/")]
    public void The_api_root_comes_from_the_remote_host_or_the_profile(string host, string? baseUrl, string expected) =>
        Assert.Equal(expected, GitLabForge.ApiRoot(host, baseUrl).AbsoluteUri);

    // The integrator, unchanged, through the GitLab forge.

    private static IntegratorPolicy Merge => new(new DrivenIntegrator(Repositories: new Dictionary<string, string> { ["group/sub/project"] = "merge" }));

    private static readonly IntegrationTarget Target = new(Repo, 7, "chargehand/run1", false, "Add a retry");

    [Fact]
    public async Task The_integrator_marks_a_draft_ready_and_enables_auto_merge_on_gitlab()
    {
        var title = "Draft: Add a retry";
        var auto = false;
        var handler = new Canned((request, body) =>
        {
            var path = request.RequestUri!.AbsolutePath + request.RequestUri.Query;
            if (request.Method == HttpMethod.Put && path.EndsWith("/merge", StringComparison.Ordinal))
            {
                auto = true;
                return Json("{}");
            }
            if (request.Method == HttpMethod.Put)
            {
                title = JsonDocument.Parse(body!).RootElement.GetProperty("title").GetString()!;
                return Json("{}");
            }
            return path.Contains("/compare", StringComparison.Ordinal) ? Json("""{"commits":[]}""")
                : StateAnswer(request, MergeRequest(draft: title.StartsWith("Draft", StringComparison.Ordinal), detailed: title.StartsWith("Draft", StringComparison.Ordinal) ? "draft_status" : "ci_still_running", auto: auto, title: title));
        });
        var forge = Forge(handler);
        List<string> told = [];

        var result = await new Integrator(Merge, forge).IntegrateAsync(Target, told.Add, default);

        Assert.Equal(IntegrationAction.AutoMergeEnabled, result.Action);
        Assert.Equal("auto-merge (squash) enabled for abc1234; GitLab merges once the required checks pass", result.Detail);
        Assert.Equal("Add a retry", title);
        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Put && Path(r) == $"{Project}/merge_requests/7/merge");
    }

    [Fact]
    public async Task The_integrator_hands_a_failing_job_and_the_end_of_its_log_to_the_fix_session_on_gitlab()
    {
        var handler = new Canned((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return request.Method == HttpMethod.Post ? Json("{}", HttpStatusCode.Created)
                : path.EndsWith("/trace", StringComparison.Ordinal) ? Json("compiling\nFAILED Retry.Tests: expected 3 but got 2\n")
                : StateAnswer(request, MergeRequest(draft: false, detailed: "ci_must_pass"), jobs: """[{"id":501,"name":"build","status":"failed","allow_failure":false},{"id":502,"name":"docs","status":"failed","allow_failure":true}]""", notes: "[]");
        });
        CiFixRequest? asked = null;

        var result = await new Integrator(Merge, Forge(handler), new DrivenCiFix(MaxAttempts: 2)).FollowAsync(Target,
            (request, _) =>
            {
                asked = request;
                return Task.FromResult(new CiFixResult(false, "nothing to push"));
            }, _ => { }, default);

        Assert.Equal(IntegrationAction.NeedsAttention, result.Action);
        Assert.Equal(["build"], asked!.Failures.Select(f => f.Name).ToArray());
        Assert.Contains("FAILED Retry.Tests: expected 3 but got 2", asked.Failures[0].LogTail);
        Assert.Contains("FAILED Retry.Tests: expected 3 but got 2", asked.Goal);
        var comment = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal($"{Project}/merge_requests/7/notes", Path(comment));
        Assert.Contains("chargehand-ci-fix attempt=1", comment.Body);
    }
}
