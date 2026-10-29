using Chargehand.Runtimes.ClaudeCode.Api;
using Xunit;

namespace Chargehand.Tests;

/// <summary>ADR 0088, decision 5 and 6: a driven batch spends the fallback inside its egress container, so the server reserves the batch's share of today's cap at its start and settles
/// what the egress log says it spent at its end.</summary>
public sealed class ApiFallbackTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly Clock _clock = new();

    public void Dispose() => _dir.Dispose();

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Two_batches_reserve_parts_of_the_remainder_and_settle_what_they_spent()
    {
        var gate = new ApiFallback(10m, null, _clock);
        gate.Spent(2m);

        // 8 left, no holder: the first batch takes it all. The second finds nothing unreserved, so it gets no fallback rather than a share that would pass the cap together with the first.
        Assert.Equal(8m, gate.Reserve("batch-a"));
        Assert.Equal(0m, gate.Reserve("batch-b"));
        Assert.Equal(["batch-a"], gate.Read().Reserved!.Keys);

        gate.Settle("batch-a", 3m, refused: false, resetsAt: null);
        Assert.Equal(5m, gate.Read().SpentUsd);
        // The second batch starts later: 5 left and no other holder, so its share is the whole 5.
        Assert.Equal(5m, gate.Reserve("batch-b"));
        gate.Settle("batch-b", 1.5m, refused: false, resetsAt: null);

        var ledger = gate.Read();
        Assert.Equal(6.5m, ledger.SpentUsd);                                   // 2 + 3 + 1.5, the logged dollars
        Assert.True(ledger.Reserved is null or { Count: 0 });                  // nothing stays reserved
    }

    [Fact]
    public void An_unreadable_egress_log_counts_the_whole_reservation()
    {
        var gate = new ApiFallback(10m, null, _clock);
        Assert.Equal(10m, gate.Reserve("batch-a"));

        gate.SettleUnreadable("batch-a");

        var ledger = gate.Read();
        Assert.Equal(10m, ledger.SpentUsd);                                    // the cap errs low, never high
        Assert.True(ledger.Reserved is null or { Count: 0 });
        Assert.False(gate.UseFallback());
        Assert.Equal(0m, gate.Reserve("batch-b"));
    }

    [Fact]
    public void A_batch_that_resumes_keeps_its_reservation_across_a_restart()
    {
        var file = Path.Combine(_dir.Path, "state", "api-fallback.json");
        Assert.Equal(10m, new ApiFallback(10m, file, _clock).Reserve("batch-a"));

        var restarted = new ApiFallback(10m, file, _clock);

        Assert.Equal(10m, restarted.Reserve("batch-a"));                       // the same batch asks again and gets what it holds, not a share of nothing
        Assert.Equal(10m, restarted.Read().Reserved!["batch-a"]);
        restarted.Settle("batch-a", 4m, refused: false, resetsAt: null);
        Assert.Equal(4m, new ApiFallback(10m, file, _clock).Read().SpentUsd);
    }

    [Fact]
    public void A_closed_or_spent_fallback_reserves_nothing()
    {
        var spent = new ApiFallback(5m, null, _clock);
        spent.Spent(5m);
        Assert.Equal(0m, spent.Reserve("batch-a"));
        Assert.Null(spent.Read().Reserved);

        var refused = new ApiFallback(5m, null, _clock);
        refused.Refused();
        Assert.Equal(0m, refused.Reserve("batch-a"));
    }

    [Fact]
    public void Settling_records_the_gateways_refusal_and_the_subscriptions_reset()
    {
        var gate = new ApiFallback(10m, null, _clock);
        gate.Reserve("batch-a");
        var reset = _clock.Now.AddHours(3);

        gate.Settle("batch-a", 1m, refused: true, resetsAt: reset);

        var ledger = gate.Read();
        Assert.True(ledger.Refused);
        Assert.Equal(reset, ledger.LimitedUntil);
        Assert.False(gate.UseFallback());
    }

    [Fact]
    public void Settling_a_batch_that_holds_nothing_still_adds_its_dollars()
    {
        var gate = new ApiFallback(10m, null, _clock);

        gate.Settle("batch-x", 2m, refused: false, resetsAt: null);

        Assert.Equal(2m, gate.Read().SpentUsd);
    }

    [Fact]
    public void A_new_utc_day_drops_the_reservations_with_the_spend()
    {
        var gate = new ApiFallback(10m, null, _clock);
        gate.Reserve("batch-a");

        _clock.Now = _clock.Now.AddDays(1);

        Assert.Null(gate.Read().Reserved);
        Assert.Equal(10m, gate.Reserve("batch-b"));
    }
}
