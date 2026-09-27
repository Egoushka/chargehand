using System.Diagnostics;
using System.Text.Json;
using Chargehand.ClaudeCode;
using Chargehand.Config;
using Chargehand.Runtime;

namespace Chargehand.Tests;

public sealed class ClaudeCodeRuntimeTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static readonly NodeSpec Spec = new("/w", "build", new ModelRef("anthropic", "claude-sonnet-5"), [], new Dictionary<string, string>());

    [Fact]
    public void Default_preset_rules_become_a_read_only_catalog_with_shell_prefixes()
    {
        var (tools, allowed, disallowed) = ClaudeCodeWorkerRuntime.Permissions(Preset.Load(Repo.Path("presets"), "default").NodeKinds["worker"].Rules);
        Assert.DoesNotContain("Edit", tools);
        Assert.DoesNotContain("Write", tools);
        Assert.DoesNotContain("WebFetch", tools);
        Assert.DoesNotContain("Agent", tools);
        Assert.DoesNotContain("AskUserQuestion", tools);
        Assert.Contains("Read", allowed);
        Assert.Contains("Bash(git log*)", allowed);
        Assert.DoesNotContain("Bash", allowed);
        Assert.Contains("Bash(rg *--pre*)", disallowed);
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
        Assert.True(Apply(s, """{"type":"result","subtype":"success","is_error":false,"result":"DONE"}"""));

        var (first, second, idle) = (s.Messages[0], s.Messages[1], s.Messages[2]);
        Assert.Equal(("msg_1", "Looking. ", 9L, 100L, 50L), (first.Id, first.Text, first.Tokens!.Output, first.Tokens.CacheRead, first.Tokens.CacheWrite));
        Assert.Contains("\"command\":\"ls\"", first.ToolOutput);
        Assert.Contains("a.txt", first.ToolOutput);
        Assert.NotNull(first.Completed);
        Assert.Equal("anthropic/claude-sonnet-5", second.Model);
        Assert.NotNull(second.Completed);
        Assert.Equal((WorkerMessageKind.Idle, IdleOutcome.Succeeded), (idle.Kind, idle.Outcome));
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

        IWorkerRuntime rt = await ClaudeCodeWorkerRuntime.ConnectAsync(claude, "2.1.195", "sk-test", CancellationToken.None);
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
        Assert.Equal("sk-test", File.ReadAllText(Path.Combine(_dir.Path, "key.txt")).Trim());
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
    public async Task A_version_other_than_the_pin_is_refused()
    {
        var claude = FakeClaude("");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ClaudeCodeWorkerRuntime.ConnectAsync(claude, "9.9.9", "k", CancellationToken.None));
    }

    private static bool Apply(ClaudeCodeWorkerRuntime.Session s, string json, bool compact = false) =>
        ClaudeCodeWorkerRuntime.Apply(s, JsonDocument.Parse(json).RootElement, compact);

    /// <summary>A stand-in CLI: prints the pinned version, records its arguments, stdin and key, and replays events.</summary>
    private string FakeClaude(string events)
    {
        _dir.Write("events.jsonl", events + "\n");
        var script = _dir.Write("claude", $"""
            #!/bin/sh
            if [ "$1" = "--version" ]; then echo "2.1.195 (Claude Code)"; exit 0; fi
            printf '%s\n' "$@" > "{_dir.Path}/args.txt"
            cat > "{_dir.Path}/stdin.txt"
            echo "$ANTHROPIC_API_KEY" > "{_dir.Path}/key.txt"
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
