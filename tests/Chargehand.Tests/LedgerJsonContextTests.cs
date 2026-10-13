using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0060: the run log writes its records without reflection, byte for byte as before, so existing logs still read.</summary>
public class LedgerJsonContextTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_start_record_is_written_as_before() =>
        Same(new StartRecord("run-1", At, new string('0', 32), Runs.DraftRequest(), 42, "parent", "boot", "alice"), LedgerJsonContext.Default.StartRecord);

    [Fact]
    public void A_call_record_with_its_computed_fields_is_written_as_before() =>
        Same(new CallRecord("run-1", "n1", "worker", "s1", "m1", "p/small", At, 1234.5, new TokenCounts(70, 30, 5, 20, 10), 0.25m,
            new PromptChain([new ChainBlock("b", "1", new string('a', 64), BlockSource.Runtime)], new AsSent("v", "a", "m", "d")), "n0",
            [new InstructionRef("k", new string('b', 64))]), LedgerJsonContext.Default.CallRecord);

    [Fact]
    public void A_run_record_with_extensions_is_written_as_before() =>
        Same(new RunRecord("run-1", At, At.AddMinutes(1), "draft", "answer", null, Runs.Completed(), "answer",
            new ExtensionsReport([new MemoryReport("m", 2, null, 0, "no commit")], [new ServiceReport("svc", ["t"], [])]), "alice"),
            LedgerJsonContext.Default.RunRecord);

    [Fact]
    public void A_score_record_is_written_as_before() =>
        Same(new ScoreRecord("run-1", "quality", 0.75, At, "eval"), LedgerJsonContext.Default.ScoreRecord);

    private static void Same<T>(T record, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        var before = JsonSerializer.Serialize(record, ContractJson.Options);
        Assert.Equal(before, JsonSerializer.Serialize(record, info));
        Assert.Equal(before, JsonSerializer.Serialize(JsonSerializer.Deserialize(before, info)!, info));
    }
}
