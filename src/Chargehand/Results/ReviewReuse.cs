using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Results;

/// <summary>
/// A review of a head commit and diff that a completed review already answered returns that review instead of running again (issue 448: 15 reviews
/// re-reviewed an identical diff). The stored review must be the same account's, on the same commit, with a diff input of the same SHA-256.
/// </summary>
public static class ReviewReuse
{
    public const string Preset = "review";
    public const string ArtifactKind = "reused-review";

    /// <summary>The SHA-256 of the request's <c>diff</c> input; null for a request that is not a review of a commit with a diff.</summary>
    public static string? DiffHash(RunRequest request) =>
        request.Context.Preset == Preset && request.Context.Repository is not null && request.Driven is null
        && request.Inputs?.FirstOrDefault(i => i.Kind == "diff") is { } diff
            ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(diff.Text)))
            : null;

    /// <summary>The latest completed review of the same commit and diff hash that <paramref name="actor"/> started; null when there is none or the log cannot list its runs.</summary>
    public static async Task<RunRecord?> FindAsync(IRunLog log, RunRequest request, string? actor, CancellationToken ct)
    {
        if (DiffHash(request) is not { } hash)
            return null;
        RunLogData data;
        try
        {
            data = await log.ReadRunsAsync(ct);
        }
        catch (NotSupportedException)
        {
            return null;
        }
        var commit = request.Context.Repository!.Commit;
        var starts = data.Starts.Where(s => s.Actor == actor && s.Request.Context.Repository?.Commit == commit).ToDictionary(s => s.RunId);
        return data.Runs
            .Where(r => r.Preset == Preset && r.Result.Status == ResultStatus.Completed && starts.TryGetValue(r.RunId, out var s) && DiffHash(s.Request) == hash)
            .OrderByDescending(r => r.Finished)
            .FirstOrDefault();
    }

    /// <summary>The stored review as this run's result: its own run and trace ids, no cost, no signature, and an artifact naming the run it came from.</summary>
    public static ResultContract Reuse(RunRecord stored, string runId, string traceId, string commit, string diffHash)
    {
        var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("run_id", stored.RunId);
            json.WriteString("commit", commit);
            json.WriteString("diff_sha256", diffHash);
            json.WriteEndObject();
        }
        var content = Encoding.UTF8.GetString(buffer.ToArray());
        var artifact = new Artifact(ArtifactKind, "application/json", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))), Content: content);
        return stored.Result with
        {
            TaskId = runId,
            TraceId = traceId,
            Artifacts = [.. stored.Result.Artifacts.Where(a => a.Kind != ArtifactKind), artifact],
            Usage = new Usage(0, 0, 0, 0, 0),
            Signature = null,
        };
    }
}
