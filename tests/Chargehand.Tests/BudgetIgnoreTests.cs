using Chargehand.Runtimes.ClaudeCode.Api;
using Xunit;

namespace Chargehand.Tests;

public sealed class BudgetIgnoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"budget-ignore-{Guid.NewGuid():N}");
    private readonly Clock _clock = new();

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    [Fact]
    public void An_ignore_lasts_four_hours_and_each_budget_has_its_own()
    {
        var ignore = new BudgetIgnore(_dir, _clock);
        ignore.Set(BudgetIgnore.Api, "ship the fix", "me");
        Assert.NotNull(ignore.Active(BudgetIgnore.Api));
        Assert.Null(ignore.Active(BudgetIgnore.Subscription));
        _clock.Now += TimeSpan.FromHours(4) + TimeSpan.FromSeconds(1);
        Assert.Null(ignore.Active(BudgetIgnore.Api));
    }

    [Fact]
    public void A_reason_is_required_and_every_change_is_logged()
    {
        var ignore = new BudgetIgnore(_dir, _clock);
        Assert.Throws<ArgumentException>(() => ignore.Set(BudgetIgnore.Subscription, "  ", "me"));
        ignore.Set(BudgetIgnore.Subscription, "release week", "me");
        Assert.True(ignore.Clear(BudgetIgnore.Subscription, "me"));
        var log = File.ReadAllLines(Path.Combine(_dir, "budget-ignore.jsonl"));
        Assert.Contains("\"event\":\"set\"", log[0]);
        Assert.Contains("release week", log[0]);
        Assert.Contains("\"event\":\"clear\"", log[1]);
    }

    [Fact]
    public void The_api_ignore_lifts_the_daily_cap_and_a_refusal_but_the_subscription_ignore_does_not()
    {
        var ignore = new BudgetIgnore(_dir, _clock);
        var gate = new ApiFallback(5m, null, _clock, ignore);
        gate.Limited(_clock.GetUtcNow().AddHours(2));
        gate.Spent(6m);
        gate.Refused();
        Assert.False(gate.UseFallback());
        ignore.Set(BudgetIgnore.Subscription, "x", "me");
        Assert.False(gate.UseFallback());
        Assert.True(gate.SubscriptionIgnored);
        Assert.False(gate.ApiIgnored);
        ignore.Set(BudgetIgnore.Api, "x", "me");
        Assert.True(gate.UseFallback());
        Assert.True(gate.Reserve("batch") > 0);
        ignore.Clear(BudgetIgnore.Api, "me");
        Assert.False(gate.UseFallback());
        Assert.Contains("\"event\":\"used\"", File.ReadAllText(Path.Combine(_dir, "budget-ignore.jsonl")));
    }

    [Fact]
    public void The_command_sets_shows_and_clears_one_budget_at_a_time()
    {
        var output = new StringWriter();
        Assert.Equal(0, BudgetCli.Run(["ignore", "api", "--reason", "why"], _dir, "me", _clock, output, TextWriter.Null));
        var status = new StringWriter();
        BudgetCli.Run(["status"], _dir, "me", _clock, status, TextWriter.Null);
        Assert.Contains("api: ignored until", status.ToString());
        Assert.Contains("subscription: enforced", status.ToString());
        Assert.Equal(2, BudgetCli.Run(["ignore", "api"], _dir, "me", _clock, TextWriter.Null, TextWriter.Null));
        Assert.Equal(0, BudgetCli.Run(["clear", "api"], _dir, "me", _clock, TextWriter.Null, TextWriter.Null));
    }
}
