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
/// line that has no newline yet.
/// </summary>
public sealed class JsonlRunLog(string path) : IRunLog
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public Task AppendAsync(StartRecord record, CancellationToken ct) => Append("start", JsonSerializer.SerializeToNode(record, LedgerJsonContext.Default.StartRecord)!, ct);

    public Task AppendAsync(CallRecord record, CancellationToken ct) => Append("call", JsonSerializer.SerializeToNode(record, LedgerJsonContext.Default.CallRecord)!, ct);

    public Task AppendAsync(RunRecord record, CancellationToken ct) => Append("run", JsonSerializer.SerializeToNode(record, LedgerJsonContext.Default.RunRecord)!, ct);

    public Task AppendAsync(ScoreRecord record, CancellationToken ct) => Append("score", JsonSerializer.SerializeToNode(record, LedgerJsonContext.Default.ScoreRecord)!, ct);

    public async Task<RunEntry> ReadAsync(string runId, CancellationToken ct)
    {
        StartRecord? start = null;
        RunRecord? run = null;
        var calls = new List<CallRecord>();
        foreach (var node in await Lines(ct))
        {
            if (node["run_id"]?.GetValue<string>() != runId)
                continue;
            switch (node["type"]?.GetValue<string>())
            {
                case "start":
                    start = node.Deserialize(LedgerJsonContext.Default.StartRecord);
                    break;
                case "run":
                    run = node.Deserialize(LedgerJsonContext.Default.RunRecord);
                    break;
                case "call":
                    calls.Add(node.Deserialize(LedgerJsonContext.Default.CallRecord)!);
                    break;
            }
        }
        return new(start, run, calls);
    }

    public async Task<IReadOnlyList<RunSummary>> ListAsync(RunListQuery query, CancellationToken ct)
    {
        // ponytail: reads the whole log per call, like ReadAllAsync; index it if a list is ever polled often over a large log.
        var data = await ReadAllAsync(ct);
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
    public static RunSummary SummaryOf(StartRecord start, RunRecord? run) =>
        new("run-summary/v1", start.RunId, run is not null ? RunStatus.StateOf(run.Result.Status) : start.OwnerAlive() ? RunState.Running : RunState.Lost, run?.Preset ?? start.Request.Context.Preset,
            start.Started, start.ParentRunId, null, run?.Finished, run?.Result.Usage.Usd, ArtifactField(run?.Result, "branch", "branch"), ArtifactField(run?.Result, "pull-request", "url"),
            run?.Result.Error?.Code, start.Actor, start.Request.Driven is null ? "single" : "batch", start.Request.Context.Repository?.Path,
            run is null ? null : run.Result.Usage.Input + run.Result.Usage.Output, start.Request.Context.Repository?.Commit);

    public async Task<ActorSpend> SpendAsync(string actor, DateTimeOffset since, CancellationToken ct)
    {
        var data = await ReadAllAsync(ct);
        var starts = data.Starts.GroupBy(s => s.RunId).ToDictionary(g => g.Key, g => g.Last());
        var finished = data.Runs.GroupBy(r => r.RunId).ToDictionary(g => g.Key, g => g.Last());
        // A task or call under a batch is inside the batch's usage.
        bool InBatch(StartRecord start)
        {
            for (var parent = start.ParentRunId; parent is not null && starts.TryGetValue(parent, out var p); parent = p.ParentRunId)
                if (p.Request.Driven is not null)
                    return true;
            return false;
        }
        decimal usd = 0;
        long tokens = 0;
        foreach (var start in starts.Values.Where(s => s.Actor == actor && !InBatch(s)))
        {
            if (finished.TryGetValue(start.RunId, out var run))
            {
                if (run.Finished < since)
                    continue;
                usd += run.Result.Usage.Usd ?? 0;
                tokens += run.Result.Usage.Input + run.Result.Usage.Output;
            }
            else if (start.OwnerAlive())
                foreach (var call in data.Calls.Where(c => c.RunId == start.RunId && c.Started >= since))
                {
                    usd += call.Usd ?? 0;
                    tokens += call.Tokens is { } t ? t.Input + t.Output : 0;
                }
        }
        return new(usd, tokens);
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
        foreach (var node in await Lines(ct))
            switch (node["type"]?.GetValue<string>())
            {
                case "start":
                    starts.Add(node.Deserialize(LedgerJsonContext.Default.StartRecord)!);
                    break;
                case "run":
                    runs.Add(node.Deserialize(LedgerJsonContext.Default.RunRecord)!);
                    break;
                case "call":
                    calls.Add(node.Deserialize(LedgerJsonContext.Default.CallRecord)!);
                    break;
                case "score":
                    scores.Add(node.Deserialize(LedgerJsonContext.Default.ScoreRecord)!);
                    break;
            }
        return new(starts, runs, calls, scores);
    }

    private async Task<IEnumerable<JsonObject>> Lines(CancellationToken ct)
    {
        if (!File.Exists(path))
            return [];
        var text = await File.ReadAllTextAsync(path, ct);
        // Another process may be mid-append: only newline-terminated lines are complete.
        return text[..(text.LastIndexOf('\n') + 1)].Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject());
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
