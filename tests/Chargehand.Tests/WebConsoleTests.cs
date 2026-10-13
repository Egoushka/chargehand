using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>The operator console (ADR 0056): off unless asked for, served with the same strict policy as the UI, built or not, with client routes falling back to index.html.</summary>
public partial class WebConsoleTests
{
    private static HttpClient Plain(TestServer s, bool follow = true) =>
        new(new HttpClientHandler { AllowAutoRedirect = follow }) { BaseAddress = s.BaseAddress };

    private static Dictionary<string, byte[]> Build(params string[] names) =>
        names.ToDictionary(n => n, n => Encoding.UTF8.GetBytes($"content of {n}"));

    private static readonly string[] SampleBuild = ["index.html", "assets/index-CE9cHNP0.js", "assets/index-jIBtDwzN.css"];

    [Fact]
    public async Task The_console_is_off_unless_the_profile_turns_the_ui_on()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        using var anonymous = Plain(s);

        foreach (var path in new[] { "/console", "/console/", "/console/runs/r1", "/console/assets/index-CE9cHNP0.js" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await s.Http.GetAsync(path)).StatusCode);
        }
    }

    [Fact]
    public async Task The_console_is_served_without_a_key_under_a_strict_policy_whether_or_not_it_is_built()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), ui: true);
        using var anonymous = Plain(s);

        foreach (var path in new[] { "/console/", "/console/index.html", "/console/runs/r1", "/console/assets/missing.js", "/console/favicon.ico" })
        {
            var res = await anonymous.GetAsync(path);
            Assert.Equal(WebConsole.ContentSecurityPolicy, res.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("no-referrer", res.Headers.GetValues("Referrer-Policy").Single());
            Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        }
        var page = await anonymous.GetAsync("/console/");
        Assert.Equal("text/html; charset=utf-8", page.Content.Headers.ContentType!.ToString());
        Assert.Equal(WebConsole.Built ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, page.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/ui/")).StatusCode);
    }

    [Fact]
    public async Task The_console_path_without_a_slash_redirects_and_opens_nothing_else()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), ui: true);
        using var anonymous = Plain(s, follow: false);

        var redirect = await anonymous.GetAsync("/console");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("/console/", redirect.Headers.Location!.OriginalString);
        // Only /console and below skip the key; a neighbour of that name and every API route still need it.
        foreach (var path in new[] { "/consolex", "/console-other/", "/v1/runs", "/v1/accounts/me", "/v1/mcp" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await anonymous.PostAsync("/console/", null)).StatusCode);
    }

    [Fact]
    public async Task A_path_that_climbs_out_of_the_console_finds_no_file()
    {
        await using var s = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply), ui: true);
        using var anonymous = Plain(s);

        // 404 from a build; the not-built page answers 503 to every path. Neither is a file read from disk: the files are a dictionary of embedded resources.
        var expected = WebConsole.Built ? HttpStatusCode.NotFound : HttpStatusCode.ServiceUnavailable;
        Assert.Equal(expected, (await anonymous.GetAsync("/console/..%2Fappsettings.json")).StatusCode);
        Assert.Equal(expected, (await anonymous.GetAsync("/console/assets/..%2F..%2Fappsettings.json")).StatusCode);
    }

    [Fact]
    public void The_policy_allows_no_inline_script_no_eval_and_no_other_origin_and_is_the_one_the_web_build_tests_against()
    {
        var directives = WebConsole.ContentSecurityPolicy.Split(';', StringSplitOptions.TrimEntries).ToDictionary(d => d.Split(' ')[0], d => d[(d.IndexOf(' ') + 1)..]);

        Assert.Equal("'none'", directives["default-src"]);
        Assert.Equal("'self'", directives["script-src"]);
        Assert.Equal("'self'", directives["style-src"]);
        Assert.Equal("'self'", directives["connect-src"]);
        Assert.Equal("'none'", directives["frame-ancestors"]);
        Assert.DoesNotContain("unsafe", WebConsole.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("*", WebConsole.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("http", WebConsole.ContentSecurityPolicy, StringComparison.Ordinal);
        // `vite preview` sends the policy in web/console/csp.ts, so the smoke test fails on a violation of what the server sends.
        Assert.Contains($"\"{WebConsole.ContentSecurityPolicy}\"", File.ReadAllText(Repo.Path("web", "console", "csp.ts")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_of_the_build_is_served_as_itself_and_assets_are_cached_for_a_year()
    {
        var files = Build(SampleBuild);

        var script = WebConsole.Resolve(files, "assets/index-CE9cHNP0.js");
        Assert.Equal((200, "text/javascript; charset=utf-8", WebConsole.ImmutableCache), (script.Status, script.ContentType, script.CacheControl));
        Assert.Equal(files["assets/index-CE9cHNP0.js"], script.Body);
        var style = WebConsole.Resolve(files, "assets/index-jIBtDwzN.css");
        Assert.Equal(("text/css; charset=utf-8", WebConsole.ImmutableCache), (style.ContentType, style.CacheControl));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("index.html")]
    public void The_page_itself_is_never_cached(string? path)
    {
        var files = Build(SampleBuild);

        var page = WebConsole.Resolve(files, path);

        Assert.Equal((200, "text/html; charset=utf-8", "no-cache"), (page.Status, page.ContentType, page.CacheControl));
        Assert.Equal(files["index.html"], page.Body);
    }

    [Theory]
    [InlineData("runs")]
    [InlineData("runs/9f2c")]
    [InlineData("needs-you")]
    public void A_client_route_gets_the_page_so_a_reload_or_a_pasted_link_works(string route)
    {
        var files = Build(SampleBuild);

        var reply = WebConsole.Resolve(files, route);

        Assert.Equal((200, "text/html; charset=utf-8", "no-cache"), (reply.Status, reply.ContentType, reply.CacheControl));
        Assert.Equal(files["index.html"], reply.Body);
    }

    [Theory]
    [InlineData("assets/index-OLD00000.js")]
    [InlineData("assets/missing")]
    [InlineData("missing.js")]
    [InlineData("favicon.ico")]
    public void A_missing_file_is_a_404_never_the_page(string path)
    {
        var reply = WebConsole.Resolve(Build(SampleBuild), path);

        Assert.Equal(404, reply.Status);
        Assert.Empty(reply.Body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("runs/9f2c")]
    [InlineData("assets/index-CE9cHNP0.js")]
    public void Without_a_web_build_every_path_answers_with_a_page_that_says_so(string? path)
    {
        var reply = WebConsole.Resolve(new Dictionary<string, byte[]>(), path);

        Assert.Equal((503, "text/html; charset=utf-8", "no-store"), (reply.Status, reply.ContentType, reply.CacheControl));
        var page = Encoding.UTF8.GetString(reply.Body);
        Assert.Contains("not built", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style", page, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"<script\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTag();

    [GeneratedRegex(@"(?:src|href)=""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex Reference();

    /// <summary>Both states of the checkout are fine; each is checked for what it is. CI builds the web app first, so there the embedded build is the one checked.</summary>
    [Fact]
    public void The_embedded_build_is_the_files_in_dist_and_the_page_loads_nothing_inline_or_from_elsewhere()
    {
        var dist = Repo.Path("web", "console", "dist");
        if (!WebConsole.Built)
        {
            Assert.Empty(WebConsole.Paths);
            return;
        }

        var onDisk = Directory.EnumerateFiles(dist, "*", SearchOption.AllDirectories).Select(f => System.IO.Path.GetRelativePath(dist, f).Replace('\\', '/')).Order().ToList();
        Assert.Equal(onDisk, WebConsole.Paths.Order().ToList());

        var html = Encoding.UTF8.GetString(WebConsole.Read("index.html"));
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Regex.Matches(html, @"\son\w+\s*=", RegexOptions.IgnoreCase));
        // Every script has a src; none is inline.
        Assert.All(ScriptTag().Matches(html).Select(m => m.Value), tag => Assert.Contains("src=\"", tag, StringComparison.Ordinal));
        // Everything the page loads is a file of this build, under /console/.
        var references = Reference().Matches(html).Select(m => m.Groups[1].Value).Where(u => !u.StartsWith("data:", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(references);
        Assert.All(references, url =>
        {
            Assert.StartsWith(WebConsole.Prefix + "/", url, StringComparison.Ordinal);
            Assert.Contains(url[(WebConsole.Prefix.Length + 1)..], WebConsole.Paths);
        });
        // Assets carry a content hash in the name, which is what makes a year of caching safe.
        Assert.All(WebConsole.Paths.Where(p => p != "index.html"), p => Assert.Matches(@"^assets/.+-[\w-]{8,}\.\w+$", p));
    }
}
