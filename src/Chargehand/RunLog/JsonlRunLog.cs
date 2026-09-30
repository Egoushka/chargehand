using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chargehand.Contracts;

namespace Chargehand.RunLog;

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

    public Task AppendAsync(StartRecord record, CancellationToken ct) => Append("start", JsonSerializer.SerializeToNode(record, ContractJson.Options)!, ct);

    public Task AppendAsync(CallRecord record, CancellationToken ct) => Append("call", JsonSerializer.SerializeToNode(record, ContractJson.Options)!, ct);

    public Task AppendAsync(RunRecord record, CancellationToken ct) => Append("run", JsonSerializer.SerializeToNode(record, ContractJson.Options)!, ct);

    public Task AppendAsync(ScoreRecord record, CancellationToken ct) => Append("score", JsonSerializer.SerializeToNode(record, ContractJson.Options)!, ct);

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
                    start = node.Deserialize<StartRecord>(ContractJson.Options);
                    break;
                case "run":
                    run = node.Deserialize<RunRecord>(ContractJson.Options);
                    break;
                case "call":
                    calls.Add(node.Deserialize<CallRecord>(ContractJson.Options)!);
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
        foreach (var start in data.Starts.GroupBy(s => s.RunId).Select(g => g.First()))
        {
            if (query.Since is { } since && start.Started < since)
                continue;
            finished.TryGetValue(start.RunId, out var run);
            var state = run is not null ? RunStatus.StateOf(run.Result.Status) : start.OwnerAlive() ? RunState.Running : RunState.Lost;
            if (query.Status is { } wanted && wanted != state)
                continue;
            rows.Add(new RunSummary("run-summary/v1", start.RunId, state, run?.Preset ?? start.Request.Context.Preset, start.Started, start.ParentRunId,
                null, run?.Finished, run?.Result.Usage.Usd, ArtifactField(run?.Result, "branch", "branch"), ArtifactField(run?.Result, "pull-request", "url"), run?.Result.Error?.Code));
        }
        return [.. rows.OrderByDescending(r => r.StartedAt).Take(Math.Clamp(query.Limit, 1, 500))];
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
                    starts.Add(node.Deserialize<StartRecord>(ContractJson.Options)!);
                    break;
                case "run":
                    runs.Add(node.Deserialize<RunRecord>(ContractJson.Options)!);
                    break;
                case "call":
                    calls.Add(node.Deserialize<CallRecord>(ContractJson.Options)!);
                    break;
                case "score":
                    scores.Add(node.Deserialize<ScoreRecord>(ContractJson.Options)!);
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
