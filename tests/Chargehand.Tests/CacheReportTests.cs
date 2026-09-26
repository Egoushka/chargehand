using Chargehand.Contracts;
using Chargehand.RunLog;
using Chargehand.Runtime;

namespace Chargehand.Tests;

public class CacheReportTests
{
    private static readonly PromptChain Chain = new(
        [new ChainBlock("core/worker", "0.2.0", new string('c', 64), BlockSource.Registry), new ChainBlock("preset/cheap", "0.1.0", new string('p', 64), BlockSource.Registry)],
        new AsSent("2.0.16", "build", "p/m", "2026-09-26"));

    private static readonly InstructionRef[] Entries = [new("chargehand-core", new string('c', 64)), new("chargehand-preset", new string('p', 64))];

    private static CallRecord Call(string node, int second, long read, long write, string? forkedFrom = null, InstructionRef[]? entries = null, long input = 300) =>
        new("run-1", node, "worker", $"ses_{node}", $"msg_{node}_{second}", "p/m", DateTimeOffset.UnixEpoch.AddSeconds(second), 1000,
            new TokenCounts(input, 50, 0, read, write), 0.001m, Chain, forkedFrom, entries ?? Entries);

    [Fact]
    public void Forked_sibling_reads_the_first_nodes_prefix()
    {
        var report = CacheReport.Build([Call("s1", 1, 0, 4500), Call("s1", 2, 4800, 60), Call("s2", 3, 4479, 250, forkedFrom: "ses_s1")]);
        Assert.Contains("s1: first node: writes the session prefix (4500 written)", report, StringComparison.Ordinal);
        Assert.Contains("s2: forked from s1: first call read 4479 of 5029", report, StringComparison.Ordinal);
        Assert.DoesNotContain("could not fork", report, StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_that_differs_between_siblings_is_named()
    {
        var broken = new[] { Entries[0], new InstructionRef("chargehand-preset", new string('9', 64)) };
        var report = CacheReport.Build([Call("s1", 1, 0, 4500), Call("s2", 3, 0, 4500, entries: broken)]);
        Assert.Contains("s2: fresh session, first call read 0 of 4800: instruction entry `chargehand-preset` (pppppppppppp → 999999999999) differs from s1",
            report, StringComparison.Ordinal);
    }

    [Fact]
    public void A_drop_inside_a_session_without_a_changed_block_points_to_the_checklist()
    {
        var report = CacheReport.Build([Call("s1", 1, 0, 4500), Call("s1", 2, 100, 4700)]);
        Assert.Contains("call 2: read 100 after a 4800-token prompt: no orchestrator block changed", report, StringComparison.Ordinal);
        Assert.Contains("date rollover", report, StringComparison.Ordinal);
    }
}
