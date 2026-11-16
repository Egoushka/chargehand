using Chargehand.Modules.Events.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Server;

namespace Chargehand.Tests;

public sealed class SignalWatcherTests : IDisposable
{
    private sealed class Judge(Func<Signal, string, SignalVerdict> answer) : ISignalJudge
    {
        public int Calls { get; private set; }

        public string? Digest { get; private set; }

        public Task<SignalVerdict> JudgeAsync(Signal signal, string digest, CancellationToken ct)
        {
            Calls++;
            Digest = digest;
            return Task.FromResult(answer(signal, digest));
        }
    }

    private readonly string _root = Directory.CreateTempSubdirectory("signals").FullName;
    private readonly SqliteEventLog _log;

    public SignalWatcherTests() => _log = SqliteEventLog.Open(Path.Combine(_root, "events.db"));

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_root, true);
    }

    private SignalHitStore Store => new(Path.Combine(_root, "hits.jsonl"));

    private SignalWatcher Watcher(Judge judge, int cap = 50) =>
        new(_log, new SignalChecker([new("loop", "session stuck in a loop")], judge, cap, (_, _) => Task.CompletedTask), Store);

    private async Task<StoredEvent> Emit(string id, string type, string thread, string data, string state = "open")
    {
        var body = type == ThreadStep.Type ? $$"""{"goal":"g","done":"did {{id}}","next":"n","state":"{{state}}"}""" : data;
        var envelope = $$"""{"specversion":"1.0","id":"{{id}}","source":"claude/s","type":"{{type}}","subject":"{{thread}}","time":"2026-11-15T10:00:00.000Z","scope":"global","data":{{body}}}""";
        var seqs = await _log.AppendAsync([new NewEvent("claude/s", id, type, thread, "global", null, "2026-11-15T10:00:00.000Z", envelope)], default);
        return (await _log.ReadAsync(new EventFilter(After: seqs[0] - 1, Limit: 1), default))[0];
    }

    [Fact]
    public async Task A_closing_step_is_checked_with_the_threads_events_and_the_hit_is_kept()
    {
        var judge = new Judge((_, _) => new(true, "same edit"));
        await Emit("a", "tool.used", "t1", """{"tool":"edit"}""");
        var open = await Emit("b", ThreadStep.Type, "t1", "");
        Assert.Empty(await Watcher(judge).HandleAsync(open, default));
        Assert.Equal(0, judge.Calls);

        var closed = await Emit("c", ThreadStep.Type, "t1", "", "closed");
        var hit = Assert.Single(await Watcher(judge).HandleAsync(closed, default));

        Assert.Equal(("loop", "t1", "same edit"), (hit.SignalId, hit.RunId, hit.Reason));
        Assert.Contains("tool.used", judge.Digest, StringComparison.Ordinal);
        Assert.Equal(hit.SignalId, Assert.Single(Store.ReadAll()).SignalId);
    }

    [Fact]
    public async Task Another_event_type_checks_nothing()
    {
        var judge = new Judge((_, _) => new(true));
        Assert.Empty(await Watcher(judge).HandleAsync(await Emit("a", "tool.used", "t1", "{}"), default));
        Assert.Equal(0, judge.Calls);
    }

    [Fact]
    public void The_digest_is_a_line_per_event_with_long_data_cut()
    {
        var text = SignalWatcher.Digest([new StoredEvent(1, "s", "a", "x.y", null, "g", null, "t", """{"data":{"v":"%s"}}""".Replace("%s", new string('z', 500)))]);
        Assert.StartsWith("t x.y ", text, StringComparison.Ordinal);
        Assert.True(text.Length < 330);
        Assert.EndsWith("…\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_the_cap_used_up_a_closing_thread_is_not_checked()
    {
        var judge = new Judge((_, _) => new(false));
        var watcher = Watcher(judge, cap: 1);
        await watcher.HandleAsync(await Emit("a", ThreadStep.Type, "t1", "", "closed"), default);
        await watcher.HandleAsync(await Emit("b", ThreadStep.Type, "t2", "", "closed"), default);

        Assert.Equal(1, judge.Calls);
    }

    [Fact]
    public async Task Run_handles_a_thread_that_closes_after_it_started_and_not_one_that_closed_before()
    {
        var judge = new Judge((_, _) => new(true, "x"));
        await Emit("old", ThreadStep.Type, "before", "", "closed");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = Watcher(judge).RunAsync(stop.Token);
        await Emit("new", ThreadStep.Type, "after", "", "closed");
        while (Store.ReadAll().Count == 0)
            await Task.Delay(20, stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(["after"], Store.ReadAll().Select(h => h.RunId));
    }

    [Fact]
    public void The_hit_store_round_trips_and_skips_a_line_it_cannot_read()
    {
        var store = Store;
        var at = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
        store.Append(new SignalHit("loop", "t1", at, "same edit"));
        File.AppendAllText(Path.Combine(_root, "hits.jsonl"), "not json\n");
        store.Append(new SignalHit("tests", "t2", at, ""));

        Assert.Equal([("loop", "t1", at, "same edit"), ("tests", "t2", at, "")], store.ReadAll().Select(h => (h.SignalId, h.RunId, h.At, h.Reason)));
    }

    [Fact]
    public void The_settings_default_to_off_and_name_their_problems()
    {
        var defaults = new SignalsSettings();
        Assert.Equal((false, 50, "cheap"), (defaults.Enabled, defaults.MaxChecksPerDay, defaults.Preset));
        Assert.Empty(defaults.Problems());

        var bad = new SignalsSettings(MaxChecksPerDay: 0, Items: [new("a", "x"), new("a", "y"), new("b", " ")]);
        Assert.Equal(3, bad.Problems().Count);
        Assert.Contains(bad.Problems(), p => p.Contains("used twice", StringComparison.Ordinal));
    }
}
