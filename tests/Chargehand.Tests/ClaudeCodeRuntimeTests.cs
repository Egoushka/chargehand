using System.Diagnostics;
using System.Text.Json;
using Chargehand.Budget;
using Chargehand.ClaudeCode;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Nodes;
using Chargehand.Runtime;
using Chargehand.Verification;

namespace Chargehand.Tests;

public sealed class ClaudeCodeRuntimeTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static readonly NodeSpec Spec = new("/w", "build", new ModelRef("anthropic", "claude-sonnet-5"), [], new Dictionary<string, string>());

    [Fact]
    public void Default_preset_rules_become_a_read_only_catalog_without_shell()
    {
        var (tools, allowed, disallowed) = ClaudeCodeWorkerRuntime.Permissions(Preset.Load(Repo.Path("presets"), "default").NodeKinds["worker"].Rules);
        Assert.DoesNotContain("Edit", tools);
        Assert.DoesNotContain("Write", tools);
        Assert.DoesNotContain("WebFetch", tools);
        Assert.DoesNotContain("Agent", tools);
        Assert.DoesNotContain("AskUserQuestion", tools);
        Assert.Contains("Read", allowed);
        Assert.DoesNotContain("Bash", tools);
        Assert.DoesNotContain(allowed, a => a.StartsWith("Bash", StringComparison.Ordinal));
        Assert.Contains("Read(**/*.env)", disallowed);
        // Deny wins in Claude Code, so the exception inside *.env.* stays denied (fails closed).
        Assert.Contains("Read(**/*.env.*)", disallowed);
    }

    [Fact]
    public void Deny_everything_leaves_an_empty_catalog()
    {
        var (tools, allowed, disallowed) = ClaudeCodeWorkerRuntime.Permissions(Preset.Load(Repo.Path("presets"), "draft").NodeKinds["draft"].Rules);
        Assert.Empty(tools);
        Assert.Empty(allowed);
        Assert.Empty(disallowed);
    }

    [Fact]
    public void Content_blocks_of_one_message_merge_and_tool_results_join_their_caller()
    {
        var s = new ClaudeCodeWorkerRuntime.Session("s", Spec, null);
        Apply(s, """{"type":"assistant","message":{"id":"msg_1","content":[{"type":"text","text":"Looking. "}],"usage":{"input_tokens":3,"output_tokens":1,"cache_read_input_tokens":100,"cache_creation_input_tokens":50}}}""");
        Apply(s, """{"type":"assistant","message":{"id":"msg_1","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls"}}],"usage":{"input_tokens":3,"output_tokens":9,"cache_read_input_tokens":100,"cache_creation_input_tokens":50}}}""");
        Apply(s, """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"a.txt"}]}}""");
        Apply(s, """{"type":"assistant","message":{"id":"msg_2","content":[{"type":"text","text":"DONE"}],"usage":{"input_tokens":1,"output_tokens":2,"cache_read_input_tokens":160,"cache_creation_input_tokens":0}}}""");
        Assert.True(Apply(s, """{"type":"result","subtype":"success","is_error":false,"result":"DONE","usage":{"input_tokens":4,"output_tokens":211,"cache_read_input_tokens":260,"cache_creation_input_tokens":50}}"""));

        var (first, second, idle) = (s.Messages[0], s.Messages[1], s.Messages[2]);
        Assert.Equal(("msg_1", "Looking. ", 9L, 100L, 50L), (first.Id, first.Text, first.Tokens!.Output, first.Tokens.CacheRead, first.Tokens.CacheWrite));
        Assert.Contains("\"command\":\"ls\"", first.ToolOutput);
        Assert.Contains("a.txt", first.ToolOutput);
        Assert.Equal(["a.txt"], first.ToolResults);
        Assert.NotNull(first.Completed);
        Assert.Equal("anthropic/claude-sonnet-5", second.Model);
        Assert.NotNull(second.Completed);
        // Assistant events carry the message_start stub; the result's total output lands on the turn's last call.
        Assert.Equal(211 - 9, second.Tokens!.Output);
        Assert.Equal((WorkerMessageKind.Idle, IdleOutcome.Succeeded), (idle.Kind, idle.Outcome));
    }

    [Fact]
    public void A_tool_result_given_as_content_blocks_is_kept_as_text()
    {
        var s = new ClaudeCodeWorkerRuntime.Session("s", Spec, null);
        Apply(s, """{"type":"assistant","message":{"id":"msg_1","content":[{"type":"tool_use","id":"t1","name":"mcp__fake__echo_fact","input":{}}],"usage":{"input_tokens":3,"output_tokens":1}}}""");
        Apply(s, """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":[{"type":"text","text":"echo_fact ok: \"spike\" fact"}]}]}}""");
        Assert.Equal(["echo_fact ok: \"spike\" fact"], s.Messages[0].ToolResults);
    }

    /// <summary>ADR 0026: an unset model (the session ran with the CLI's own default, no --model pinned) reports no
    /// specific model on the message, rather than fabricating one.</summary>
    [Fact]
    public void An_unset_model_reports_no_model_on_the_message()
    {
        var unpinned = Spec with { Model = null };
        var s = new ClaudeCodeWorkerRuntime.Session("s", unpinned, null);
        Apply(s, """{"type":"assistant","message":{"id":"msg_1","content":[{"type":"text","text":"Hi"}],"usage":{"input_tokens":1,"output_tokens":1,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}""");
        Assert.Null(s.Messages[0].Model);
    }

    [Fact]
    public void An_error_result_is_a_failed_idle_and_a_compact_run_records_its_usage()
    {
        var s = new ClaudeCodeWorkerRuntime.Session("s", Spec, null);
        Apply(s, """{"type":"result","subtype":"success","is_error":true,"result":"Not logged in"}""");
        Assert.Equal((IdleOutcome.Failed, "Not logged in"), (s.Messages[^1].Outcome, s.Messages[^1].Error));

        Apply(s, """{"type":"system","subtype":"compact_boundary"}""", compact: true);
        Apply(s, """{"type":"result","subtype":"success","is_error":false,"usage":{"input_tokens":10,"output_tokens":400,"cache_read_input_tokens":9000,"cache_creation_input_tokens":0}}""", compact: true);
        Assert.Equal((WorkerMessageKind.Compaction, 400L), (s.Messages[^1].Kind, s.Messages[^1].Tokens!.Output));
    }

    /// <summary>The error_* subtypes of Claude Code's result event carry no <c>result</c> (2.1.283 emits <c>errors</c> instead), and a
    /// <c>success</c> one flagged is_error can carry it empty: the idle message still names the cause.</summary>
    [Theory]
    [InlineData("""{"type":"result","subtype":"error_max_turns","is_error":true,"num_turns":3,"errors":["Reached maximum number of turns (3)"]}""",
        "claude reported error_max_turns after 3 turns: Reached maximum number of turns (3)")]
    [InlineData("""{"type":"result","subtype":"error_max_budget_usd","is_error":true,"num_turns":1,"errors":["Reached maximum budget ($0.5)","second"]}""",
        "claude reported error_max_budget_usd after 1 turn: Reached maximum budget ($0.5); second")]
    [InlineData("""{"type":"result","subtype":"error_during_execution","is_error":true}""", "claude reported error_during_execution")]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"result":"","num_turns":2,"api_error_status":529}""",
        "claude reported success with is_error after 2 turns (API status 529)")]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"result":null}""", "claude reported success with is_error")]
    [InlineData("""{"type":"result","is_error":true}""", "claude reported an error result")]
    public void An_error_result_without_result_text_still_names_its_cause(string result, string expected)
    {
        var s = new ClaudeCodeWorkerRuntime.Session("s", Spec, null);
        Assert.True(Apply(s, result));
        Assert.Equal((IdleOutcome.Failed, expected), (s.Messages[^1].Outcome, s.Messages[^1].Error));
    }

    [Fact]
    public void An_error_result_with_result_text_keeps_that_text_and_a_successful_one_has_no_error()
    {
        var s = new ClaudeCodeWorkerRuntime.Session("s", Spec, null);
        Apply(s, """{"type":"result","subtype":"error_max_turns","is_error":true,"num_turns":3,"result":"Stopped early","errors":["ignored"]}""");
        Assert.Equal("Stopped early", s.Messages[^1].Error);
        Apply(s, """{"type":"result","subtype":"success","is_error":false,"result":"DONE"}""");
        Assert.Equal(IdleOutcome.Succeeded, s.Messages[^1].Outcome);
        Assert.Null(s.Messages[^1].Error);
    }

    /// <summary>The bug behind this: an error result with no <c>result</c> and no assistant message reached WorkerNode as a failed
    /// session with no reason, which it reported in OpenCode's terms.</summary>
    [Fact]
    public async Task A_max_turns_result_reaches_the_caller_as_its_cause_not_as_a_missing_model_call()
    {
        var claude = FakeClaude("""{"type":"result","subtype":"error_max_turns","is_error":true,"num_turns":3,"errors":["Reached maximum number of turns (3)"]}""");
        var rt = await ClaudeCodeWorkerRuntime.ConnectAsync(claude, "2.1.195", new ClaudeCodeCredential("k", false), CancellationToken.None);
        var request = new NodeRequest("run-1", "worker", new string('0', 32), Spec with { Directory = _dir.Path }, [], "Task",
            new PromptChain([], new AsSent("2.0.16", "build", "anthropic/claude-sonnet-5", "2026-09-26")),
            _dir.Path, "abc1234", [], "", 1.00m, TimeSpan.FromMinutes(1));

        var e = (await new WorkerNode(rt, new PriceTable(new Dictionary<string, ModelPrice>()), new GitEvidenceResolver(), TimeSpan.FromMilliseconds(10))
            .RunAsync(request, CancellationToken.None)).Contract.Error!;

        Assert.Equal("worker ended failed: claude reported error_max_turns after 3 turns: Reached maximum number of turns (3)", e.Message);
        Assert.DoesNotContain("model call", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenCode", e.Action, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_turn_runs_the_cli_with_pinned_flags_and_reads_its_stream()
    {
        var repo = _dir.Write("repo/a.txt", "one\n");
        var repoDir = Path.GetDirectoryName(repo)!;
        Git(repoDir, "init", "-q");
        Git(repoDir, "add", "-A");
        Git(repoDir, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-qm", "init");
        var claude = FakeClaude("""
            {"type":"system","subtype":"init"}
            {"type":"assistant","message":{"id":"msg_1","content":[{"type":"text","text":"DONE"}],"usage":{"input_tokens":1,"output_tokens":2,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}
            {"type":"result","subtype":"success","is_error":false,"result":"DONE"}
            """);

        IWorkerRuntime rt = await ClaudeCodeWorkerRuntime.ConnectAsync(claude, "2.1.195", new ClaudeCodeCredential("sk-test", false), CancellationToken.None);
        var session = await rt.CreateAsync(new NodeSpec(repoDir, "build", new ModelRef("anthropic", "claude-sonnet-5"),
            [new PermissionRule("*", "*", PermissionEffect.Allow), new PermissionRule("edit", "*", PermissionEffect.Deny)], new Dictionary<string, string>()), CancellationToken.None);
        await rt.SetInstructionAsync(session.Id, "core", "Be terse.", CancellationToken.None);
        await rt.SubmitAsync(session.Id, "Say DONE", CancellationToken.None);

        Assert.Equal(IdleOutcome.Succeeded, await rt.AwaitIdleAsync(session.Id, CancellationToken.None));
        var messages = await rt.ReadMessagesAsync(session.Id, CancellationToken.None);
        Assert.Equal([WorkerMessageKind.Idle, WorkerMessageKind.Assistant, WorkerMessageKind.User], messages.Select(m => m.Kind));
        var args = File.ReadAllLines(Path.Combine(_dir.Path, "args.txt"));
        Assert.Equal(session.Id, args[Array.IndexOf(args, "--session-id") + 1]);
        Assert.Equal("Be terse.", args[Array.IndexOf(args, "--append-system-prompt") + 1]);
        Assert.Equal("dontAsk", args[Array.IndexOf(args, "--permission-mode") + 1]);
        Assert.Contains("--bare", args);
        Assert.DoesNotContain("Edit", args[Array.IndexOf(args, "--tools") + 1].Split(','));
        Assert.Equal("Say DONE", File.ReadAllText(Path.Combine(_dir.Path, "stdin.txt")));
        Assert.Equal("api=sk-test oauth= base=", File.ReadAllText(Path.Combine(_dir.Path, "key.txt")).Trim());
        await Assert.ThrowsAsync<InvalidOperationException>(() => rt.SetInstructionAsync(session.Id, "core", "late", CancellationToken.None));

        await rt.SubmitAsync(session.Id, "again", CancellationToken.None);
        await rt.AwaitIdleAsync(session.Id, CancellationToken.None);
        args = File.ReadAllLines(Path.Combine(_dir.Path, "args.txt"));
        Assert.Equal(session.Id, args[Array.IndexOf(args, "--resume") + 1]);

        var fork = await rt.ForkAsync(session.Id, messages[^1].Id, CancellationToken.None);
        Assert.NotEqual(session.Id, fork.Id);
        await Assert.ThrowsAsync<NotSupportedException>(() => rt.ForkAsync(session.Id, messages[1].Id, CancellationToken.None));

        File.WriteAllText(repo, "one\ntwo\n");
        _dir.Write("repo/new.txt", "fresh\n");
        var diff = await rt.DiffAsync(session.Id, CancellationToken.None);
        Assert.Equal([("a.txt", "modified", 1), ("new.txt", "added", 1)], diff.Select(d => (d.File, d.Status, d.Additions)).OrderBy(d => d.File));
        Assert.Contains("@@", diff[0].Patch);
    }

    [Fact]
    public async Task A_subscription_uses_the_oauth_token_without_bare_and_drops_an_inherited_api_key()
    {
        var claude = FakeClaude("""{"type":"result","subtype":"success","is_error":false,"result":"hi"}""");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "inherited");
        Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "https://inherited.invalid");
        try
        {
            IWorkerRuntime rt = await ClaudeCodeWorkerRuntime.ConnectAsync(claude, "2.1.195", new ClaudeCodeCredential("oauth-test", true), CancellationToken.None,
                new Uri("https://gateway.example.com/anthropic/"));
            var session = await rt.CreateAsync(Spec with { Directory = _dir.Path }, CancellationToken.None);
            await rt.SubmitAsync(session.Id, "hi", CancellationToken.None);
            Assert.Equal(IdleOutcome.Succeeded, await rt.AwaitIdleAsync(session.Id, CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", null);
        }
        var args = File.ReadAllLines(Path.Combine(_dir.Path, "args.txt"));
        Assert.DoesNotContain("--bare", args);
        Assert.Equal("", args[Array.IndexOf(args, "--setting-sources") + 1]);
        Assert.Equal("api= oauth=oauth-test base=https://gateway.example.com/anthropic", File.ReadAllText(Path.Combine(_dir.Path, "key.txt")).Trim());
    }

    /// <summary>No credential configured: the CLI's own login. Nothing is set, inherited credential variables are
    /// removed so the CLI cannot pick another account, and --bare (which never reads the keychain) is not used.</summary>
    [Fact]
    public async Task A_cli_login_sets_no_credential_strips_inherited_ones_and_skips_bare()
    {
        var claude = FakeClaude("""{"type":"result","subtype":"success","is_error":false,"result":"hi"}""");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "inherited");
        Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", "inherited");
        try
        {
            IWorkerRuntime rt = await ClaudeCodeWorkerRuntime.ConnectAsync(claude, "2.1.195", ClaudeCodeCredential.CliLogin, CancellationToken.None);
            var session = await rt.CreateAsync(Spec with { Directory = _dir.Path }, CancellationToken.None);
            await rt.SubmitAsync(session.Id, "hi", CancellationToken.None);
            Assert.Equal(IdleOutcome.Succeeded, await rt.AwaitIdleAsync(session.Id, CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", null);
        }
        var args = File.ReadAllLines(Path.Combine(_dir.Path, "args.txt"));
        Assert.DoesNotContain("--bare", args);
        Assert.Equal("", args[Array.IndexOf(args, "--setting-sources") + 1]);
        Assert.Equal("api= oauth= base=", File.ReadAllText(Path.Combine(_dir.Path, "key.txt")).Trim());
    }

    [Fact]
    public async Task A_cli_that_is_not_signed_in_is_runtime_unavailable()
    {
        var claude = FakeClaude("", signedIn: false);
        var e = await Assert.ThrowsAsync<ChargehandException>(() => ClaudeCodeWorkerRuntime.ConnectAsync(claude, "2.1.195", ClaudeCodeCredential.CliLogin, CancellationToken.None));
        Assert.Equal(ErrorCode.RuntimeUnavailable, e.Code);
        Assert.Contains("sign in", e.Action);
        Assert.Contains("CLAUDE_CODE_OAUTH_TOKEN", e.Action);
        Assert.Contains("ANTHROPIC_API_KEY", e.Action);
    }

    [Fact]
    public async Task Generate_runs_without_thinking_tools_or_the_agent_system_prompt()
    {
        var claude = FakeClaude("""{"type":"result","subtype":"success","is_error":false,"result":"{\"ok\":true}"}""");
        var rt = await ClaudeCodeWorkerRuntime.ConnectAsync(claude, "2.1.195", new ClaudeCodeCredential("k", false), CancellationToken.None);

        Assert.Equal("""{"ok":true}""", await rt.GenerateAsync(new ModelRef("anthropic", "claude-haiku-4-5"), "spec please", CancellationToken.None));
        var args = File.ReadAllLines(Path.Combine(_dir.Path, "args.txt"));
        Assert.Equal("", args[Array.IndexOf(args, "--tools") + 1]);
        Assert.Contains("--system-prompt", args);
        Assert.Contains("--no-session-persistence", args);
        Assert.Equal("0", File.ReadAllText(Path.Combine(_dir.Path, "thinking.txt")).Trim());
    }

    [Fact]
    public async Task Generate_names_the_cause_of_an_error_result_that_has_no_result_text()
    {
        var claude = FakeClaude("""{"type":"result","subtype":"error_max_turns","is_error":true,"num_turns":1,"errors":["Reached maximum number of turns (1)"]}""");
        var rt = await ClaudeCodeWorkerRuntime.ConnectAsync(claude, "2.1.195", new ClaudeCodeCredential("k", false), CancellationToken.None);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => rt.GenerateAsync(null, "spec please", CancellationToken.None));
        Assert.Equal("claude -p exited 0: claude reported error_max_turns after 1 turn: Reached maximum number of turns (1)", e.Message);
    }

    [Fact]
    public async Task A_version_other_than_the_pin_is_refused()
    {
        var claude = FakeClaude("");
        var e = await Assert.ThrowsAsync<ChargehandException>(() => ClaudeCodeWorkerRuntime.ConnectAsync(claude, "9.9.9", new ClaudeCodeCredential("k", false), CancellationToken.None));
        Assert.Equal(ErrorCode.RuntimeVersionMismatch, e.Code);
        Assert.NotNull(e.Action);
    }

    [Fact]
    public async Task A_missing_binary_is_runtime_unavailable()
    {
        var e = await Assert.ThrowsAsync<ChargehandException>(() => ClaudeCodeWorkerRuntime.ConnectAsync(Path.Combine(_dir.Path, "no-claude"), "2.1.195", new ClaudeCodeCredential("k", false), CancellationToken.None));
        Assert.Equal(ErrorCode.RuntimeUnavailable, e.Code);
        Assert.NotNull(e.Action);
    }

    private static bool Apply(ClaudeCodeWorkerRuntime.Session s, string json, bool compact = false) =>
        ClaudeCodeWorkerRuntime.Apply(s, JsonDocument.Parse(json).RootElement, compact);

    /// <summary>A stand-in CLI: prints the pinned version, answers auth status, records its arguments, stdin and key,
    /// and replays events.</summary>
    private string FakeClaude(string events, bool signedIn = true)
    {
        _dir.Write("events.jsonl", events + "\n");
        var script = _dir.Write("claude", $"""
            #!/bin/sh
            if [ "$1" = "--version" ]; then echo "2.1.195 (Claude Code)"; exit 0; fi
            if [ "$1" = "auth" ]; then exit {(signedIn ? 0 : 1)}; fi
            printf '%s\n' "$@" > "{_dir.Path}/args.txt"
            cat > "{_dir.Path}/stdin.txt"
            echo "api=$ANTHROPIC_API_KEY oauth=$CLAUDE_CODE_OAUTH_TOKEN base=$ANTHROPIC_BASE_URL" > "{_dir.Path}/key.txt"
            echo "$MAX_THINKING_TOKENS" > "{_dir.Path}/thinking.txt"
            cat "{_dir.Path}/events.jsonl"
            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }
}
