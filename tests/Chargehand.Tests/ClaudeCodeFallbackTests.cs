using Chargehand.ClaudeCode;
using Chargehand.Contracts;
using Chargehand.Runtime;
using Chargehand.Runtimes.ClaudeCode.Api;
using Xunit;

namespace Chargehand.Tests;

/// <summary>Subscription first, then the API credit (issue #632): a turn the subscription's limit ends runs again on the fallback key within the day's cap, and the first turn after
/// the reset goes on the subscription again. The fake CLI answers the subscription with Claude Code 2.1.303's limit while a marker file exists.</summary>
public sealed class ClaudeCodeFallbackTests : IDisposable
{
    private const string Limit = """{"type":"result","subtype":"success","is_error":true,"api_error_status":429,"result":"You've hit your session limit · resets 4:41pm (Europe/Kiev)"}""";
    private const string Done = """{"type":"result","subtype":"success","is_error":false,"result":"DONE","total_cost_usd":0.5}""";

    private readonly TempDir _dir = new();
    private readonly Clock _clock = new();

    public void Dispose() => _dir.Dispose();

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task A_limited_turn_runs_again_on_the_fallback_key_at_its_own_endpoint_and_later_turns_stay_there()
    {
        var gate = new ApiFallback(6m, null, _clock);
        var (rt, session) = await StartAsync(gate);

        Assert.Equal(IdleOutcome.Succeeded, await TurnAsync(rt, session));
        Assert.Equal(IdleOutcome.Succeeded, await TurnAsync(rt, session));

        Assert.Equal(["oauth bare=no headers=x-bf-vk: vk-sub base=http://gateway.internal/sub", "api bare=yes headers= base=http://gateway.internal/api",
            "api bare=yes headers= base=http://gateway.internal/api"], Calls());
        Assert.Equal(1.0m, gate.Read().SpentUsd);
        Assert.All((await rt.ReadMessagesAsync(session, default)).Where(m => m.Kind == WorkerMessageKind.Idle), m => Assert.Equal(IdleOutcome.Succeeded, m.Outcome));
    }

    [Fact]
    public async Task The_first_turn_after_the_reset_goes_on_the_subscription_again()
    {
        var gate = new ApiFallback(6m, null, _clock);
        var (rt, session) = await StartAsync(gate);
        await TurnAsync(rt, session);
        File.Delete(Path.Combine(_dir.Path, "limited"));

        _clock.Now = gate.Read().LimitedUntil!.Value.AddSeconds(1);
        Assert.Equal(IdleOutcome.Succeeded, await TurnAsync(rt, session));

        Assert.Equal(["oauth", "api", "oauth"], Calls().Select(c => c.Split(' ')[0]));
    }

    [Fact]
    public async Task Past_the_daily_cap_the_limit_ends_the_turn_rate_limited_and_nothing_falls_back()
    {
        var gate = new ApiFallback(0.4m, null, _clock);
        var (rt, session) = await StartAsync(gate);
        await TurnAsync(rt, session);

        Assert.Equal(IdleOutcome.Failed, await TurnAsync(rt, session));

        Assert.Equal(["oauth", "api", "oauth"], Calls().Select(c => c.Split(' ')[0]));
        Assert.Contains("(API status 429)", (await rt.ReadMessagesAsync(session, default)).First(m => m.Kind == WorkerMessageKind.Idle).Error);
    }

    [Fact]
    public async Task Without_a_fallback_the_limit_ends_the_turn_as_before()
    {
        var (rt, session) = await StartAsync(null);

        Assert.Equal(IdleOutcome.Failed, await TurnAsync(rt, session));
        Assert.Equal(["oauth"], Calls().Select(c => c.Split(' ')[0]));
    }

    [Fact]
    public async Task A_judge_call_falls_back_too()
    {
        var gate = new ApiFallback(6m, null, _clock);
        var (rt, _) = await StartAsync(gate);

        Assert.Equal("DONE", await rt.GenerateAsync(null, "judge this", default));
        Assert.Equal(["oauth", "api"], Calls().Select(c => c.Split(' ')[0]));
    }

    [Fact]
    public void The_gate_closes_for_the_day_on_a_budget_refusal_and_keeps_its_ledger_across_a_restart()
    {
        var file = Path.Combine(_dir.Path, "state", "api-fallback.json");
        var gate = new ApiFallback(6m, file, _clock);
        Assert.True(gate.Limited(_clock.Now.AddDays(2)));
        gate.Spent(2m);
        Assert.True(gate.UseFallback());

        var restarted = new ApiFallback(6m, file, _clock);
        Assert.Equal(2m, restarted.Read().SpentUsd);
        restarted.Refused();
        Assert.False(restarted.UseFallback());

        _clock.Now = _clock.Now.AddDays(1);
        Assert.True(restarted.UseFallback());
        Assert.Equal(0m, restarted.Read().SpentUsd);
    }

    private async Task<(ClaudeCodeWorkerRuntime Runtime, string Session)> StartAsync(ApiFallback? gate)
    {
        File.WriteAllText(Path.Combine(_dir.Path, "limited"), "");
        var rt = await ClaudeCodeWorkerRuntime.ConnectAsync(FakeClaude(), "2.1.195", new ClaudeCodeCredential("oauth-sub", true), default,
            new Uri("http://gateway.internal/sub"), "x-bf-vk: vk-sub", gate is null ? null : new ClaudeCodeFallback("sk-api", new Uri("http://gateway.internal/api"), gate));
        var session = await rt.CreateAsync(new NodeSpec(_dir.Path, "build", null, [], new Dictionary<string, string>()), default);
        return (rt, session.Id);
    }

    private static async Task<IdleOutcome> TurnAsync(ClaudeCodeWorkerRuntime rt, string session)
    {
        await rt.SubmitAsync(session, "go", default);
        return await rt.AwaitIdleAsync(session, default);
    }

    private string[] Calls() => File.ReadAllLines(Path.Combine(_dir.Path, "calls.txt"));

    private string FakeClaude()
    {
        _dir.Write("limit.jsonl", Limit + "\n");
        _dir.Write("done.jsonl", Done + "\n");
        var script = _dir.Write("claude", $"""
            #!/bin/sh
            if [ "$1" = "--version" ]; then echo "2.1.195 (Claude Code)"; exit 0; fi
            bare=no; for a in "$@"; do [ "$a" = "--bare" ] && bare=yes; done
            cat > /dev/null
            if [ -n "$CLAUDE_CODE_OAUTH_TOKEN" ]; then
              echo "oauth bare=$bare headers=$ANTHROPIC_CUSTOM_HEADERS base=$ANTHROPIC_BASE_URL" >> "{_dir.Path}/calls.txt"
              if [ -f "{_dir.Path}/limited" ]; then cat "{_dir.Path}/limit.jsonl"; exit 1; fi
            else
              echo "api bare=$bare headers=$ANTHROPIC_CUSTOM_HEADERS base=$ANTHROPIC_BASE_URL" >> "{_dir.Path}/calls.txt"
            fi
            cat "{_dir.Path}/done.jsonl"
            """);
        ScriptFile.Seal(script);
        return script;
    }
}
