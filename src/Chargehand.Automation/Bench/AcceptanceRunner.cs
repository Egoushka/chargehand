using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Chargehand.Driven;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Bench;

/// <param name="SourceDirectory">A git directory that holds <paramref name="Branch"/> and the base commit.</param>
/// <param name="Image">The session image for the repository's ecosystem, by digest.</param>
/// <param name="Allow">The egress allowlist of the container's network: the hosts a restore may reach.</param>
public sealed record AcceptanceRequest(string Label, string SourceDirectory, string Branch, string BaseCommit, IReadOnlyList<string> Argv, TimeSpan Timeout, string Image, IReadOnlyList<string> Allow);

/// <summary>Runs one command on a branch in a fresh container and says whether it exited 0.</summary>
public interface IAcceptanceRunner
{
    Task<AcceptanceResult> RunAsync(AcceptanceRequest request, CancellationToken ct);
}

/// <summary>The container limits of the driven preset, so a check gets what a session's own test run got.</summary>
public sealed record AcceptanceLimits(int MemoryMb, double Cpus, int Pids);

/// <summary>An acceptance check in a container that is made the way a driven task's verification is (ADR 0039): a new workspace cloned at the base commit, the branch put on it from a
/// bundle, the command run by <c>chargehand verify-branch</c> on a network whose only way out is the allowlist proxy, with no credential in the environment. Its one stdout record is the
/// verdict. The bundle goes in through the seed file of a fresh output volume, which the verifier's own path (a session's bundle) does not use.</summary>
public sealed class ContainerAcceptanceRunner(IContainerEngine engine, IWorkspaceEngine workspace, IOutVolumeEngine files, AcceptanceLimits limits, TimeSpan? poll = null, TimeSpan? slack = null) : IAcceptanceRunner
{
    private const int LogBytes = 64 * 1024;

    public async Task<AcceptanceResult> RunAsync(AcceptanceRequest request, CancellationToken ct)
    {
        var bundle = await BundleAsync(request, ct);
        var id = $"bench-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(5))}";
        var work = RunnerNames.Work(id);
        var output = RunnerNames.Out(id);
        BatchNetworkInfo? network = null;
        var batchNetwork = new BatchNetwork(engine);
        string? container = null;
        try
        {
            network = await batchNetwork.CreateAsync(id, request.Image, request.Allow, ct);
            await engine.CreateVolumeAsync(output, id, ct);
            await files.WriteOutFileAsync(new OutFileSpec(id, request.Image, output, ContainerTemplate.SeedFile), bundle, ct);
            await workspace.PrepareWorkspaceAsync(new WorkspaceSpec(id, request.Image, request.SourceDirectory, work, request.Branch, request.BaseCommit), ct);
            var env = new Dictionary<string, string> { ["HTTPS_PROXY"] = network.ProxyUrl(id), ["HTTP_PROXY"] = network.ProxyUrl(id) };
            var seconds = ((int)Math.Ceiling(request.Timeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            var spec = new ContainerSpec(id, request.Image, work, output, network.Network, env, limits.MemoryMb, limits.Cpus, limits.Pids,
                ["verify-branch", "--bundle", $"/out/{ContainerTemplate.SeedFile}", "--branch", request.Branch, "--timeout-seconds", seconds, "--", .. request.Argv]);
            container = await engine.StartAsync(spec, ct);
            var clock = Stopwatch.StartNew();
            ContainerState state;
            while (true)
            {
                state = await engine.InspectAsync(container, ct);
                if (state.Status != ContainerStatus.Running)
                    break;
                if (clock.Elapsed > request.Timeout + (slack ?? TimeSpan.FromSeconds(90)))
                {
                    await engine.SignalAsync(container, "SIGKILL", CancellationToken.None);
                    return new AcceptanceResult(false, -1, true, clock.Elapsed.TotalSeconds, "the acceptance container did not finish in time and was killed");
                }
                await Task.Delay(poll ?? TimeSpan.FromSeconds(1), ct);
            }
            return Parse(await engine.LogsTailAsync(container, LogBytes, ct), state, clock.Elapsed);
        }
        finally
        {
            var done = CancellationToken.None;
            if (container is not null)
                await engine.RemoveAsync(container, done);
            await engine.RemoveVolumeAsync(work, done);
            await engine.RemoveVolumeAsync(output, done);
            if (network is not null)
                await batchNetwork.RemoveAsync(network, done);
        }
    }

    /// <summary>The branch as a bundle against the base commit, the shape the verifier fetches from.</summary>
    private static async Task<byte[]> BundleAsync(AcceptanceRequest request, CancellationToken ct)
    {
        var file = Path.Combine(Path.GetTempPath(), $"chargehand-bench-{Guid.NewGuid():N}.bundle");
        try
        {
            var made = await GitBenchWorkspace.Git(request.SourceDirectory, ["bundle", "create", "-q", file, $"refs/heads/{request.Branch}", $"^{request.BaseCommit}"], ct);
            return made.ExitCode == 0 ? await File.ReadAllBytesAsync(file, ct) : throw new BenchException($"could not bundle {request.Branch}: {made.Output.Trim()}");
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static AcceptanceResult Parse(string logs, ContainerState state, TimeSpan elapsed)
    {
        var line = logs.Split('\n').LastOrDefault(l => l.StartsWith(VerifyBranchCli.Marker, StringComparison.Ordinal));
        if (line is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(line[VerifyBranchCli.Marker.Length..]);
                var r = doc.RootElement;
                var exit = r.GetProperty("exit_code").GetInt32();
                var timedOut = r.GetProperty("timed_out").GetBoolean();
                return new AcceptanceResult(exit == 0 && !timedOut, exit, timedOut, r.GetProperty("duration_ms").GetInt64() / 1000.0, Tail(r.GetProperty("output_tail").GetString() ?? ""));
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // Falls through: a record that cannot be read is a failed check.
            }
        }
        var note = state.OomKilled ? "the acceptance container ran out of memory. " : "the acceptance container ended without a record. ";
        return new AcceptanceResult(false, state.ExitCode is { } code and not 0 ? code : -1, false, elapsed.TotalSeconds, Tail(note + logs));
    }

    private static string Tail(string text) => text.Length > 4000 ? text[^4000..] : text;
}
