using System.Diagnostics;
using System.Text.Json;
using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary>The in-container driver (ADR 0039): runs headless Claude Code, ends it when it stalls, and leaves a bundle, a report and an outcome.</summary>
public class SessionDriverTests
{
    private const string Canary = "canary-secret-value-123";

    private static SessionTask Task(int noProgressSeconds = 60, long? maxTokens = null) =>
        new("run1", "Add a retry to the fetch client", "chargehand/run1", MaxTurns: 80, MaxMinutes: 5, NoProgressSeconds: noProgressSeconds, MaxTokens: maxTokens);

    private sealed class Workspace : IDisposable
    {
        public TempDir Dir { get; } = new();
        public string Work => Path.Combine(Dir.Path, "work");
        public string Out => Path.Combine(Dir.Path, "out");

        public Workspace(bool commit = true)
        {
            Directory.CreateDirectory(Work);
            Directory.CreateDirectory(Out);
            Git("init", "-q", "-b", "main");
            Git("config", "user.email", "t@example.test");
            Git("config", "user.name", "t");
            File.WriteAllText(Path.Combine(Work, "a.txt"), "a");
            Git("add", "-A");
            Git("commit", "-q", "-m", "base");
            if (commit)
            {
                Git("checkout", "-q", "-b", "chargehand/run1");
                File.WriteAllText(Path.Combine(Work, "b.txt"), "b");
                Git("add", "-A");
                Git("commit", "-q", "-m", "feat: the change");
            }
        }

        public string Git(params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = Work, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {p.StandardError.ReadToEnd()}");
            return output;
        }

        public void Dispose() => Dir.Dispose();
    }

    private static SessionOptions Options(Workspace w, string script, SessionTask task, string[]? secrets = null) =>
        new(w.Work, w.Out, task, new SessionCommand("/bin/sh", [w.Dir.Write("claude.sh", script)]), secrets ?? [Canary], PromptText: "system prompt", McpUrl: null, McpToken: null);

    private const string Init = """{"type":"system","subtype":"init","session_id":"s"}""";

    private static string Assistant(string id, string text, string usage = """{"input_tokens":100,"output_tokens":20}""") =>
        JsonSerializer.Serialize(new { type = "assistant", message = new { id, content = new[] { new { type = "text", text } }, usage = JsonDocument.Parse(usage).RootElement } });

    private static string Result(string text = "done") =>
        JsonSerializer.Serialize(new
        {
            type = "result",
            is_error = false,
            result = text,
            total_cost_usd = 0.12,
            usage = new { input_tokens = 150, output_tokens = 40, cache_read_input_tokens = 500 },
        });

    private static string Emit(params string[] lines) => string.Join("\n", lines.Select(l => $"cat <<'EOF_LINE'\n{l}\nEOF_LINE"));

