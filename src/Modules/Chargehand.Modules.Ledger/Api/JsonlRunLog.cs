using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chargehand.Contracts;

namespace Chargehand.Modules.Ledger.Api;

/// <summary>Every record in the log, for reports.</summary>
public sealed record RunLogData(IReadOnlyList<StartRecord> Starts, IReadOnlyList<RunRecord> Runs, IReadOnlyList<CallRecord> Calls, IReadOnlyList<ScoreRecord> Scores);

/// <summary>
/// One JSON object per line, tagged "start", "call", "run" or "score". Local file, gitignored. Several processes append
/// to it (CLI runs, the server, evals): an append holds an exclusive lock on "&lt;path&gt;.lock", and readers skip a last
/// line that has no newline yet. A line that is not JSON (NUL bytes or half a record, left by a hard stop) is skipped
/// with one warning, which names the line and its length and never its content; the file is not changed.
/// </summary>
public sealed class JsonlRunLog(string path, Action<string>? warn = null) : IRunLog
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly Action<string> _warn = warn ?? Console.Error.WriteLine;

    private readonly ConcurrentDictionary<long, byte> _reported = new();

    public Task AppendAsync(StartRecord record, CancellationToken ct) => Append("start", JsonSerializer.SerializeToNode(record, LedgerJsonContext.Default.StartRecord)!, ct);

    public Task AppendAsync(CallRecord record, CancellationToken ct) => Append("call", JsonSerializer.SerializeToNode(record, LedgerJsonContext.Default.CallRecord)!, ct);

    public Task AppendAsync(RunRecord record, CancellationToken ct) => Append("run", JsonSerializer.SerializeToNode(record, LedgerJsonContext.Default.RunRecord)!, ct);

    public Task AppendAsync(ScoreRecord record, CancellationToken ct) => Append("score", JsonSerializer.SerializeToNode(record, LedgerJsonContext.Default.ScoreRecord)!, ct);

    public async Task<RunEntry> ReadAsync(string runId, CancellationToken ct)
    {
        StartRecord? start = null;
        RunRecord? run = null;
        var calls = new List<CallRecord>();
        await foreach (var line in Lines(ct))
        {
            using var doc = line;
            var root = doc.RootElement;
            if (StringOf(root, "run_id") != runId)
                continue;
            switch (StringOf(root, "type"))
            {
                case "start":
                    start = root.Deserialize(LedgerJsonContext.Default.StartRecord);
                    break;
                case "run":
                    run = root.Deserialize(LedgerJsonContext.Default.RunRecord);
                    break;
                case "call":
                    calls.Add(root.Deserialize(LedgerJsonContext.Default.CallRecord)!);
                    break;
            }
        }
        return new(start, run, calls);
    }

    public async Task<IReadOnlyList<RunSummary>> ListAsync(RunListQuery query, CancellationToken ct)
    {
        var data = await ReadRunsAsync(ct);
        var finished = data.Runs.GroupBy(r => r.RunId).ToDictionary(g => g.Key, g => g.Last());
        var rows = new List<RunSummary>();
        foreach (var start in data.Starts.GroupBy(s => s.RunId).Select(g => g.Last()))
        {
            if (!Matches(query, start))
                continue;
            finished.TryGetValue(start.RunId, out var run);
            var summary = SummaryOf(start, run);
            if (query.Status is { } wanted && wanted != summary.Status)
                continue;
            rows.Add(summary);
        }
        return [.. rows.OrderByDescending(r => r.StartedAt).Take(Math.Clamp(query.Limit, 1, 500))];
    }

    /// <summary>Whether a start record passes the query's filters that do not need the run record.</summary>
    private static bool Matches(RunListQuery query, StartRecord start)
    {
        var kind = start.Request.Driven is null ? "single" : "batch";
        return (query.Since is not { } since || start.Started >= since)
            && (query.Actor is not { } actor || start.Actor == actor)
            && (query.Kind is not { } wantedKind || wantedKind == kind)
            && (query.Repository is not { } wantedRepository || wantedRepository == start.Request.Context.Repository?.Path)
            && (query.RunId is not { } wantedId || wantedId == start.RunId);
    }

    /// <summary>The run-summary/v1 row of a run: from its start record and, once it finished, its run record.</summary>
    public static RunSummary SummaryOf(StartRecord start, RunRecord? run) => Paid(
        new("run-summary/v1", start.RunId, run is not null ? RunStatus.StateOf(run.Result.Status) : start.OwnerAlive() ? RunState.Running : RunState.Lost, run?.Preset ?? start.Request.Context.Preset,
            start.Started, start.ParentRunId, null, run?.Finished, run?.Result.Usage.Usd, ArtifactField(run?.Result, "branch", "branch"), ArtifactField(run?.Result, "pull-request", "url"),
            run?.Result.Error?.Code, start.Actor, start.Request.Driven is null ? "single" : "batch", start.Request.Context.Repository?.Path,
            run is null ? null : run.Result.Usage.Input + run.Result.Usage.Output, start.Request.Context.Repository?.Commit)
        { Name = RunNames.Of(start.RunId) }, run);

    /// <summary>The row with who paid for the run and how much of the subscription's windows it had used (ADR 0082, 0098): only a finished run's record holds them.</summary>
    private static RunSummary Paid(RunSummary row, RunRecord? run) => row with { Credential = run?.Credential, SubscriptionUsage = run?.SubscriptionUsage };

    public async Task<ActorSpend> SpendAsync(string actor, DateTimeOffset since, CancellationToken ct)
    {
        var (starts, finished, calls) = await ReadSpendRowsAsync(since, ct);
        var callsOf = calls.ToLookup(c => c.RunId);
        // A task or call under a batch is inside the batch's usage.
        bool InBatch(SpendStart start)
        {
            for (var parent = start.ParentRunId; parent is not null && starts.TryGetValue(parent, out var p); parent = p.ParentRunId)
                if (p.Driven)
                    return true;
            return false;
        }
        var total = new SpendTotal();
        foreach (var (runId, start) in starts.Where(s => s.Value.Actor == actor && !InBatch(s.Value)))
        {
            if (finished.TryGetValue(runId, out var run))
            {
                if (run.Finished < since)
                    continue;
                total.Add(run.Usd, run.Credential, run.Tokens);
            }
            else if (ProcessBoot.OwnerAlive(start.BootId, start.Pid))
                foreach (var call in callsOf[runId])
                {
                    total.Add(call.Usd, call.Credential, call.Tokens);
                }
        }
        return total.Result();
    }

    public async Task<IReadOnlyList<(DateTimeOffset At, SubscriptionUsage Usage)>> SubscriptionUsageAsync(DateTimeOffset since, CancellationToken ct)
    {
        List<(DateTimeOffset At, SubscriptionUsage Usage)> readings = [];
        await foreach (var line in Lines(ct))
        {
            using var doc = line;
            var root = doc.RootElement;
            if (StringOf(root, "type") != "call")
                continue;
            var call = root.Deserialize(LedgerJsonContext.Default.CallRecord)!;
            if (call.SubscriptionUsage is { } usage && call.Started >= since)
                readings.Add((call.Started, usage));
        }
        readings.Sort((a, b) => a.At.CompareTo(b.At));
        return readings;
    }

    /// <summary>One pass over the lines, keeping a few fields of each run: the account standing is read on every console request, so it must not hold the log's records.
    /// Calls older than <paramref name="since"/> are not kept.</summary>
    private async Task<(Dictionary<string, SpendStart> Starts, Dictionary<string, SpendRun> Finished, List<SpendCall> Calls)> ReadSpendRowsAsync(DateTimeOffset since, CancellationToken ct)
    {
        Dictionary<string, SpendStart> starts = [];
        Dictionary<string, SpendRun> finished = [];
        List<SpendCall> calls = [];
        await foreach (var line in Lines(ct))
        {
            using var doc = line;
            var root = doc.RootElement;
            switch (StringOf(root, "type"))
            {
                case "start":
                    var start = root.Deserialize(LedgerJsonContext.Default.StartRecord)!;
                    starts[start.RunId] = new(start.Actor, start.ParentRunId, start.Request.Driven is not null, start.BootId, start.Pid);
                    break;
                case "run":
                    var run = root.Deserialize(LedgerJsonContext.Default.RunRecord)!;
                    finished[run.RunId] = new(run.Finished, run.Result.Usage.Usd, run.Credential, run.Result.Usage.Input + run.Result.Usage.Output);
                    break;
                case "call":
                    var call = root.Deserialize(LedgerJsonContext.Default.CallRecord)!;
                    if (call.Started >= since)
                        calls.Add(new(call.RunId, call.Usd, call.Credential, call.Tokens is { } t ? t.Input + t.Output : 0));
                    break;
            }
        }
        return (starts, finished, calls);
    }

    private readonly record struct SpendStart(string? Actor, string? ParentRunId, bool Driven, string? BootId, int Pid);

    private readonly record struct SpendRun(DateTimeOffset Finished, decimal? Usd, string? Credential, long Tokens);

    private readonly record struct SpendCall(string RunId, decimal? Usd, string? Credential, long Tokens);

    /// <summary>Dollars count as billed only for a credential that is billed; tokens and the price-table figure count for every one.</summary>
    private sealed class SpendTotal
    {
        private decimal _billed, _atApiPrice;
        private long _tokens;

        public void Add(decimal? usd, string? credential, long tokens)
        {
            _atApiPrice += usd ?? 0;
            if (CredentialKind.Billed(credential))
                _billed += usd ?? 0;
            _tokens += tokens;
        }

        public ActorSpend Result() => new(_billed, _tokens, _atApiPrice);
    }

    /// <summary>A string member of a result artifact's inline JSON (the branch artifact's <c>branch</c>, the pull request's <c>url</c>); null when there is none.</summary>
    private static string? ArtifactField(ResultContract? result, string kind, string member)
    {
        var content = result?.Artifacts.FirstOrDefault(a => a.Kind == kind)?.Content;
        if (content is null)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(content);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(member, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<RunLogData> ReadAllAsync(CancellationToken ct)
    {
        List<StartRecord> starts = [];
        List<RunRecord> runs = [];
        List<CallRecord> calls = [];
        List<ScoreRecord> scores = [];
        await foreach (var line in Lines(ct))
        {
            using var doc = line;
            var root = doc.RootElement;
            switch (StringOf(root, "type"))
            {
                case "start":
                    starts.Add(root.Deserialize(LedgerJsonContext.Default.StartRecord)!);
                    break;
                case "run":
                    runs.Add(root.Deserialize(LedgerJsonContext.Default.RunRecord)!);
                    break;
                case "call":
                    calls.Add(root.Deserialize(LedgerJsonContext.Default.CallRecord)!);
                    break;
                case "score":
                    scores.Add(root.Deserialize(LedgerJsonContext.Default.ScoreRecord)!);
                    break;
            }
        }
        return new(starts, runs, calls, scores);
    }

    /// <summary>The latest start and run record of each run, in the order the runs first appear; calls and scores are parsed no further than their tag, so the console's run index holds a part of the log.</summary>
    public async Task<RunLogData> ReadRunsAsync(CancellationToken ct)
    {
        Dictionary<string, StartRecord> starts = [];
        Dictionary<string, RunRecord> runs = [];
        await foreach (var line in Lines(ct))
        {
            using var doc = line;
            var root = doc.RootElement;
            switch (StringOf(root, "type"))
            {
                case "start":
                    var start = root.Deserialize(LedgerJsonContext.Default.StartRecord)!;
                    starts[start.RunId] = start;
                    break;
                case "run":
                    var run = root.Deserialize(LedgerJsonContext.Default.RunRecord)!;
                    runs[run.RunId] = run;
                    break;
            }
        }
        return new([.. starts.Values], [.. runs.Values], [], []);
    }

    private static string? StringOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The log's complete lines, one at a time, each parsed on its own: no more than a line is in memory at once. A reader that keeps a document past the next step reads over a buffer that has moved on.</summary>
    private async IAsyncEnumerable<JsonDocument> Lines([EnumeratorCancellation] CancellationToken ct)
    {
        if (!File.Exists(path))
            yield break;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        var filled = 0;
        long number = 0;
        while (true)
        {
            if (filled == buffer.Length)
                Array.Resize(ref buffer, buffer.Length * 2);
            var read = await file.ReadAsync(buffer.AsMemory(filled), ct);
            // Another process may be mid-append: only newline-terminated lines are complete, so what follows the last newline is left.
            if (read == 0)
                yield break;
            filled += read;
            var from = 0;
            int end;
            while ((end = Array.IndexOf(buffer, (byte)'\n', from, filled - from)) >= 0)
            {
                number++;
                if (end > from && TryParse(buffer.AsMemory(from, end - from), number) is { } document)
                    yield return document;
                from = end + 1;
            }
            Buffer.BlockCopy(buffer, from, buffer, 0, filled - from);
            filled -= from;
        }
    }

    /// <summary>The line as a document, or null (after one warning per line for the life of this log) when it is not JSON.</summary>
    private JsonDocument? TryParse(ReadOnlyMemory<byte> line, long number)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            if (_reported.TryAdd(number, 0))
                _warn($"chargehand: skipped unreadable run-log line {number} ({line.Length} bytes)");
            return null;
        }
    }

    private async Task Append(string type, JsonNode node, CancellationToken ct)
    {
        node["type"] = type;
        var line = Encoding.UTF8.GetBytes(node.ToJsonString() + "\n");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await Gate.WaitAsync(ct);
        try
        {
            // On Unix, FileMode.Append opens without O_APPEND and writes at the length it read when opening, so two
            // processes appending at once could overwrite each other's lines. The lock file serialises them: FileShare.None
            // takes an exclusive flock, and the log is opened (and its length read) only while it is held.
            await using var held = await Lock(ct);
            await using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await file.WriteAsync(line, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<FileStream> Lock(CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
            try
            {
                return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 250)
            {
                await Task.Delay(20, ct);
            }
    }
}
