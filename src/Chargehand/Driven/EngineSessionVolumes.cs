using System.Text.Json;
using Chargehand.Containers;

namespace Chargehand.Driven;

/// <summary>Moves the task and the results through a session's output volume with the container engine's own file helper (the Docker CLI, or the runner service over HTTP),
/// so the runner's allowlist still decides what is touched. <paramref name="image"/> is the session image: the helper needs only <c>sh</c> and <c>cat</c>.</summary>
public sealed class EngineSessionVolumes(IOutVolumeEngine engine, string image) : ISessionVolumes
{
    public Task WriteTaskAsync(string runId, SessionTask task, CancellationToken ct) =>
        engine.WriteOutFileAsync(Spec(runId, "task.json"), JsonSerializer.SerializeToUtf8Bytes(task, SessionCli.Json), ct);

    public async Task FetchOutputAsync(string runId, string directory, CancellationToken ct)
    {
        foreach (var name in ContainerTemplate.OutFilesOut)
        {
            var path = Path.Combine(directory, name);
            bool found;
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
                found = await engine.ReadOutFileAsync(Spec(runId, name), file, ct);
            if (!found)
                File.Delete(path);
        }
    }

    private OutFileSpec Spec(string runId, string name) => new(runId, image, RunnerNames.Out(runId), name);
}
