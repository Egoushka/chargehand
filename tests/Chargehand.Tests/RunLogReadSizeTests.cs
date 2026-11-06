using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Tests;

/// <summary>The console and the account standing read the run log on every request; a large log must not be held whole while they do (CHARGEHAND-672).</summary>
public class RunLogReadSizeTests : IDisposable
{
    private const int RunCount = 300;
    private const int CallsPerRun = 40;

    // A string of the whole file is 2 bytes per char on the large object heap: the log below is about 45 MB on disk, so holding it costs 90 MB there.
    private const long LargeObjectBudget = 16L * 1024 * 1024;

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Directory.CreateTempSubdirectory("run-log-size").FullName;

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Spend_is_summed_without_holding_the_log_whole()
    {
        var path = WriteLog();
        Assert.True(new FileInfo(path).Length > 40_000_000);
        IRunLog log = new JsonlRunLog(path);

        ActorSpend spend = default!;
        var large = await LargeAllocations.During(async () => spend = await log.SpendAsync("alice", Now.AddDays(-1), CancellationToken.None));

        // Every other run is alice's, and all of those finished within the day: 150 runs of $0.5 and 100 tokens.
        Assert.Equal(new ActorSpend(75m, 15_000, 75m), spend);
        Assert.True(large < LargeObjectBudget, $"SpendAsync allocated {large:N0} bytes on the large object heap");
    }

    [Fact]
    public async Task Reading_every_record_does_not_hold_the_log_as_one_string()
    {
        var path = WriteLog();
        IRunLog log = new JsonlRunLog(path);

        RunLogData data = default!;
        var large = await LargeAllocations.During(async () => data = await log.ReadAllAsync(CancellationToken.None));

        Assert.Equal((RunCount, RunCount, RunCount * CallsPerRun), (data.Starts.Count, data.Runs.Count, data.Calls.Count));
        Assert.True(large < LargeObjectBudget, $"ReadAllAsync allocated {large:N0} bytes on the large object heap");
    }

    [Fact]
    public async Task The_console_index_reads_starts_and_runs_and_leaves_the_calls()
    {
        var path = WriteLog();
        IRunLog log = new JsonlRunLog(path);

        RunLogData data = default!;
        var large = await LargeAllocations.During(async () => data = await log.ReadRunsAsync(CancellationToken.None));

        Assert.Equal((RunCount, RunCount, 0, 0), (data.Starts.Count, data.Runs.Count, data.Calls.Count, data.Scores.Count));
        Assert.True(large < LargeObjectBudget, $"ReadRunsAsync allocated {large:N0} bytes on the large object heap");
    }

    [Fact]
    public async Task A_run_started_twice_is_one_record_in_the_console_index()
    {
        var path = Path.Combine(_dir, "runs.jsonl");
        IRunLog log = new JsonlRunLog(path);
        var request = Runs.DraftRequest();
        await log.AppendAsync(new StartRecord("r1", Now, new string('0', 32), request, 1, null, "boot", "alice"), CancellationToken.None);
        await log.AppendAsync(new StartRecord("r1", Now.AddMinutes(1), new string('0', 32), request, 2, null, "boot", "alice"), CancellationToken.None);
        await log.AppendAsync(new StartRecord("r2", Now, new string('0', 32), request, 1, null, "boot", "bob"), CancellationToken.None);

        var data = await log.ReadRunsAsync(CancellationToken.None);

        Assert.Equal([("r1", 2), ("r2", 1)], data.Starts.Select(s => (s.RunId, s.Pid)));
    }

    [Fact]
    public async Task A_line_still_being_appended_is_left_for_the_next_read()
    {
        var path = Path.Combine(_dir, "runs.jsonl");
        IRunLog log = new JsonlRunLog(path);
        await log.AppendAsync(new StartRecord("r1", Now, new string('0', 32), Runs.DraftRequest(), 1, null, "boot", "alice"), CancellationToken.None);
        await File.AppendAllTextAsync(path, "{\"type\":\"start\",\"run_");

        Assert.Single((await log.ReadAllAsync(CancellationToken.None)).Starts);
        Assert.Single((await log.ReadRunsAsync(CancellationToken.None)).Starts);
        Assert.Equal(new ActorSpend(0, 0), await log.SpendAsync("alice", Now.AddDays(-1), CancellationToken.None));
    }

    /// <summary>A log of <see cref="RunCount"/> runs, each a start, <see cref="CallsPerRun"/> calls and a run record; every other run is alice's, the rest bob's.</summary>
    private string WriteLog()
    {
        var path = Path.Combine(_dir, "runs.jsonl");
        var request = Runs.DraftRequest();
        // Instruction hashes make a call line as long as a real one.
        var instructions = Enumerable.Range(0, 30).Select(i => new InstructionRef($"instruction-{i}", new string('a', 64))).ToList();
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        void Line(string type, JsonNode node)
        {
            node["type"] = type;
            writer.Write(node.ToJsonString());
            writer.Write('\n');
        }

        for (var i = 0; i < RunCount; i++)
        {
            var id = $"run-{i:D4}";
            var actor = i % 2 == 0 ? "alice" : "bob";
            var finished = Now.AddMinutes(-i);
            Line("start", JsonSerializer.SerializeToNode(new StartRecord(id, finished.AddMinutes(-1), new string('0', 32), request, 1, null, "earlier-boot", actor), LedgerJsonContext.Default.StartRecord)!);
            for (var c = 0; c < CallsPerRun; c++)
            {
                var call = new CallRecord(id, $"n{c}", "worker", "s1", "m1", "p/small", finished.AddMinutes(-1), 120, new TokenCounts(10, 5, 0, 0, 0), 0.01m, new PromptChain([], new AsSent("2.0.16", "build", "p/m", "2026-09-26")),
                    Instructions: instructions);
                Line("call", JsonSerializer.SerializeToNode(call, LedgerJsonContext.Default.CallRecord)!);
            }
            var result = Runs.Completed() with { TaskId = id, Usage = new Usage(100, 0, 0, 0, 0.5m), Actor = actor };
            Line("run", JsonSerializer.SerializeToNode(new RunRecord(id, finished.AddMinutes(-1), finished, request.Context.Preset, null, null, result, Actor: actor), LedgerJsonContext.Default.RunRecord)!);
        }
        return path;
    }

    /// <summary>Counts what the process allocates on the large object heap while an action runs, from the runtime's allocation-tick events.</summary>
    private sealed class LargeAllocations : EventListener
    {
        private long _bytes;

        public static async Task<long> During(Func<Task> action)
        {
            using var listener = new LargeAllocations();
            await action();
            return Interlocked.Read(ref listener._bytes);
        }

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventName?.StartsWith("GCAllocationTick", StringComparison.Ordinal) != true || e.Payload is null || e.PayloadNames is null)
                return;
            var kind = e.PayloadNames.IndexOf("AllocationKind");
            var amount = e.PayloadNames.IndexOf("AllocationAmount64");
            if (kind >= 0 && amount >= 0 && Convert.ToInt32(e.Payload[kind], CultureInfo.InvariantCulture) == 1)
                Interlocked.Add(ref _bytes, Convert.ToInt64(e.Payload[amount], CultureInfo.InvariantCulture));
        }
    }
}
