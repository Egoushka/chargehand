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

        /// <summary>A refusal message by file name, thrown as the runner client throws it; null: the file is served.</summary>
        public Func<string, string?>? Refuse { get; init; }

        public Task WriteOutFileAsync(OutFileSpec spec, ReadOnlyMemory<byte> content, CancellationToken ct)
        {
            Specs.Add(spec);
            Files[spec.Name] = content.ToArray();
            return Task.CompletedTask;
        }

        public async Task<bool> ReadOutFileAsync(OutFileSpec spec, Stream destination, CancellationToken ct)
        {
            Specs.Add(spec);
            if (Refuse?.Invoke(spec.Name) is { } refusal)
                throw new ChargehandException(ErrorCode.ContainerUnavailable, refusal);
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
        Assert.Equal(["chargehand.bundle", "driven-report.json", "handoff.json", "progress.md", "session-outcome.json"], engine.Specs.Select(s => s.Name).Order());     // the usage file is a poll, not an output
    }

    [Fact]
    public async Task A_stopped_sessions_handoff_files_come_out_with_the_rest()
    {
        var engine = new FakeOut();
        engine.Files["handoff.json"] = """{"version":1}"""u8.ToArray();
        engine.Files["progress.md"] = "# Handoff"u8.ToArray();
        using var dir = new TempDir();
        await new EngineSessionVolumes(engine, Image).FetchOutputAsync("run1", dir.Path, default);

        Assert.Equal("""{"version":1}""", File.ReadAllText(System.IO.Path.Combine(dir.Path, "handoff.json")));
        Assert.Equal("# Handoff", File.ReadAllText(System.IO.Path.Combine(dir.Path, "progress.md")));
    }

    [Fact]
    public async Task A_runner_that_refuses_the_handoff_names_costs_the_task_nothing_but_a_refused_bundle_still_fails()
    {
        var engine = new FakeOut { Refuse = name => name is "handoff.json" or "progress.md" ? $"the runner refused GET /out/run1/{name}?image=x: 403 the runner reads only chargehand.bundle, driven-report.json, session-outcome.json, session-usage.json" : null };
        engine.Files["chargehand.bundle"] = [1, 2];
        using var dir = new TempDir();
        await new EngineSessionVolumes(engine, Image).FetchOutputAsync("run1", dir.Path, default);       // handoff.json and progress.md are refused: skipped

        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(System.IO.Path.Combine(dir.Path, "chargehand.bundle")));
        Assert.False(File.Exists(System.IO.Path.Combine(dir.Path, "handoff.json")));
        Assert.False(File.Exists(System.IO.Path.Combine(dir.Path, "progress.md")));

        var refusesBundle = new FakeOut { Refuse = name => name == "chargehand.bundle" ? "the runner refused GET /out/run1/chargehand.bundle: 403 nope" : null };
        await Assert.ThrowsAsync<ChargehandException>(() => new EngineSessionVolumes(refusesBundle, Image).FetchOutputAsync("run1", dir.Path, default));
        var unreachable = new FakeOut { Refuse = name => name == "handoff.json" ? "the runner could not be reached: connection refused" : null };
        await Assert.ThrowsAsync<ChargehandException>(() => new EngineSessionVolumes(unreachable, Image).FetchOutputAsync("run1", dir.Path, default));
    }

    [Fact]
    public async Task The_running_tally_is_read_from_the_usage_file_and_a_bad_one_is_ignored()
    {
        var engine = new FakeOut();
        var volumes = new EngineSessionVolumes(engine, Image);
        Assert.Null(await volumes.ReadUsageAsync("run1", default));
        engine.Files["session-usage.json"] = """{"tokens": 4200}"""u8.ToArray();
        Assert.Equal(new TaskUsage(4200, 0), await volumes.ReadUsageAsync("run1", default));
        Assert.Equal("session-usage.json", engine.Specs.Last().Name);
        engine.Files["session-usage.json"] = """{"tokens": 4300, "turns": 7, "stage": "test"}"""u8.ToArray();
        Assert.Equal(new TaskUsage(4300, 0, 7, SessionStage.Test), await volumes.ReadUsageAsync("run1", default));
        engine.Files["session-usage.json"] = """{"tokens": 4350, "cache_read": 90000, "cache_write": 1200, "usd_estimate": 0.31, "usd_estimate_partial": true}"""u8.ToArray();
        Assert.Equal(new TaskUsage(4350, 0, CacheRead: 90000, CacheWrite: 1200, UsdEstimate: 0.31m, UsdEstimatePartial: true), await volumes.ReadUsageAsync("run1", default));
        engine.Files["session-usage.json"] = """{"tokens": 4400, "stage": "deploy"}"""u8.ToArray();
        Assert.Equal(new TaskUsage(4400, 0), await volumes.ReadUsageAsync("run1", default));     // a stage it does not know is left out, the tokens still count
        engine.Files["session-usage.json"] = """{"tokens": 4350, "turns": 8, "added_later": {"x": 1}}"""u8.ToArray();
        Assert.Equal(new TaskUsage(4350, 0, 8, null), await volumes.ReadUsageAsync("run1", default));      // a member a newer session image wrote does not hide the tally
        engine.Files["session-usage.json"] = """{"tokens": 42"""u8.ToArray();
        Assert.Null(await volumes.ReadUsageAsync("run1", default));
        engine.Files["session-usage.json"] = """{"tokens": -5}"""u8.ToArray();
        Assert.Null(await volumes.ReadUsageAsync("run1", default));
    }

    [Fact]
    public async Task A_stand_in_still_fails_a_task_before_any_container_starts()
    {
        var e = await Assert.ThrowsAsync<ChargehandException>(() => new UnavailableSessionVolumes().WriteTaskAsync("run1", new SessionTask("run1", "g", "chargehand/run1", 1, 1, 1), default));
        Assert.Equal(ErrorCode.ContainerUnavailable, e.Code);
    }
}
