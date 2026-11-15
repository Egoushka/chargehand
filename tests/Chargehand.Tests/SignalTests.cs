using Chargehand.Contracts;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Tests;

public class SignalTests
{
    private sealed class Judge(Func<Signal, SignalVerdict> answer) : ISignalJudge
    {
        public int Calls { get; private set; }

        public Task<SignalVerdict> JudgeAsync(Signal signal, string digest, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(answer(signal));
        }
    }

    private static readonly Signal Loop = new("loop", "session stuck in a loop");
    private static readonly Signal Tests = new("tests", "edits tests to pass");

    private static SignalChecker Checker(Judge judge, List<SignalHit> posted, ManualTimeProvider time, int cap = 100, params Signal[] signals) =>
        new(signals.Length == 0 ? [Loop, Tests] : signals, judge, cap, (hit, _) => { posted.Add(hit); return Task.CompletedTask; }, time);

    [Fact]
    public async Task A_signal_that_fires_is_posted_once_and_one_that_does_not_is_not()
    {
        var posted = new List<SignalHit>();
        var checker = Checker(new Judge(s => new(s.Id == "loop", "same edit 6 times")), posted, new ManualTimeProvider());
        var hits = await checker.CheckAsync("run-1", "digest", CancellationToken.None);

        var hit = Assert.Single(hits);
        Assert.Equal(("loop", "run-1", "same edit 6 times"), (hit.SignalId, hit.RunId, hit.Reason));
        Assert.Equal(hits, posted);
    }

    [Fact]
    public async Task The_daily_cap_bounds_judge_calls_and_resets_the_next_day()
    {
        var time = new ManualTimeProvider();
        var judge = new Judge(_ => new(false));
        var checker = Checker(judge, [], time, cap: 3);
        await checker.CheckAsync("a", "d", CancellationToken.None);
        await checker.CheckAsync("b", "d", CancellationToken.None);

        Assert.Equal(3, judge.Calls);
        Assert.Equal(1, checker.Skipped);

        time.Advance(TimeSpan.FromDays(1));
        await checker.CheckAsync("c", "d", CancellationToken.None);
        Assert.Equal(5, judge.Calls);
    }

    [Fact]
    public async Task The_same_signal_on_the_same_run_fires_once()
    {
        var posted = new List<SignalHit>();
        var checker = Checker(new Judge(_ => new(true, "x")), posted, new ManualTimeProvider(), signals: Loop);
        await checker.CheckAsync("run-1", "d", CancellationToken.None);
        await checker.CheckAsync("run-1", "d", CancellationToken.None);

        Assert.Single(posted);
    }

    [Fact]
    public async Task A_failing_judge_or_post_does_not_stop_the_other_signals()
    {
        var judge = new Judge(s => s.Id == "loop" ? throw new SignalException("bad answer") : new(true, "ok"));
        var checker = new SignalChecker([Loop, Tests], judge, 10, (_, _) => throw new InvalidOperationException("inbox down"), new ManualTimeProvider());
        var hits = await checker.CheckAsync("run-1", "d", CancellationToken.None);

        Assert.Equal("tests", Assert.Single(hits).SignalId);
    }

    private static ModelSignalJudge Model(ResultStatus status, string summary, List<RunRequest> seen) =>
        new((request, _, _, _) =>
        {
            seen.Add(request);
            return Task.FromResult(new ResultContract("result/v1", "run-signal", "answer", "trace", new PromptChain([], new AsSent("2.0.16", "build", "m", "2026-10-13")), status, summary, [], [], [], [], 0.9, new Usage(0, 0, 0, 0, 0),
                status == ResultStatus.Failed ? new ResultError(ErrorCode.CheckoutInvalid, "failed", false) : null));
        }, "cheap");

    [Theory]
    [InlineData("yes the same test edited 5 times", true, "the same test edited 5 times")]
    [InlineData("YES", true, null)]
    [InlineData("no", false, null)]
    [InlineData("No, nothing odd", false, null)]
    public async Task The_model_judge_reads_yes_and_no(string answer, bool fires, string? reason)
    {
        var seen = new List<RunRequest>();
        var verdict = await Model(ResultStatus.Completed, answer, seen).JudgeAsync(Loop, "edit a.cs; edit a.cs", CancellationToken.None);

        Assert.Equal((fires, reason), (verdict.Fires, verdict.Reason));
        var prompt = Assert.Single(seen).Text;
        Assert.Contains("session stuck in a loop", prompt, StringComparison.Ordinal);
        Assert.Contains("edit a.cs; edit a.cs", prompt, StringComparison.Ordinal);
        Assert.Contains("data", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ResultStatus.Failed, "yes")]
    [InlineData(ResultStatus.Completed, "  ")]
    public async Task A_failed_run_or_an_empty_answer_is_a_signal_error(ResultStatus status, string summary) =>
        await Assert.ThrowsAsync<SignalException>(() => Model(status, summary, []).JudgeAsync(Loop, "d", CancellationToken.None));

    [Fact]
    public void Precision_is_confirmed_over_labelled_and_passes_at_seventy_percent()
    {
        Assert.Null(SignalPrecision.Of([]));
        Assert.Equal(0.7, SignalPrecision.Of([.. Enumerable.Repeat(true, 21), .. Enumerable.Repeat(false, 9)]));
        Assert.True(SignalPrecision.Passes([.. Enumerable.Repeat(true, 21), .. Enumerable.Repeat(false, 9)]));
        Assert.False(SignalPrecision.Passes([.. Enumerable.Repeat(true, 20), .. Enumerable.Repeat(false, 10)]));
        Assert.False(SignalPrecision.Passes([]));
    }
}
