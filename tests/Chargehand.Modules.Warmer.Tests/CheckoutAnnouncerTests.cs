using System.Text.Json;
using Chargehand.Modules.Events.Api;
using Chargehand.Modules.Warmer.Api;

namespace Chargehand.Modules.Warmer.Tests;

public sealed class CheckoutAnnouncerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "chargehand-announce-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteEventLog log;

    public CheckoutAnnouncerTests()
    {
        Directory.CreateDirectory(root);
        log = SqliteEventLog.Open(Path.Combine(root, "events.db"));
    }

    public void Dispose()
    {
        log.Dispose();
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task A_checkout_is_announced_by_its_name_and_a_second_time_is_a_second_event()
    {
        await CheckoutAnnouncer.AnnounceAsync(log, "repo-1234abcd/0123456789ab", default);
        await CheckoutAnnouncer.AnnounceAsync(log, "repo-1234abcd/0123456789ab", default);
        var events = await log.ReadAsync(new EventFilter(Type: CacheWarmer.CreatedType), default);
        Assert.Equal(2, events.Count);
        using var doc = JsonDocument.Parse(events[0].Envelope);
        Assert.Equal("repo-1234abcd/0123456789ab", doc.RootElement.GetProperty("data").GetProperty("checkout").GetString());
        Assert.Equal("chargehand/orchestrator", events[0].Source);
    }

    [Fact]
    public async Task A_log_that_fails_never_throws()
    {
        log.Dispose();
        await CheckoutAnnouncer.AnnounceAsync(log, "repo-1234abcd/0123456789ab", default);
    }
}
