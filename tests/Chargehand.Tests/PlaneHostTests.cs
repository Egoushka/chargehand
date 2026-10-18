using System.Net;
using Chargehand.Automation;

namespace Chargehand.Tests;

/// <summary>ADR 0067: intake's calls to Plane's public API v1. The key is in <c>X-API-Key</c> and in no message; work items are asked for first and issues second.</summary>
public class PlaneHostTests
{
    private const string Key = "plane_api_secretkey";

    private sealed class Canned(Func<HttpRequestMessage, string?, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body, string? Key)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!.ToString(), body, request.Headers.TryGetValues("X-API-Key", out var v) ? v.Single() : null));
            return answer(request, body);
        }
    }

    private static PlaneHost Host(Canned handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://plane.example.test/") }, "ws", () => Key);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };

    private const string Base = "https://plane.example.test/api/v1/workspaces/ws/projects/p1";

    private static string ItemJson(string id, string state, int sequence = 1, string html = "<p>Body</p>") =>
        $$"""{"id":"{{id}}","name":"Item {{id}}","description_html":"{{html}}","state":"{{state}}","sequence_id":{{sequence}},"updated_at":"2026-10-15T10:00:00Z"}""";

    [Fact]
    public async Task States_and_the_project_identifier_are_read_with_the_key_in_the_header()
    {
        var handler = new Canned((r, _) => Json(r.RequestUri!.AbsolutePath.EndsWith("/states/", StringComparison.Ordinal)
            ? """{"results":[{"id":"s1","name":"Agent"},{"id":"s2","name":"In Review"}]}"""
            : """{"id":"p1","identifier":"ABC"}"""));
        var host = Host(handler);

        Assert.Equal(["s1:Agent", "s2:In Review"], (await host.StatesAsync("p1", default)).Select(s => $"{s.Id}:{s.Name}"));
        Assert.Equal("ABC", await host.ProjectIdentifierAsync("p1", default));
        Assert.Equal([(HttpMethod.Get, $"{Base}/states/?per_page=100", (string?)null, (string?)Key), (HttpMethod.Get, $"{Base}/", null, Key)], handler.Requests);
    }

    [Fact]
    public async Task Work_items_are_paged_by_cursor_and_filtered_by_state_with_the_description_as_text()
    {
        var handler = new Canned((r, _) => Json(r.RequestUri!.Query.Contains("cursor=100%3A1%3A0", StringComparison.Ordinal)
            ? $$"""{"results":[{{ItemJson("i3", "s1", 3)}}],"next_page_results":false,"next_cursor":"100:2:0"}"""
            : r.RequestUri.Query.Contains("per_page=1&", StringComparison.Ordinal) || r.RequestUri.Query == "?per_page=1" ? """{"results":[]}"""
            : $$"""{"results":[{{ItemJson("i1", "s1", 1, "<p>One &amp; two</p><ul><li>a</li></ul>")}},{{ItemJson("i2", "s2", 2)}}],"next_page_results":true,"next_cursor":"100:1:0"}"""));
        var items = await Host(handler).ItemsInStateAsync("p1", "s1", default);

        Assert.Equal(["i1", "i3"], items.Select(i => i.Id));
        Assert.Equal("One & two\n\n- a", items[0].Description);
        Assert.Equal((3, new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero)), (items[1].SequenceId, items[1].UpdatedAt));
        Assert.Equal([$"{Base}/work-items/?per_page=1", $"{Base}/work-items/?per_page=100", $"{Base}/work-items/?per_page=100&cursor=100%3A1%3A0"], handler.Requests.Select(r => r.Url));
    }

    [Fact]
    public async Task An_instance_that_answers_404_for_work_items_is_asked_for_issues_and_the_answer_is_kept()
    {
        var handler = new Canned((r, _) => r.RequestUri!.AbsolutePath.Contains("/work-items/", StringComparison.Ordinal)
            ? Json("""{"error":"not found"}""", HttpStatusCode.NotFound)
            : Json("[" + ItemJson("i1", "s1") + "]"));
        var host = Host(handler);

        Assert.Single(await host.ItemsInStateAsync("p1", "s1", default));
        await host.CommentAsync("p1", "i1", "hi", default);
        Assert.Equal([$"{Base}/work-items/?per_page=1", $"{Base}/issues/?per_page=100", $"{Base}/issues/i1/comments/"], handler.Requests.Select(r => r.Url));
    }

    [Fact]
    public async Task A_comment_is_posted_as_escaped_html_and_a_state_change_patches_the_item()
    {
        var handler = new Canned((_, _) => Json("{}"));
        var host = Host(handler);
        await host.CommentAsync("p1", "i1", "run <1> & more\n\nsecond", default);
        await host.SetStateAsync("p1", "i1", "s9", default);

        var comment = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal($"{Base}/work-items/i1/comments/", comment.Url);
        Assert.Equal("<p>run &lt;1&gt; &amp; more</p><p>second</p>", System.Text.Json.JsonDocument.Parse(comment.Body!).RootElement.GetProperty("comment_html").GetString());
        var patch = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.Equal(($"{Base}/work-items/i1/", """{"state":"s9"}"""), (patch.Url, patch.Body));
    }

    [Fact]
    public async Task An_error_names_the_status_and_never_the_key()
    {
        var handler = new Canned((_, _) => Json($$"""{"error":"bad key {{Key}}"}""", HttpStatusCode.Unauthorized));
        var e = await Assert.ThrowsAsync<IntakeException>(() => Host(handler).StatesAsync("p1", default));

        Assert.Equal(401, e.Status);
        Assert.Contains("Plane answered 401 to the states: bad key [redacted]", e.Message);
        Assert.DoesNotContain(Key, e.Message);
    }

    [Fact]
    public async Task A_body_that_is_not_a_list_is_an_intake_error_and_an_unresolved_secret_is_too()
    {
        await Assert.ThrowsAsync<IntakeException>(() => Host(new Canned((_, _) => Json("""{"count":1}"""))).StatesAsync("p1", default));
        var host = new PlaneHost(new HttpClient(new Canned((_, _) => Json("{}"))) { BaseAddress = new Uri("https://plane.example.test/") }, "ws", () => throw new InvalidOperationException("no secret source resolved 'plane-key'"));
        var e = await Assert.ThrowsAsync<IntakeException>(() => host.StatesAsync("p1", default));
        Assert.Contains("plane-key", e.Message);
    }

    [Theory]
    [InlineData("<p>One</p><p>Two &lt;b&gt;</p>", "One\n\nTwo <b>")]
    [InlineData("<h1>Title</h1><ol><li>a</li><li>b</li></ol>", "Title\n\n- a\n- b")]
    [InlineData("line<br/>break&nbsp;here", "line\nbreak here")]
    [InlineData("  ", "")]
    public void Html_becomes_plain_text(string html, string text) => Assert.Equal(text, PlaneText.FromHtml(html));
}
