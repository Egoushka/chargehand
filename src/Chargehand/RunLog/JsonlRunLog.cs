using System.Text.Json;
using System.Text.Json.Nodes;
using Chargehand.Contracts;

namespace Chargehand.RunLog;

/// <summary>One JSON object per line, tagged "call" or "run". Local file, gitignored.</summary>
public sealed class JsonlRunLog(string path) : IRunLog
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public Task AppendAsync(CallRecord record, CancellationToken ct) => Append("call", JsonSerializer.SerializeToNode(record, ContractJson.Options)!, ct);

    public Task AppendAsync(RunRecord record, CancellationToken ct) => Append("run", JsonSerializer.SerializeToNode(record, ContractJson.Options)!, ct);

    public async Task<(RunRecord? Run, IReadOnlyList<CallRecord> Calls)> ReadAsync(string runId, CancellationToken ct)
    {
        RunRecord? run = null;
        var calls = new List<CallRecord>();
        if (!File.Exists(path))
            return (null, calls);
        foreach (var line in await File.ReadAllLinesAsync(path, ct))
        {
            var node = JsonNode.Parse(line)!.AsObject();
            if (node["run_id"]?.GetValue<string>() != runId)
                continue;
            if (node["type"]?.GetValue<string>() == "run")
                run = node.Deserialize<RunRecord>(ContractJson.Options);
            else
                calls.Add(node.Deserialize<CallRecord>(ContractJson.Options)!);
        }
        return (run, calls);
    }

    private async Task Append(string type, JsonNode node, CancellationToken ct)
    {
        node["type"] = type;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await Gate.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(path, node.ToJsonString() + "\n", ct);
        }
        finally
        {
            Gate.Release();
        }
    }
}
