using System.Text.Json;
using Chargehand.ClaudeCode;
using Chargehand.Runtime;

namespace Chargehand.Tests;

/// <summary>Goal 0.6: granted services reach a Claude Code worker as a private --mcp-config file and explicit allowed tools.</summary>
public sealed class ClaudeCodeServicesTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _configs = new();

    public void Dispose()
    {
        _dir.Dispose();
        _configs.Dispose();
    }

    private static readonly ServiceGrant Docs = new("team-docs",
        new HttpServiceTransport(new Uri("https://mcp.example.internal/docs"), new Dictionary<string, string> { ["Authorization"] = "Bearer s3cret" }),
        ["search_docs", "read_doc"], new string('a', 64), ["write_note"]);

    private static readonly ServiceGrant Notes = new("team-notes",
        new StdioServiceTransport(["npx", "-y", "example-notes-mcp"], new Dictionary<string, string> { ["NOTES_TOKEN"] = "s3cret" }),
        ["search_notes"], new string('b', 64));

    private NodeSpec Spec(params ServiceGrant[] grants) =>
        new(_dir.Path, "build", new ModelRef("anthropic", "claude-sonnet-5"), [], new Dictionary<string, string>(), grants);

    [Fact]
    public void The_config_lists_each_server_in_claude_codes_format()
    {
        var (json, allowed, disallowed) = ClaudeCodeWorkerRuntime.ServiceConfig([Docs, Notes]);

        using var doc = JsonDocument.Parse(json);
        var servers = doc.RootElement.GetProperty("mcpServers");
        var docs = servers.GetProperty("team-docs");
        Assert.Equal(("http", "https://mcp.example.internal/docs", "Bearer s3cret"),
            (docs.GetProperty("type").GetString(), docs.GetProperty("url").GetString(), docs.GetProperty("headers").GetProperty("Authorization").GetString()));
        var notes = servers.GetProperty("team-notes");
        Assert.Equal("npx", notes.GetProperty("command").GetString());
        Assert.Equal(["-y", "example-notes-mcp"], notes.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("s3cret", notes.GetProperty("env").GetProperty("NOTES_TOKEN").GetString());
        Assert.Equal(["mcp__team-docs__search_docs", "mcp__team-docs__read_doc", "mcp__team-notes__search_notes"], allowed);
        Assert.Equal(["mcp__team-docs__write_note"], disallowed);
    }

    [Fact]
    public void A_legacy_sse_server_is_typed_sse_and_a_server_without_headers_or_env_writes_neither()
    {
        var sse = new ServiceGrant("chronicle", new HttpServiceTransport(new Uri("http://localhost:8031/sse"), new Dictionary<string, string>(), HttpServiceProtocol.Sse), ["recall"], new string('c', 64));
        var bare = new ServiceGrant("plain", new StdioServiceTransport(["plain-mcp"], new Dictionary<string, string>()), ["ping"], new string('d', 64));

        using var doc = JsonDocument.Parse(ClaudeCodeWorkerRuntime.ServiceConfig([sse, bare]).Json);

        var servers = doc.RootElement.GetProperty("mcpServers");
        Assert.Equal("sse", servers.GetProperty("chronicle").GetProperty("type").GetString());
        Assert.False(servers.GetProperty("chronicle").TryGetProperty("headers", out _));
        Assert.Equal("plain-mcp", servers.GetProperty("plain").GetProperty("command").GetString());
        Assert.Empty(servers.GetProperty("plain").GetProperty("args").EnumerateArray());
        Assert.False(servers.GetProperty("plain").TryGetProperty("env", out _));
    }

    /// <summary>Claude Code names a tool <c>mcp__server__tool</c> with every character outside letters, digits, "_" and "-" replaced by
    /// "_" (2.1.283: a server tool <c>echo.fact</c> is listed as <c>mcp__team-docs__echo_fact</c>), so the allow and deny lists must too.</summary>
    [Fact]
    public void A_tool_name_is_spelled_the_way_the_cli_lists_it()
    {
        var dotted = new ServiceGrant("team-docs", Docs.Transport, ["search.docs", "read-doc"], new string('a', 64), ["write.note"]);

        var (_, allowed, disallowed) = ClaudeCodeWorkerRuntime.ServiceConfig([dotted]);

        Assert.Equal(["mcp__team-docs__search_docs", "mcp__team-docs__read-doc"], allowed);
        Assert.Equal(["mcp__team-docs__write_note"], disallowed);
    }

    [Fact]
    public async Task A_session_with_grants_passes_a_private_config_file_and_only_the_granted_tools()
    {
        var rt = await ConnectAsync(FakeClaude());
        var session = await rt.CreateAsync(Spec(Docs), CancellationToken.None);

        await rt.SubmitAsync(session.Id, "hi", CancellationToken.None);
        Assert.Equal(IdleOutcome.Succeeded, await rt.AwaitIdleAsync(session.Id, CancellationToken.None));

        var args = File.ReadAllLines(Path.Combine(_dir.Path, "args.txt"));
        var config = args[Array.IndexOf(args, "--mcp-config") + 1];
        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("mcp__team-docs__search_docs", args[(Array.IndexOf(args, "--allowedTools") + 1)..]);
        Assert.Contains("mcp__team-docs__read_doc", args[(Array.IndexOf(args, "--allowedTools") + 1)..]);
        Assert.DoesNotContain("mcp__team-docs__write_note", args[..Array.IndexOf(args, "--disallowedTools")]);
        Assert.Equal("mcp__team-docs__write_note", args[Array.IndexOf(args, "--disallowedTools") + 1]);   // the server's other tool is not shipped to the model
        Assert.DoesNotContain("s3cret", string.Join(' ', args), StringComparison.Ordinal);              // no credential on the command line
        Assert.StartsWith(_configs.Path, config, StringComparison.Ordinal);                               // not under the checkout
        Assert.False(config.StartsWith(_dir.Path, StringComparison.Ordinal));
        Assert.Equal("-rw-------", File.ReadAllText(Path.Combine(_dir.Path, "mcp.mode")).Trim());       // the file the CLI read was private ...
        Assert.Equal("drwx------", File.ReadAllText(Path.Combine(_dir.Path, "mcp.dirmode")).Trim());    // ... in a private directory
        Assert.Contains("Bearer s3cret", File.ReadAllText(Path.Combine(_dir.Path, "mcp.json")), StringComparison.Ordinal);
        Assert.False(File.Exists(config));                                                                 // gone once the turn ended
        Assert.Empty(Directory.EnumerateFileSystemEntries(_configs.Path));
    }

    [Fact]
    public async Task Every_turn_writes_its_own_file_and_removes_it()
    {
        var rt = await ConnectAsync(FakeClaude());
        var session = await rt.CreateAsync(Spec(Docs), CancellationToken.None);

        await rt.SubmitAsync(session.Id, "one", CancellationToken.None);
        await rt.AwaitIdleAsync(session.Id, CancellationToken.None);
        var first = File.ReadAllLines(Path.Combine(_dir.Path, "args.txt"));
        await rt.SubmitAsync(session.Id, "two", CancellationToken.None);
        await rt.AwaitIdleAsync(session.Id, CancellationToken.None);
        var second = File.ReadAllLines(Path.Combine(_dir.Path, "args.txt"));

        Assert.Contains("--resume", second);
        Assert.NotEqual(first[Array.IndexOf(first, "--mcp-config") + 1], second[Array.IndexOf(second, "--mcp-config") + 1]);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_configs.Path));
    }

    [Fact]
    public async Task A_session_without_grants_passes_no_mcp_config()
    {
        var rt = await ConnectAsync(FakeClaude());
        var session = await rt.CreateAsync(Spec(), CancellationToken.None);

        await rt.SubmitAsync(session.Id, "hi", CancellationToken.None);
        await rt.AwaitIdleAsync(session.Id, CancellationToken.None);

        var args = File.ReadAllLines(Path.Combine(_dir.Path, "args.txt"));
        Assert.DoesNotContain("--mcp-config", args);
        Assert.Contains("--strict-mcp-config", args);
        Assert.DoesNotContain(args, a => a.StartsWith("mcp__", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_configs.Path));
    }

    [Fact]
    public async Task A_fork_keeps_the_grants_of_its_source()
    {
        var rt = await ConnectAsync(FakeClaude());
        var source = await rt.CreateAsync(Spec(Docs), CancellationToken.None);
        await rt.SubmitAsync(source.Id, "hi", CancellationToken.None);
        await rt.AwaitIdleAsync(source.Id, CancellationToken.None);
        var firstUser = (await rt.ReadMessagesAsync(source.Id, CancellationToken.None)).Last(m => m.Kind == WorkerMessageKind.User);
        File.Delete(Path.Combine(_dir.Path, "args.txt"));

        var fork = await rt.ForkAsync(source.Id, firstUser.Id, CancellationToken.None);
        await rt.SubmitAsync(fork.Id, "again", CancellationToken.None);
        await rt.AwaitIdleAsync(fork.Id, CancellationToken.None);

        Assert.Contains("--mcp-config", File.ReadAllLines(Path.Combine(_dir.Path, "args.txt")));
    }

    [Fact]
    public async Task The_file_is_removed_when_the_cli_fails()
    {
        var rt = await ConnectAsync(FakeClaude(after: "echo 'server unreachable' >&2; exit 3"));
        var session = await rt.CreateAsync(Spec(Docs), CancellationToken.None);

        await rt.SubmitAsync(session.Id, "hi", CancellationToken.None);

        Assert.Equal(IdleOutcome.Failed, await rt.AwaitIdleAsync(session.Id, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(_dir.Path, "mcp.json")));                                   // the CLI did get the file
        Assert.Empty(Directory.EnumerateFileSystemEntries(_configs.Path));
    }

    [Fact]
    public async Task The_file_is_removed_when_the_turn_is_interrupted()
    {
        var rt = await ConnectAsync(FakeClaude(after: "sleep 30"));
        var session = await rt.CreateAsync(Spec(Docs), CancellationToken.None);
        await rt.SubmitAsync(session.Id, "hi", CancellationToken.None);
        await Eventually(() => File.Exists(Path.Combine(_dir.Path, "stdin.txt")));
        Assert.Single(Directory.EnumerateDirectories(_configs.Path));                                    // present while the CLI runs

        await rt.InterruptAsync(session.Id, CancellationToken.None);

        Assert.Equal(IdleOutcome.Interrupted, await rt.AwaitIdleAsync(session.Id, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_configs.Path));
    }

    [Fact]
    public async Task The_file_is_removed_when_the_cli_cannot_be_started()
    {
        var claude = FakeClaude();
        var rt = await ConnectAsync(claude);
        var session = await rt.CreateAsync(Spec(Docs), CancellationToken.None);
        File.Delete(claude);

        await Assert.ThrowsAnyAsync<Exception>(() => rt.SubmitAsync(session.Id, "hi", CancellationToken.None));

        Assert.Empty(Directory.EnumerateFileSystemEntries(_configs.Path));
    }

    [Fact]
    public void Directories_left_by_a_crash_are_swept_once_they_are_a_day_old()
    {
        var stale = Directory.CreateDirectory(Path.Combine(_configs.Path, "chargehand-mcp-old"));
        File.WriteAllText(Path.Combine(stale.FullName, "mcp.json"), "{}");
        stale.LastWriteTimeUtc = DateTime.UtcNow.AddDays(-2);
        var recent = Directory.CreateDirectory(Path.Combine(_configs.Path, "chargehand-mcp-new"));
        var other = Directory.CreateDirectory(Path.Combine(_configs.Path, "someone-elses"));
        other.LastWriteTimeUtc = DateTime.UtcNow.AddDays(-2);

        ClaudeCodeWorkerRuntime.SweepStaleConfigs(_configs.Path, TimeSpan.FromDays(1));

        Assert.False(Directory.Exists(stale.FullName));
        Assert.True(Directory.Exists(recent.FullName));
        Assert.True(Directory.Exists(other.FullName));
    }

    [Fact]
    public async Task A_granted_server_that_did_not_connect_is_named_with_the_status_the_cli_reported()
    {
        var rt = await ConnectAsync(FakeClaude("""
            {"type":"system","subtype":"init","mcp_servers":[{"name":"team-docs","status":"failed","source":"dynamic"},{"name":"team-notes","status":"connected"}]}
            {"type":"result","subtype":"success","is_error":false,"result":"hi"}
            """));
        var session = await rt.CreateAsync(Spec(Docs, Notes), CancellationToken.None);

        await rt.SubmitAsync(session.Id, "hi", CancellationToken.None);
        Assert.Equal(IdleOutcome.Succeeded, await rt.AwaitIdleAsync(session.Id, CancellationToken.None));

        Assert.Equal(new Dictionary<string, string> { ["team-docs"] = "failed" }, ((IServiceHealth)rt).UnavailableServices(session.Id));
    }

    [Fact]
    public async Task A_granted_server_missing_from_the_init_event_is_unavailable_too()
    {
        var rt = await ConnectAsync(FakeClaude("""
            {"type":"system","subtype":"init","mcp_servers":[]}
            {"type":"result","subtype":"success","is_error":false,"result":"hi"}
            """));
        var session = await rt.CreateAsync(Spec(Docs), CancellationToken.None);

        await rt.SubmitAsync(session.Id, "hi", CancellationToken.None);
        await rt.AwaitIdleAsync(session.Id, CancellationToken.None);

        Assert.Equal(new Dictionary<string, string> { ["team-docs"] = "absent" }, ((IServiceHealth)rt).UnavailableServices(session.Id));
    }

    [Fact]
    public async Task Connected_servers_and_a_session_without_grants_report_nothing()
    {
        var rt = await ConnectAsync(FakeClaude("""
            {"type":"system","subtype":"init","mcp_servers":[{"name":"team-docs","status":"connected"}]}
            {"type":"result","subtype":"success","is_error":false,"result":"hi"}
            """));
        var granted = await rt.CreateAsync(Spec(Docs), CancellationToken.None);
        var plain = await rt.CreateAsync(Spec(), CancellationToken.None);

        foreach (var id in new[] { granted.Id, plain.Id })
        {
            await rt.SubmitAsync(id, "hi", CancellationToken.None);
            await rt.AwaitIdleAsync(id, CancellationToken.None);
            Assert.Empty(((IServiceHealth)rt).UnavailableServices(id));
        }
    }

    [Fact]
    public async Task A_credential_the_cli_prints_is_kept_out_of_the_failure_text()
    {
        var rt = await ConnectAsync(FakeClaude(after: "echo 'connect team-notes: rejected NOTES-value-91' >&2; exit 2"));
        var withEnv = new ServiceGrant("team-notes", new StdioServiceTransport(["npx"], new Dictionary<string, string> { ["NOTES_TOKEN"] = "NOTES-value-91" }), ["search_notes"], new string('b', 64));
        var session = await rt.CreateAsync(Spec(Docs, withEnv), CancellationToken.None);

        await rt.SubmitAsync(session.Id, "hi", CancellationToken.None);
        await rt.AwaitIdleAsync(session.Id, CancellationToken.None);

        var error = (await rt.ReadMessagesAsync(session.Id, CancellationToken.None)).First(m => m.Kind == WorkerMessageKind.Idle).Error!;
        Assert.StartsWith("claude exited 2: connect team-notes: rejected", error, StringComparison.Ordinal);
        Assert.DoesNotContain("NOTES-value-91", error, StringComparison.Ordinal);
    }

    private async Task<ClaudeCodeWorkerRuntime> ConnectAsync(string claude)
    {
        var rt = await ClaudeCodeWorkerRuntime.ConnectAsync(claude, "2.1.195", new ClaudeCodeCredential("k", false), CancellationToken.None);
        rt.ConfigRoot = _configs.Path;
        return rt;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition(), "the stand-in CLI did not get as far as reading its config");
    }

    /// <summary>A stand-in CLI that records argv and copies the --mcp-config file (and its mode, and its directory's) while it still exists.
    /// <paramref name="after"/> runs once the prompt is read, before the events are replayed.</summary>
    private string FakeClaude(string? events = null, string after = "")
    {
        _dir.Write("events.jsonl", (events ?? """{"type":"result","subtype":"success","is_error":false,"result":"hi"}""") + "\n");
        var script = _dir.Write("claude", $"""
            #!/bin/sh
            if [ "$1" = "--version" ]; then echo "2.1.195 (Claude Code)"; exit 0; fi
            printf '%s\n' "$@" > "{_dir.Path}/args.txt"
            prev=""
            for a in "$@"; do
              if [ "$prev" = "--mcp-config" ]; then
                cp "$a" "{_dir.Path}/mcp.json"
                ls -ld "$(dirname "$a")" | cut -c1-10 > "{_dir.Path}/mcp.dirmode"
                ls -l "$a" | cut -c1-10 > "{_dir.Path}/mcp.mode"
              fi
              prev="$a"
            done
            cat > "{_dir.Path}/stdin.txt"
            {after}
            cat "{_dir.Path}/events.jsonl"
            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }
}