    [Fact]
    public async Task A_finished_session_leaves_a_bundle_a_report_and_a_completed_outcome()
    {
        using var w = new Workspace();
        const string report = """{"summary":"added a retry","claims":[{"text":"the client retries","evidence":["src/a.cs:1-3"]}]}""";
        var script = Emit(Init, Assistant("m1", "working"), Assistant("m2", "All done.\n```json\n" + report + "\n```"), Result());
        var outcome = await SessionDriver.RunAsync(Options(w, script, Task()), default);
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Equal(2, outcome.Turns);
        Assert.Equal(150, outcome.InputTokens);
        Assert.Equal(40, outcome.OutputTokens);
        Assert.Equal(0.12m, outcome.CostUsd);
        Assert.True(outcome.BundleWritten);
        w.Git("bundle", "verify", Path.Combine(w.Out, "chargehand.bundle"));
        Assert.Contains("chargehand/run1", w.Git("bundle", "list-heads", Path.Combine(w.Out, "chargehand.bundle")));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(w.Out, "driven-report.json")));
        Assert.Equal("added a retry", doc.RootElement.GetProperty("summary").GetString());
        Assert.True(File.Exists(Path.Combine(w.Out, "session-outcome.json")));
        Assert.Contains("\"type\":\"result\"", File.ReadAllText(Path.Combine(w.Out, "stream.jsonl")));
    }

    [Fact]
    public async Task A_session_that_prints_nothing_is_stopped_as_stalled_and_leaves_no_process()
    {
        using var w = new Workspace();
        var sw = Stopwatch.StartNew();
        var outcome = await SessionDriver.RunAsync(Options(w, Emit(Init) + "\nsleep 60", Task(noProgressSeconds: 1)), default);
        Assert.Equal(SessionStatus.Stalled, outcome.Status);
        Assert.Equal("no_progress", outcome.Reason);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"took {sw.Elapsed}");
        Assert.True(outcome.BundleWritten, "work so far is bundled for the handover to decide on");
    }

    [Fact]
    public async Task Needs_input_ends_the_session_with_the_questions()
    {
        using var w = new Workspace();
        var script = Emit(Init, Assistant("m1", "I cannot continue.\nNEEDS_INPUT:\n1. Which database?\n2. Keep the old API?"), Result("stopped"));
        var outcome = await SessionDriver.RunAsync(Options(w, script, Task()), default);
        Assert.Equal(SessionStatus.NeedsInput, outcome.Status);
        Assert.Equal(["Which database?", "Keep the old API?"], outcome.Questions);
    }

    [Fact]
    public async Task A_secret_never_reaches_the_stored_stream_or_the_outcome()
    {
        using var w = new Workspace();
        var script = Emit(Init, Assistant("m1", $"the token is {Canary} oops"), Result($"and again {Canary}"));
        var outcome = await SessionDriver.RunAsync(Options(w, script, Task()), default);
        foreach (var file in Directory.GetFiles(w.Out))
            Assert.DoesNotContain(Canary, File.ReadAllText(file));
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Contains("[redacted]", File.ReadAllText(Path.Combine(w.Out, "stream.jsonl")));
    }

    [Fact]
    public async Task A_nonzero_exit_with_no_result_is_failed()
    {
        using var w = new Workspace();
        var outcome = await SessionDriver.RunAsync(Options(w, Emit(Init) + "\necho boom >&2\nexit 3", Task()), default);
        Assert.Equal(SessionStatus.Failed, outcome.Status);
        Assert.Equal(3, outcome.ExitCode);
    }

    [Fact]
    public async Task No_branch_means_no_bundle()
    {
        using var w = new Workspace(commit: false);
        var outcome = await SessionDriver.RunAsync(Options(w, Emit(Init, Assistant("m1", "hi"), Result()), Task()), default);
        Assert.False(outcome.BundleWritten);
        Assert.False(File.Exists(Path.Combine(w.Out, "chargehand.bundle")));
    }

    [Fact]
    public async Task Passing_the_token_cap_stops_the_session()
    {
        using var w = new Workspace();
        var script = Emit(Init, Assistant("m1", "big", """{"input_tokens":900,"output_tokens":300}""")) + "\nsleep 60";
        var outcome = await SessionDriver.RunAsync(Options(w, script, Task(maxTokens: 1000)), default);
        Assert.Equal(SessionStatus.TokenCap, outcome.Status);
        Assert.True(outcome.InputTokens + outcome.OutputTokens >= 1000);
    }

    [Fact]
    public void The_claude_command_line_is_headless_bounded_and_carries_no_secret()
    {
        var args = SessionDriver.ClaudeArgs(Task(maxTokens: 500_000) with { Model = "claude-sonnet-5-5", MaxUsd = 2.5m }, "/opt/plugin", "/tmp/mcp.json", "/tmp/prompt.md");
        string After(string flag) => args[args.ToList().IndexOf(flag) + 1];
        Assert.Contains("-p", args);
        Assert.StartsWith("/chargehand:change ", After("-p"));
        Assert.Contains("Add a retry to the fetch client", After("-p"));
        Assert.Equal("/opt/plugin", After("--plugin-dir"));
        Assert.Equal("dontAsk", After("--permission-mode"));
        Assert.Equal("stream-json", After("--output-format"));
        Assert.Contains("--verbose", args);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Equal("/tmp/mcp.json", After("--mcp-config"));
        Assert.Equal("/tmp/prompt.md", After("--append-system-prompt-file"));
        Assert.Equal("claude-sonnet-5-5", After("--model"));
        Assert.Equal("2.5", After("--max-budget-usd"));
        Assert.Contains("mcp__chargehand__orchestrate", After("--allowedTools"));
        Assert.DoesNotContain("--bare", args);
        Assert.DoesNotContain("--dangerously-skip-permissions", args);
        Assert.DoesNotContain(args, a => a.Contains("Bearer"));
    }

    [Fact]
    public void Without_a_dollar_cap_or_model_those_flags_are_absent()
    {
        var args = SessionDriver.ClaudeArgs(Task(), "/opt/plugin", "/tmp/mcp.json", "/tmp/prompt.md");
        Assert.DoesNotContain("--max-budget-usd", args);
        Assert.DoesNotContain("--model", args);
    }

    [Fact]
    public void A_goal_starting_with_a_slash_command_cannot_replace_the_skill()
    {
        var args = SessionDriver.ClaudeArgs(Task() with { Goal = "/login now" }, "/opt/plugin", "/tmp/mcp.json", "/tmp/prompt.md");
        Assert.StartsWith("/chargehand:change ", args[args.ToList().IndexOf("-p") + 1]);
    }

    [Fact]
    public void The_mcp_config_names_the_chargehand_server_over_http_with_the_run_token()
    {
        var json = SessionDriver.McpConfig("http://chargehand.internal:4300/v1/mcp", "run-token-1");
        using var doc = JsonDocument.Parse(json);
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("chargehand");
        Assert.Equal("http", server.GetProperty("type").GetString());
        Assert.Equal("http://chargehand.internal:4300/v1/mcp", server.GetProperty("url").GetString());
        Assert.Equal("Bearer run-token-1", server.GetProperty("headers").GetProperty("Authorization").GetString());
    }
}
