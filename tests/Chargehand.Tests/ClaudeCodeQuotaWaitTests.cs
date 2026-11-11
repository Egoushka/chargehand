using Chargehand.ClaudeCode;
using Chargehand.Contracts;
using Chargehand.Modules.Research.Api;
using Chargehand.Modules.Sessions.Api;
using Xunit;

namespace Chargehand.Tests;

/// <summary>ADR 0096: a worker turn that the subscription's limit ends, and that no fallback takes, waits for the reset and resumes the same session. The fake CLI answers with the
/// limit while a marker file exists; a test removes the marker during the wait.</summary>
public sealed class ClaudeCodeQuotaWaitTests : IDisposable
{
    private const string Done = """{"type":"result","subtype":"success","is_error":false,"result":"DONE","total_cost_usd":0.5}""";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private async Task<(ClaudeCodeWorkerRuntime Runtime, string Session)> StartAsync(string limit, Func<TimeSpan, CancellationToken, Task> sleep)
    {
        File.WriteAllText(Path.Combine(_dir.Path, "limited"), "");
        _dir.Write("limit.jsonl", limit + "\n");
        _dir.Write("done.jsonl", Done + "\n");
        var script = _dir.Write("claude", $"""
            #!/bin/sh
            if [ "$1" = "--version" ]; then echo "2.1.195 (Claude Code)"; exit 0; fi
            cat > /dev/null
            echo "$*" >> "{_dir.Path}/calls.txt"
            if [ -f "{_dir.Path}/limited" ]; then cat "{_dir.Path}/limit.jsonl"; exit 1; fi
            cat "{_dir.Path}/done.jsonl"
            """);
        ScriptFile.Seal(script);
        var rt = await ClaudeCodeWorkerRuntime.ConnectAsync(script, "2.1.195", new ClaudeCodeCredential("oauth-sub", true), default);
        rt.Sleep = sleep;
        var session = await rt.CreateAsync(new NodeSpec(_dir.Path, "build", null, [], new Dictionary<string, string>()), default);
        return (rt, session.Id);
    }

    private static string Limit(string text) => $$"""{"type":"result","subtype":"success","is_error":true,"api_error_status":429,"result":"{{text}}"}""";

    private string[] Calls() => File.ReadAllLines(Path.Combine(_dir.Path, "calls.txt"));

    private static async Task<IdleOutcome> TurnAsync(ClaudeCodeWorkerRuntime rt, string session)
    {
        await rt.SubmitAsync(session, "go", default);
        return await rt.AwaitIdleAsync(session, default);
    }

    [Fact]
    public async Task A_limited_turn_waits_for_the_reset_then_resumes_the_session_and_succeeds()
    {
        var slept = new List<TimeSpan>();
        var (rt, session) = await StartAsync(Limit("API Error: 429 rate limit, retry-after: 120"), (d, _) =>
        {
            slept.Add(d);
            File.Delete(Path.Combine(_dir.Path, "limited"));
            return Task.CompletedTask;
        });

        Assert.Equal(IdleOutcome.Succeeded, await TurnAsync(rt, session));

        Assert.Equal([TimeSpan.FromSeconds(120) + QuotaWait.Margin], slept);
        var calls = Calls();
        Assert.Equal(2, calls.Length);
        Assert.Contains("--session-id", calls[0]);
        Assert.Contains($"--resume {session}", calls[1]);
        Assert.Single(await rt.ReadMessagesAsync(session, default), m => m.Kind == WorkerMessageKind.Idle);
    }

    [Fact]
    public async Task A_budget_refusal_ends_the_turn_without_waiting()
    {
        var slept = new List<TimeSpan>();
        var (rt, session) = await StartAsync(Limit("API Error: 402 payment required"), (d, _) =>
        {
            slept.Add(d);
            return Task.CompletedTask;
        });

        Assert.Equal(IdleOutcome.Failed, await TurnAsync(rt, session));
        Assert.Empty(slept);
        Assert.Single(Calls());
    }

    [Fact]
    public async Task An_interrupt_ends_the_wait_and_the_turn()
    {
        var sleeping = new TaskCompletionSource();
        var (rt, session) = await StartAsync(Limit("API Error: 429 rate limit, retry-after: 120"), async (_, ct) =>
        {
            sleeping.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });

        await rt.SubmitAsync(session, "go", default);
        await sleeping.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await rt.InterruptAsync(session, default);

        Assert.Equal(IdleOutcome.Interrupted, await rt.AwaitIdleAsync(session, default));
        Assert.Single(Calls());
    }
}
