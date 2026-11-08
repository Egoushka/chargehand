using System.Text.Json;
using Chargehand.Modules.Sessions.Api;
using static Chargehand.Modules.Sessions.Tests.SessionDriverTests;

namespace Chargehand.Modules.Sessions.Tests;

/// <summary>ADR 0096: a driven session that meets the subscription's limit waits for the reset and resumes the same session. The stand-in CLI is a script that limits its first
/// run (a marker file) and records every call's arguments.</summary>
public class SessionDriverQuotaTests
{
    private const string SessionInit = """{"type":"system","subtype":"init","session_id":"cc-1"}""";

    private static string Limited(string text = "API Error: 429 rate limit, retry-after: 60") =>
        JsonSerializer.Serialize(new { type = "result", is_error = true, api_error_status = 429, result = text });

    /// <summary>The first call prints <paramref name="first"/>, every later one <paramref name="later"/>; each records its arguments in calls.txt.</summary>
    private static string Script(Workspace w, string[] first, string[] later) =>
        $"""
        echo "$*" >> "{w.Dir.Path}/calls.txt"
        if [ ! -f "{w.Dir.Path}/seen" ]; then
          touch "{w.Dir.Path}/seen"
        {Emit(first)}
          exit 0
        fi
        {Emit(later)}
        """;

    private static (SessionOptions Options, List<TimeSpan> Slept) Waiting(Workspace w, string script, SessionTask? task = null)
    {
        var slept = new List<TimeSpan>();
        var options = Options(w, script, task ?? SessionDriverTests.Task() with { QuotaWaitMinutes = null }) with
        {
            Delay = (d, _) =>
            {
                slept.Add(d);
                return System.Threading.Tasks.Task.CompletedTask;
            },
        };
        return (options, slept);
    }

    private static string[] Calls(Workspace w) => File.ReadAllLines(Path.Combine(w.Dir.Path, "calls.txt"));

    [Fact]
    public async Task A_session_that_hits_the_limit_waits_then_resumes_the_same_session_and_completes()
    {
        using var w = new Workspace();
        var (options, slept) = Waiting(w, Script(w, [SessionInit, Assistant("m1", "working"), Limited()], [SessionInit, Assistant("m2", "done\n" + Reported), Result()]));

        var outcome = await SessionDriver.RunAsync(options, default);

        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Equal([TimeSpan.FromSeconds(60) + QuotaWait.Margin], slept);
        var calls = Calls(w);
        Assert.Equal(2, calls.Length);
        Assert.DoesNotContain("--resume", calls[0]);
        Assert.Contains("--resume cc-1", calls[1]);
        Assert.Contains(QuotaWait.ResumePrompt, calls[1]);
        Assert.Contains("quota_wait", File.ReadAllText(Path.Combine(w.Out, "stream.jsonl")));
    }

    [Fact]
    public async Task A_limit_before_the_first_message_runs_the_start_command_again()
    {
        using var w = new Workspace();
        var (options, slept) = Waiting(w, Script(w, [Limited()], [SessionInit, Assistant("m1", "done\n" + Reported), Result()]));

        var outcome = await SessionDriver.RunAsync(options, default);

        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Single(slept);
        Assert.All(Calls(w), c => Assert.DoesNotContain("--resume", c));
        Assert.Equal(2, Calls(w).Length);
    }

    [Fact]
    public async Task A_budget_refusal_ends_the_session_rate_limited_without_waiting()
    {
        using var w = new Workspace();
        var (options, slept) = Waiting(w, Script(w, [SessionInit, Assistant("m1", "hi"), Limited("API Error: 402 payment required")], [Result()]));

        var outcome = await SessionDriver.RunAsync(options, default);

        Assert.Equal((SessionStatus.RateLimited, GatewayRefusal.BudgetReason), (outcome.Status, outcome.Reason));
        Assert.Empty(slept);
        Assert.Single(Calls(w));
    }

    [Fact]
    public async Task A_reset_past_the_wall_clock_ends_the_session_rate_limited_as_before()
    {
        using var w = new Workspace();
        var reset = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds();
        var (options, slept) = Waiting(w, Script(w, [SessionInit, Assistant("m1", "hi"), Limited($"usage limit reached|{reset}")], [Result()]));

        var outcome = await SessionDriver.RunAsync(options, default);

        Assert.Equal((SessionStatus.RateLimited, "rate_limited"), (outcome.Status, outcome.Reason));
        Assert.Empty(slept);
        Assert.Single(Calls(w));
    }

    [Fact]
    public async Task A_session_still_limited_after_its_waits_ends_rate_limited()
    {
        using var w = new Workspace();
        var always = $"""
            echo "$*" >> "{w.Dir.Path}/calls.txt"
            {Emit(SessionInit, Assistant("m1", "hi"), Limited())}
            """;
        var (options, slept) = Waiting(w, always);

        var outcome = await SessionDriver.RunAsync(options, default);

        Assert.Equal(SessionStatus.RateLimited, outcome.Status);
        Assert.Equal(QuotaWait.MostWaits, slept.Count);
        Assert.Equal(QuotaWait.MostWaits + 1, Calls(w).Length);
    }

    [Fact]
    public async Task A_task_that_turns_the_wait_off_ends_rate_limited_at_once()
    {
        using var w = new Workspace();
        var (options, slept) = Waiting(w, Script(w, [SessionInit, Assistant("m1", "hi"), Limited()], [Result()]), SessionDriverTests.Task() with { QuotaWaitMinutes = 0 });

        var outcome = await SessionDriver.RunAsync(options, default);

        Assert.Equal(SessionStatus.RateLimited, outcome.Status);
        Assert.Empty(slept);
    }
}
