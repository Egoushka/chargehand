using System.Text.Json;
using Chargehand.Modules.Sessions.Api;
using static Chargehand.Modules.Sessions.Tests.SessionDriverTests;

namespace Chargehand.Modules.Sessions.Tests;

/// <summary>The bare arm of the benchmark (ADR 0079): the goal goes to Claude Code as written, nothing steers or checks the session, and the working tree is committed once at the end.</summary>
public class SessionBareTests
{
    private static SessionTask BareTask() => Task() with { Bare = true };

    [Fact]
    public void The_bare_command_line_is_the_goal_and_the_tools_and_nothing_else()
    {
        var args = SessionDriver.ClaudeArgs(BareTask() with { Model = "claude-sonnet-5-5", MaxUsd = 2.5m }, "/opt/plugin", "/tmp/mcp.json", "/tmp/prompt.md", "/tmp/settings.json");
        Assert.Equal(["-p", "Add a retry to the fetch client", "--permission-mode", "dontAsk", "--allowedTools", "Bash,Read,Edit,Write,Glob,Grep",
            "--strict-mcp-config", "--mcp-config", "/tmp/mcp.json", "--output-format", "stream-json", "--verbose", "--model", "claude-sonnet-5-5", "--max-budget-usd", "2.5"], args);
        Assert.DoesNotContain("--plugin-dir", args);
        Assert.DoesNotContain("--append-system-prompt-file", args);
        Assert.DoesNotContain("--settings", args);
    }

    [Fact]
    public void Only_a_bare_task_says_so_in_task_json_so_an_older_session_image_still_parses_the_others()
    {
        Assert.DoesNotContain("bare", JsonSerializer.Serialize(Task(), SessionCli.Json));
        Assert.True(JsonSerializer.Deserialize<SessionTask>(JsonSerializer.Serialize(BareTask(), SessionCli.Json), SessionCli.Json)!.Bare);
    }

    [Fact]
    public async Task A_bare_session_gets_the_goal_and_no_hook_check_or_report_and_its_files_are_committed_once()
    {
        using var w = new Workspace(commit: false);
        w.Git("checkout", "-q", "-b", "chargehand/run1");
        var report = "```json\n{\"summary\":\"s\",\"claims\":[]}\n```\nNEEDS_INPUT:\n- which?";
        var script = "printf '%s\\n' \"$@\" > ../out/args.txt\nenv > ../out/env.txt\necho changed > c.txt\n" + Emit(Init, Assistant("m1", report), Result());
        var task = BareTask() with { Acceptance = [new("false")], CheckRetries = 2 };
        var outcome = await SessionDriver.RunAsync(Options(w, script, task), default);

        Assert.Equal((SessionStatus.Completed, true, true, 1), (outcome.Status, outcome.Bare, outcome.BundleWritten, outcome.Commits));
        Assert.Empty(outcome.Questions);
        Assert.Null(outcome.Checks);
        Assert.Equal("Changes left by the agent", w.Git("log", "-1", "--format=%s").Trim());
        Assert.Equal("changed", w.Git("show", "chargehand/run1:c.txt").Trim());
        var args = File.ReadAllLines(Path.Combine(w.Out, "args.txt"));
        Assert.Equal("Add a retry to the fetch client", args[1]);
        Assert.DoesNotContain("--plugin-dir", args);
        Assert.DoesNotContain(File.ReadAllLines(Path.Combine(w.Out, "env.txt")), l => l.StartsWith("CHARGEHAND_DRIVEN=", StringComparison.Ordinal) || l.StartsWith("CHARGEHAND_BRANCH=", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(w.Out, "driven-report.json")));
        Assert.False(File.Exists(Path.Combine(w.Out, "handoff.json")));
    }

    [Fact]
    public async Task A_bare_session_that_changed_nothing_adds_no_commit()
    {
        using var w = new Workspace(commit: false);
        w.Git("checkout", "-q", "-b", "chargehand/run1");
        var outcome = await SessionDriver.RunAsync(Options(w, Emit(Init, Assistant("m1", "this cannot be done"), Result()), BareTask()), default);

        Assert.Equal((SessionStatus.Completed, 0), (outcome.Status, outcome.Commits));
        Assert.Equal("base", w.Git("log", "-1", "--format=%s").Trim());
    }

    [Fact]
    public async Task A_bare_session_that_is_stopped_is_not_salvaged_with_a_handoff()
    {
        using var w = new Workspace(commit: false);
        w.Git("checkout", "-q", "-b", "chargehand/run1");
        var outcome = await SessionDriver.RunAsync(Options(w, "echo c > c.txt\n" + Emit(Init) + "\nsleep 60", BareTask() with { NoProgressSeconds = 1 }), default);

        Assert.Equal(("no_progress", true), (outcome.Reason, outcome.Bare));
        Assert.False(File.Exists(Path.Combine(w.Out, "handoff.json")));
        Assert.False(File.Exists(Path.Combine(w.Out, "progress.md")));
    }

    [Fact]
    public async Task A_session_that_is_not_bare_does_not_say_it_was()
    {
        using var w = new Workspace();
        var outcome = await SessionDriver.RunAsync(Options(w, Emit(Init, Assistant("m1", "done"), Result()), Task()), default);
        Assert.False(outcome.Bare);
        Assert.DoesNotContain("bare", File.ReadAllText(Path.Combine(w.Out, "session-outcome.json")));
    }
}
