using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Modules.Sessions.Api;

/// <summary>Moves the task and the results through a session's output volume with the container engine's own file helper (the Docker CLI, or the runner service over HTTP),
/// so the runner's allowlist still decides what is touched. <paramref name="image"/> is the session image: the helper needs only <c>sh</c> and <c>cat</c>.</summary>
public sealed class EngineSessionVolumes(IOutVolumeEngine engine, string image) : ISessionVolumes
{
    public Task WriteTaskAsync(string runId, SessionTask task, CancellationToken ct) =>
        engine.WriteOutFileAsync(Spec(runId, "task.json"), JsonSerializer.SerializeToUtf8Bytes(task, SessionCli.Context.SessionTask), ct);

    public async Task WriteSeedAsync(string runId, string bundlePath, CancellationToken ct) =>
        await engine.WriteOutFileAsync(Spec(runId, ContainerTemplate.SeedFile), await File.ReadAllBytesAsync(bundlePath, ct), ct);

    public async Task FetchOutputAsync(string runId, string directory, CancellationToken ct)
    {
        foreach (var name in ContainerTemplate.OutFilesOut.Where(n => n != ContainerTemplate.UsageFile))
        {
            var path = Path.Combine(directory, name);
            bool found;
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
                try
                {
                    found = await engine.ReadOutFileAsync(Spec(runId, name), file, ct);
                }
                // A runner older than the handoff files refuses their names; a task must not fail for a file it can do without.
                catch (ChargehandException e) when (ContainerTemplate.OptionalOutFiles.Contains(name) && e.Message.Contains(": 403 ", StringComparison.Ordinal))
                {
                    found = false;
                }
            if (!found)
                File.Delete(path);
        }
    }

    public async Task<TaskUsage?> ReadUsageAsync(string runId, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        if (!await engine.ReadOutFileAsync(Spec(runId, ContainerTemplate.UsageFile), buffer, ct))
            return null;
        try
        {
            return JsonSerializer.Deserialize(buffer.ToArray(), SessionCli.Context.SessionUsage) is { Tokens: >= 0 } usage
                ? new TaskUsage(usage.Tokens, 0, usage.Turns is >= 0 ? usage.Turns : null, Enum.TryParse<SessionStage>(usage.Stage, ignoreCase: true, out var stage) ? stage : null,
                    usage.CacheRead, usage.CacheWrite, usage.UsdEstimate, usage.UsdEstimatePartial)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<byte[]?> ReadTranscriptTailAsync(string runId, int maxBytes, CancellationToken ct)
    {
        if (engine is not IOutTailEngine tailer)
            return null;
        using var buffer = new MemoryStream();
        return await tailer.ReadOutTailAsync(Spec(runId, ContainerTemplate.TranscriptFile), Math.Clamp(maxBytes, 1, ContainerTemplate.MaxTailBytes), buffer, ct) ? buffer.ToArray() : null;
    }

    private OutFileSpec Spec(string runId, string name) => new(runId, image, RunnerNames.Out(runId), name);
}
