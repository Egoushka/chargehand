using System.Text;
using System.Text.Json;
using Chargehand.Containers;
using Chargehand.Contracts;
using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary>ADR 0039: the task goes into the run's output volume as task.json, and the bundle, the report and the outcome come out into a directory chargehand owns.</summary>
public class EngineSessionVolumesTests
{
    private const string Image = "registry.example/session@sha256:1111111111111111111111111111111111111111111111111111111111111111";

    private sealed class FakeOut : IOutVolumeEngine
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public List<OutFileSpec> Specs { get; } = [];

        public Task WriteOutFileAsync(OutFileSpec spec, ReadOnlyMemory<byte> content, CancellationToken ct)
        {
            Specs.Add(spec);
            Files[spec.Name] = content.ToArray();
            return Task.CompletedTask;
        }

        public async Task<bool> ReadOutFileAsync(OutFileSpec spec, Stream destination, CancellationToken ct)
        {
            Specs.Add(spec);
            if (!Files.TryGetValue(spec.Name, out var bytes))
                return false;
            await destination.WriteAsync(bytes, ct);
            return true;
        }
    }

    [Fact]
    public async Task The_task_is_written_as_the_session_reads_it()
    {
        var engine = new FakeOut();
        var task = new SessionTask("run1", "Fix the parser", "chargehand/run1", 40, 30, 600, 500_000, MaxUsd: 2.5m);
        await new EngineSessionVolumes(engine, Image).WriteTaskAsync("run1", task, default);

        Assert.Equal(new OutFileSpec("run1", Image, "chargehand-out-run1", "task.json"), engine.Specs.Single());
        using var dir = new TempDir();
        var path = dir.Write("task.json", Encoding.UTF8.GetString(engine.Files["task.json"]));
        Assert.Equal(task, SessionCli.ReadTask(path));          // the container's own reader accepts it
        Assert.Equal("Fix the parser", JsonDocument.Parse(engine.Files["task.json"]).RootElement.GetProperty("goal").GetString());
    }

    [Fact]
    public async Task Fetch_copies_the_files_that_exist_and_leaves_no_file_for_one_that_does_not()
    {
        var engine = new FakeOut();
        engine.Files["chargehand.bundle"] = [0, 1, 2, 255];
        engine.Files["session-outcome.json"] = "{}"u8.ToArray();
        using var dir = new TempDir();
        await new EngineSessionVolumes(engine, Image).FetchOutputAsync("run1", dir.Path, default);

        Assert.Equal(new byte[] { 0, 1, 2, 255 }, File.ReadAllBytes(System.IO.Path.Combine(dir.Path, "chargehand.bundle")));
        Assert.Equal("{}", File.ReadAllText(System.IO.Path.Combine(dir.Path, "session-outcome.json")));
        Assert.False(File.Exists(System.IO.Path.Combine(dir.Path, "driven-report.json")));
        Assert.Equal(["chargehand.bundle", "driven-report.json", "session-outcome.json"], engine.Specs.Select(s => s.Name).Order());
    }

    [Fact]
    public async Task A_stand_in_still_fails_a_task_before_any_container_starts()
    {
        var e = await Assert.ThrowsAsync<ChargehandException>(() => new UnavailableSessionVolumes().WriteTaskAsync("run1", new SessionTask("run1", "g", "chargehand/run1", 1, 1, 1), default));
        Assert.Equal(ErrorCode.ContainerUnavailable, e.Code);
    }
}
