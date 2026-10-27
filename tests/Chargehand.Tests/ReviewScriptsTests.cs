using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Contracts;

namespace Chargehand.Tests;

/// <summary>The scripts behind .github/workflows/review.yml and self-review.yml: the request, the comment, the server client,
/// the comment poster and the gate. They need bash and jq, as the workflows' runners have.</summary>
public sealed partial class ReviewScriptsTests : IDisposable
{
    private const string Head = "0123456789abcdef0123456789abcdef01234567";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static async Task<(int Exit, string Out, string Err)> Bash(string script, string[] args, Dictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(Repo.Path("scripts", script));
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["NO_PROXY"] = "127.0.0.1";
        psi.Environment["no_proxy"] = "127.0.0.1";
        foreach (var (k, v) in env ?? [])
            psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdout, await stderr);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    // review-request.sh

    [Fact]
    public async Task The_request_carries_the_goal_and_the_diff_verbatim_and_passes_the_schema()
    {
        var goal = _dir.Write("goal.txt", "Fix `parse` with \"quotes\" and $(touch pwned) and @alice\n\nbody\n");
        var diff = _dir.Write("diff.txt", "diff --git a/x b/x\n+$(touch pwned)\n");
        var (exit, stdout, stderr) = await Bash("review-request.sh", ["/srv/repos/r", Head, goal, diff]);

        Assert.True(exit == 0, stderr);
        var request = Json(stdout);
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.Request, request));
        Assert.Equal("review", request.GetProperty("context").GetProperty("preset").GetString());
        Assert.Equal("/srv/repos/r", request.GetProperty("context").GetProperty("repository").GetProperty("path").GetString());
        Assert.Equal(Head, request.GetProperty("context").GetProperty("repository").GetProperty("commit").GetString());
        var inputs = request.GetProperty("inputs").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetString()!, i => i.GetProperty("text").GetString()!);
        Assert.Equal("Fix `parse` with \"quotes\" and $(touch pwned) and @alice\n\nbody", inputs["goal"]);
        Assert.Equal("diff --git a/x b/x\n+$(touch pwned)", inputs["diff"]);
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "pwned")));
    }

    [Fact]
    public async Task A_long_diff_and_goal_are_cut_and_the_preset_comes_from_the_environment()
    {
        var goal = _dir.Write("goal.txt", new string('g', 9000));
        var diff = _dir.Write("diff.txt", new string('d', 70000));
        var (exit, stdout, stderr) = await Bash("review-request.sh", ["/r", Head, goal, diff], new() { ["REVIEW_PRESET"] = "strict" });

        Assert.True(exit == 0, stderr);
        var request = Json(stdout);
        Assert.Equal("strict", request.GetProperty("context").GetProperty("preset").GetString());
        var inputs = request.GetProperty("inputs").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetString()!, i => i.GetProperty("text").GetString()!);
        Assert.Equal(new string('d', 60000) + "\n[diff truncated at 60000 characters]", inputs["diff"]);
        Assert.Equal(new string('g', 8000) + "\n[goal truncated at 8000 characters]", inputs["goal"]);
    }

    // review-comment.sh

    private const string Completed = """
        {"contract_version":"result/v1","task_id":"run-1","status":"completed",
         "summary":"Two issues, see @alice <b>now</b>.\nSecond line.",
         "claims":[
           {"text":"Minor naming issue","evidence":["e1"],"confidence":0.4,"support":"partial"},
           {"text":"Parse dereferences null @bob","evidence":["e2","e3","e4","e9","e5"],"confidence":0.9,"support":"supported"},
           {"text":"Cites only a URL","evidence":["e6"],"confidence":0.6}],
         "evidence":[
           {"id":"e1","kind":"input","locator":"diff"},
           {"id":"e2","kind":"file","locator":"src/Parser.cs:10-12"},
           {"id":"e3","kind":"diff","locator":"src/Parser.cs:11"},
           {"id":"e4","kind":"file","locator":"../../etc/passwd:1"},
           {"id":"e5","kind":"file","locator":"docs/a b.md:3"},
           {"id":"e6","kind":"url","locator":"https://example.com/`x`"}],
         "artifacts":[],"open_questions":["Unsupported by its citation: it never throws"],"confidence":0.8,
         "usage":{"input":1,"output":1,"cache_read":0,"cache_write":0,"usd":0.123456}}
        """;

    private async Task<string> Comment(string result, Dictionary<string, string>? env = null, int expectedExit = 0)
    {
        var file = _dir.Write("result.json", result);
        var (exit, stdout, stderr) = await Bash("review-comment.sh", [file, "owner/repo", Head], env);
        Assert.True(exit == expectedExit, stderr);
        return stdout;
    }

    [Fact]
    public async Task The_comment_lists_findings_with_linked_citations_and_support_verdicts()
    {
        var comment = await Comment(Completed);
        var blob = $"https://github.com/owner/repo/blob/{Head}";

        Assert.StartsWith("<!-- chargehand-review -->\n### chargehand review\n", comment, StringComparison.Ordinal);
        Assert.Contains($"[`src/Parser.cs:10-12`]({blob}/src/Parser.cs#L10-L12)", comment, StringComparison.Ordinal);
        Assert.Contains($"[`src/Parser.cs:11`]({blob}/src/Parser.cs#L11)", comment, StringComparison.Ordinal);
        Assert.Contains("Support: `supported`, confidence 0.9.", comment, StringComparison.Ordinal);
        Assert.Contains("Support: `partial`, confidence 0.4.", comment, StringComparison.Ordinal);
        Assert.Contains("Support: `not checked`, confidence 0.6.", comment, StringComparison.Ordinal);
        Assert.True(comment.IndexOf("Parse dereferences", StringComparison.Ordinal) < comment.IndexOf("Minor naming", StringComparison.Ordinal), "highest confidence first");
        Assert.Contains("`input diff`", comment, StringComparison.Ordinal);
        Assert.Contains("`unresolved e9`", comment, StringComparison.Ordinal);
        Assert.Contains("Open questions:\n- Unsupported by its citation: it never throws", comment, StringComparison.Ordinal);
        Assert.Contains($"Run `run-1`, $0.1235; citations checked against `{Head[..12]}`.", comment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_citation_that_could_leave_the_repository_or_break_the_link_is_not_linked()
    {
        var comment = await Comment(Completed);

        Assert.Contains("`file ../../etc/passwd:1`", comment, StringComparison.Ordinal);
        Assert.Contains("`file docs/a b.md:3`", comment, StringComparison.Ordinal);
        Assert.Contains("`url https://example.com/'x'`", comment, StringComparison.Ordinal);
        Assert.DoesNotContain("](https://example.com", comment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_text_cannot_ping_open_html_or_start_a_block()
    {
        var comment = await Comment(Completed);

        Assert.Contains("@​alice", comment, StringComparison.Ordinal);
        Assert.Contains("@​bob", comment, StringComparison.Ordinal);
        Assert.DoesNotContain("@alice", comment, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", comment, StringComparison.Ordinal);
        Assert.Contains("&lt;b>now&lt;/b>. Second line.", comment, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(comment, "<!--"));
    }

    [Fact]
    public async Task The_number_of_findings_is_capped_and_the_cap_is_said()
    {
        var comment = await Comment(Completed, new() { ["REVIEW_MAX_FINDINGS"] = "1", ["GITHUB_SERVER_URL"] = "https://ghe.example" });

        Assert.Contains("Showing 1 of 3 findings, highest confidence first.", comment, StringComparison.Ordinal);
        Assert.Contains("Parse dereferences", comment, StringComparison.Ordinal);
        Assert.DoesNotContain("Minor naming", comment, StringComparison.Ordinal);
        Assert.Contains($"](https://ghe.example/owner/repo/blob/{Head}/src/Parser.cs#L10-L12)", comment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bad_cap_is_a_usage_error()
    {
        var file = _dir.Write("result.json", Completed);
        var (exit, _, stderr) = await Bash("review-comment.sh", [file, "owner/repo", Head], new() { ["REVIEW_MAX_FINDINGS"] = "ten" });

        Assert.Equal(2, exit);
        Assert.Contains("REVIEW_MAX_FINDINGS", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_review_says_why_and_a_clean_one_says_no_findings()
    {
        var failed = await Comment("""
            {"contract_version":"result/v1","task_id":"run-2","status":"failed","summary":"s","claims":[],"evidence":[],"open_questions":[],
             "error":{"code":"checkout_invalid","message":"commit abc is not in the checkout","retryable":false,"action":"Fetch the commit."},
             "usage":{"usd":null}}
            """);
        Assert.Contains("The review failed (`checkout_invalid`): commit abc is not in the checkout\n\nAction: Fetch the commit.", failed, StringComparison.Ordinal);
        Assert.EndsWith("Run `run-2`.\n", failed, StringComparison.Ordinal);

        var clean = await Comment("""
            {"contract_version":"result/v1","task_id":"run-3","status":"completed","summary":"Nothing wrong.","claims":[],"evidence":[],"open_questions":[],"usage":{"usd":0.5}}
            """);
        Assert.Contains("Nothing wrong.\n\nNo findings.", clean, StringComparison.Ordinal);
    }

    // review-gate.sh

    [Theory]
    [InlineData("none", "completed", 0.95, 0)]
    [InlineData("high", "completed", 0.95, 1)]
    [InlineData("high", "completed", 0.8, 1)]
    [InlineData("high", "completed", 0.79, 0)]
    [InlineData("high", "failed", 0.95, 0)]
    [InlineData("severe", "completed", 0.95, 2)]
    public async Task The_gate_fails_on_a_confident_finding_only_when_asked_to(string failOn, string status, double confidence, int expected)
    {
        var file = _dir.Write("result.json", $$"""{"status":"{{status}}","claims":[{"text":"t","evidence":["e"],"confidence":{{confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}]}""");
        var (exit, _, _) = await Bash("review-gate.sh", [file, failOn]);

        Assert.Equal(expected, exit);
    }

    // review-post.sh

    private (string Bin, string Log) FakeGh(string comments, params string[] refuse)
    {
        var log = Path.Combine(_dir.Path, "gh.log");
        var list = _dir.Write("comments.json", comments);
        var refused = string.Join('|', refuse.Select(id => $"*comments/{id}\\ *"));
        var bin = _dir.Write("bin/gh", $$"""
            #!/bin/sh
            echo "$*" >> "{{log}}"
            case "$*" in
              "api --paginate repos/o/r/issues/7/comments") cat "{{list}}" ;;
              {{(refuse.Length > 0 ? refused + ") exit 1 ;;" : "NONE) ;;")}}
              *) echo '{}' ;;
            esac
            """);
        ScriptFile.Seal(bin);
        return (Path.GetDirectoryName(bin)!, log);
    }

    private Task<(int Exit, string Out, string Err)> Post(string bin, string body = "<!-- chargehand-review -->\nhi\n") =>
        Bash("review-post.sh", ["o/r", "7", _dir.Write("comment.md", body)], new() { ["PATH"] = $"{bin}:{Environment.GetEnvironmentVariable("PATH")}" });

    [Fact]
    public async Task A_marked_comment_is_edited_and_a_foreign_one_with_the_marker_is_skipped()
    {
        var (bin, log) = FakeGh("""
            [{"id":10,"body":"unrelated"},{"id":11,"body":"<!-- chargehand-review -->\nspoofed"},{"id":12,"body":"<!-- chargehand-review -->\nold"}]
            """, "11");
        var (exit, _, stderr) = await Post(bin);

        Assert.True(exit == 0, stderr);
        var calls = await File.ReadAllLinesAsync(log);
        Assert.Contains(calls, c => c.StartsWith("api -X PATCH repos/o/r/issues/comments/11 ", StringComparison.Ordinal));
        Assert.Contains(calls, c => c.StartsWith("api -X PATCH repos/o/r/issues/comments/12 ", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, c => c.Contains("-X POST", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, c => c.Contains("comments/10", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_no_marked_comment_it_posts_a_new_one()
    {
        var (bin, log) = FakeGh("""[{"id":10,"body":"unrelated"}]""");
        var (exit, _, stderr) = await Post(bin);

        Assert.True(exit == 0, stderr);
        Assert.Contains(await File.ReadAllLinesAsync(log), c => c.StartsWith("api -X POST repos/o/r/issues/7/comments ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_comment_without_the_marker_is_not_posted()
    {
        var (bin, log) = FakeGh("[]");
        var (exit, _, _) = await Post(bin, "no marker\n");

        Assert.Equal(2, exit);
        Assert.False(File.Exists(log));
    }

    // review-remote.sh

    private async Task<(int Exit, JsonElement Result, string Err)> Remote(string url, string key, RunRequest request, int timeoutSeconds = 30)
    {
        var requestFile = _dir.Write("request.json", JsonSerializer.Serialize(request, ContractJson.Options));
        var (exit, stdout, stderr) = await Bash("review-remote.sh", [requestFile], new()
        {
            ["CHARGEHAND_URL"] = url,
            ["CHARGEHAND_API_KEY"] = key,
            ["REVIEW_TIMEOUT_SECONDS"] = timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["REVIEW_POLL_SECONDS"] = "1",
            ["REVIEW_WAIT_SECONDS"] = "0",
        });
        var result = Json(stdout);
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.Result, result));
        return (exit, result, stderr);
    }

    [Fact]
    public async Task A_run_that_outlasts_the_first_call_is_polled_until_its_result()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Hold = new() };
        await using var server = await TestServer.StartAsync(runtime);
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            runtime.Hold.SetResult();
        });
        var (exit, result, stderr) = await Remote(server.BaseAddress.ToString(), TestServer.Key, Runs.DraftRequest());

        Assert.True(exit == 0, stderr);
        Assert.Equal("completed", result.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_wrong_key_unreachable_server_and_timeout_each_give_a_failed_result_that_names_no_address()
    {
        await using var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply) { Hold = new() });

        var (exit, result, _) = await Remote(server.BaseAddress.ToString(), "wrong", Runs.DraftRequest());
        Assert.Equal(1, exit);
        Assert.Equal("failed", result.GetProperty("status").GetString());
        Assert.Contains("refused the key", result.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);

        (exit, result, var stderr) = await Remote("http://127.0.0.1:1", "k", Runs.DraftRequest());
        Assert.Equal(1, exit);
        Assert.Equal("runtime_unavailable", result.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("127.0.0.1", result.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("curl", stderr, StringComparison.OrdinalIgnoreCase);

        (exit, result, _) = await Remote(server.BaseAddress.ToString(), TestServer.Key, Runs.DraftRequest(), timeoutSeconds: 2);
        Assert.Equal(1, exit);
        Assert.Equal("deadline_exceeded", result.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("run ", result.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_the_server_refuses_is_reported_with_its_reasons()
    {
        await using var server = await TestServer.StartAsync(new ScriptedRuntime(Runs.DraftReply));
        var (exit, result, _) = await Remote(server.BaseAddress.ToString(), TestServer.Key, new RunRequest("request/v1", "x", new RequestContext(false, "no-such-preset")));

        Assert.Equal(1, exit);
        Assert.Equal("invalid_request", result.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("no-such-preset", result.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    // .github/workflows/review.yml

    [Fact]
    public void The_reusable_workflow_pins_actions_and_keeps_pull_request_text_out_of_shell_commands()
    {
        var lines = File.ReadAllLines(Repo.Path(".github", "workflows", "review.yml"));
        var inRun = -1;
        foreach (var (line, i) in lines.Select((l, i) => (l, i)))
        {
            var indent = line.Length - line.TrimStart().Length;
            if (inRun >= 0 && (line.Trim().Length == 0 || indent > inRun))
            {
                Assert.True(!line.Contains("${{", StringComparison.Ordinal), $"line {i + 1} interpolates an expression into a shell command: {line.Trim()}");
                continue;
            }
            inRun = -1;
            var run = RunKey().Match(line);
            if (run.Success)
            {
                Assert.True(!run.Groups["rest"].Value.Contains("${{", StringComparison.Ordinal), $"line {i + 1} interpolates an expression into a shell command");
                inRun = run.Groups["rest"].Value is "|" or ">" or "|-" ? indent : -1;
            }
            var uses = UsesKey().Match(line);
            if (uses.Success)
                Assert.True(PinnedUse().IsMatch(uses.Groups["ref"].Value), $"line {i + 1}: {uses.Groups["ref"].Value} is not pinned by commit sha");
        }
        var text = string.Join('\n', lines);
        Assert.Contains("pull-requests: write", text, StringComparison.Ordinal);
        Assert.Contains("persist-credentials: false", text, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^\s*(- )?run:\s*(?<rest>.*)$")]
    private static partial Regex RunKey();

    [GeneratedRegex(@"^\s*(- )?uses:\s*(?<ref>\S+)")]
    private static partial Regex UsesKey();

    [GeneratedRegex(@"^[\w.-]+/[\w./-]+@[0-9a-f]{40}$")]
    private static partial Regex PinnedUse();
}
