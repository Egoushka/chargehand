using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Modules.Runners.Tests;

/// <summary>The measurement of ADR 0078, step 3, and only that: with <c>CHARGEHAND_TEST_MISE_IMAGE</c> (a locally built <c>images/session-mise</c> image id) and <c>CHARGEHAND_SPIKE_DIR</c> (a directory with
/// <c>spike.json</c> and the checkouts it names, each with its pins committed) it runs, per repository, the install helper and the repository's own test command through <see cref="ContainerTemplate"/> and
/// <see cref="DockerCliEngine"/>, cold (a new home volume) and warm (the same volume, a new work volume), and writes a Markdown table to <c>CHARGEHAND_SPIKE_OUT</c>. No model runs. Without the
/// variables it is skipped.</summary>
public class MiseSpikeTests
{
    private static string? Image => Environment.GetEnvironmentVariable("CHARGEHAND_TEST_MISE_IMAGE");

    private sealed class SpikeFactAttribute : FactAttribute
    {
        public SpikeFactAttribute() => Skip = string.IsNullOrEmpty(Image) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CHARGEHAND_SPIKE_DIR"))
            ? "set CHARGEHAND_TEST_MISE_IMAGE and CHARGEHAND_SPIKE_DIR (see MiseSpikeTests)"
            : null;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Entry(string Name, string Directory, string Commit, string[] Test);

    private sealed record Row(string Name, string Pass, double InstallSeconds, double TestSeconds, int ExitCode, string VolumeMb);

    [SpikeFact]
    public async Task Every_repository_installs_and_runs_its_own_tests_cold_and_warm_as_the_session_user()
    {
        var dir = Environment.GetEnvironmentVariable("CHARGEHAND_SPIKE_DIR")!;
        var proxy = Environment.GetEnvironmentVariable("CHARGEHAND_SPIKE_PROXY");
        var entries = JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(Path.Combine(dir, "spike.json")), Json)!;
        var engine = new DockerCliEngine();
        var rows = new List<Row>();
        foreach (var entry in entries)
        {
            var home = HomeVolumes.Name($"spike/{entry.Name}");
            await engine.RemoveVolumeAsync(home, default);
            foreach (var pass in new[] { "cold", "warm" })
            {
                var run = $"{entry.Name}{(pass == "cold" ? "c" : "w")}";
                await engine.RemoveVolumeAsync(RunnerNames.Work(run), default);
                await engine.CreateVolumeAsync(RunnerNames.Out(run), run, default);
                await engine.PrepareWorkspaceAsync(new WorkspaceSpec(run, Image!, Path.Combine(dir, entry.Directory), RunnerNames.Work(run), "chargehand/spike", entry.Commit), default);
                Dictionary<string, string> env = proxy is null ? [] : new() { ["HTTPS_PROXY"] = proxy, ["HTTP_PROXY"] = proxy };

                var install = await RunAsync(engine, new ContainerSpec($"{run}-install", Image!, RunnerNames.Work(run), RunnerNames.Out(run), "bridge", env, 4096, 4, 512,
                    ContainerTemplate.InstallCommand, Home: home, Install: true));
                var test = await RunAsync(engine, new ContainerSpec(run, Image!, RunnerNames.Work(run), RunnerNames.Out(run), "bridge", env, 4096, 4, 512,
                    ["sh", "-c", string.Join(' ', entry.Test)], Home: home));
                rows.Add(new Row(entry.Name, pass, install.Seconds, test.Seconds, install.ExitCode != 0 ? install.ExitCode : test.ExitCode, await VolumeMbAsync(home)));
                File.WriteAllText(Path.Combine(dir, $"{run}.log"), $"== install ({install.ExitCode})\n{install.Log}\n== test ({test.ExitCode})\n{test.Log}\n");
                await engine.RemoveVolumeAsync(RunnerNames.Work(run), default);
                await engine.RemoveVolumeAsync(RunnerNames.Out(run), default);
            }
        }
        var table = new StringBuilder("| repository | pass | install s | test s | exit | home volume MB |\n|---|---|---:|---:|---:|---:|\n");
        foreach (var r in rows)
            table.AppendLine(CultureInfo.InvariantCulture, $"| {r.Name} | {r.Pass} | {r.InstallSeconds:0} | {r.TestSeconds:0} | {r.ExitCode} | {r.VolumeMb} |");
        File.WriteAllText(Environment.GetEnvironmentVariable("CHARGEHAND_SPIKE_OUT") ?? Path.Combine(dir, "table.md"), table.ToString());
    }

    private static async Task<(double Seconds, int ExitCode, string Log)> RunAsync(DockerCliEngine engine, ContainerSpec spec)
    {
        var id = await engine.StartAsync(spec, default);
        var clock = Stopwatch.StartNew();
        ContainerState state;
        while ((state = await engine.InspectAsync(id, default)).Status == ContainerStatus.Running)
            await Task.Delay(500);
        var seconds = clock.Elapsed.TotalSeconds;
        var log = await engine.LogsTailAsync(id, 64 * 1024, default);
        await engine.RemoveAsync(id, default);
        return (seconds, state.ExitCode ?? -1, log);
    }

    private static async Task<string> VolumeMbAsync(string volume)
    {
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true };
        foreach (var a in new[] { "run", "--rm", "-v", $"{volume}:/v:ro", "--entrypoint", "du", Image!, "-sm", "/v" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return output.Split('\t')[0].Trim();
    }
}
