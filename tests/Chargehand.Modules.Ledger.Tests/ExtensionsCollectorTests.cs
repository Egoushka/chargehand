using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Modules.Ledger.Tests;

public class ExtensionsCollectorTests
{
    [Fact]
    public void The_same_unavailable_server_is_recorded_once_however_many_nodes_saw_it()
    {
        var collector = new ExtensionsCollector();
        collector.Serviced([new ServiceReport("team-docs", ["read_doc"], ["tool_missing: x"])]);

        collector.Unavailable("team-docs", "not_connected: failed");
        collector.Unavailable("team-docs", "not_connected: failed");
        collector.Unavailable("other", "not_connected: absent");

        var report = collector.ToReport()!.Services;
        Assert.Equal(["tool_missing: x", "not_connected: failed"], report.Single(s => s.Server == "team-docs").Issues);
        var other = report.Single(s => s.Server == "other");
        Assert.Equal(["not_connected: absent"], other.Issues);
        Assert.Empty(other.Tools);
    }
}
