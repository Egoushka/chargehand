using Chargehand.Modules.Events.Api;

namespace Chargehand.Modules.Events.Tests;

public sealed class ReservedTypeTests
{
    [Fact]
    public async Task Signal_and_wake_events_are_stored_and_read_back_by_type_glob_with_causation()
    {
        using var log = new Log();
        await log.PostAsync(Log.Envelope("sig1", "signal.rule.fired", subject: "rule-1", data: "{\"rule\":\"rule-1\",\"facts\":[1],\"proposal\":null}"));
        await log.PostAsync(Log.Envelope("wake1", "wake.job.finished", subject: "job-1", causation: "sig1", data: "{\"job\":\"job-1\",\"outcome\":\"ok\",\"proposals\":0}"));
        await log.PostAsync(Log.Envelope("other", "claude.tool.called"));

        var signals = Log.Parse(await log.Service.QueryAsync(EventGrant.Owner, new EventQuery(Type: "signal.*"), CancellationToken.None)).GetProperty("events");
        var wakes = Log.Parse(await log.Service.QueryAsync(EventGrant.Owner, new EventQuery(Type: "wake.*"), CancellationToken.None)).GetProperty("events");

        Assert.Equal("sig1", Assert.Single(signals.EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal("sig1", Assert.Single(wakes.EnumerateArray()).GetProperty("causationid").GetString());
    }

    [Fact]
    public async Task No_thread_is_derived_from_reserved_types()
    {
        using var log = new Log();
        await log.PostAsync(Log.Envelope("sig1", "signal.rule.fired", subject: "rule-1"));
        await log.PostAsync(Log.Envelope("wake1", "wake.job.started", subject: "job-1", causation: "sig1"));

        var threads = Log.Parse(await log.Service.ThreadsAsync(EventGrant.Owner, "all", null, null, CancellationToken.None)).GetProperty("threads");

        Assert.Equal(0, threads.GetArrayLength());
        Assert.Equal(404, (await log.Service.CardAsync(EventGrant.Owner, "rule-1", CancellationToken.None)).Status);
    }
}
