using System.Diagnostics;
using System.Text.Json;
using Chargehand.Containers;

namespace Chargehand.Driven;

/// <param name="Image">The session image (it has the toolchains and git).</param>
/// <param name="SourcePath">chargehand's checkout of the pinned commit, mounted read-only into the workspace helper.</param>
/// <param name="ProxyUrl">The batch's egress proxy, so a restore can reach the allowlisted registries; the container has no other route out.</param>
public sealed record ContainerVerifierSettings(string Image, string SourcePath, string BaseCommit, string Network, string ProxyUrl, int MemoryMb, double Cpus, int Pids);

/// <summary>Runs the repository's tests on a session's branch in a fresh container (ADR 0039). The session's own container and workspace are removed first; a new workspace is
/// cloned at the base commit; only the bundle (in the session's output volume) is carried over, and the branch is checked out from it. The container gets the batch network's proxy
/// and nothing else in its environment: no model credential and no run token. Its one stdout record is read after it ends, so nothing the tests print can be mistaken for a verdict.</summary>
public sealed class ContainerVerifier(IContainerEngine engine, IWorkspaceEngine workspace, ContainerVerifierSettings settings, TimeSpan? poll = null, TimeSpan? slack = null) : IBranchVerifier
{
    private const int LogBytes = 64 * 1024;
    private static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultSlack = TimeSpan.FromSeconds(90);

    public async Task<VerificationRecord> VerifyAsync(BranchVerification verification, CancellationToken ct)
    {
        var run = verification.RunId;
        var pollEvery = poll ?? DefaultPoll;
        await engine.RemoveAsync($"chargehand-{run}", ct);
        await engine.RemoveVolumeAsync(RunnerNames.Work(run), ct);
        await workspace.PrepareWorkspaceAsync(new WorkspaceSpec(run, settings.Image, settings.SourcePath, RunnerNames.Work(run), verification.Branch, settings.BaseCommit), ct);
        var spec = new ContainerSpec(run, settings.Image, RunnerNames.Work(run), RunnerNames.Out(run), settings.Network,
            new Dictionary<string, string> { ["HTTPS_PROXY"] = settings.ProxyUrl, ["HTTP_PROXY"] = settings.ProxyUrl },
            settings.MemoryMb, settings.Cpus, settings.Pids,
            ["verify-branch", "--bundle", "/out/chargehand.bundle", "--branch", verification.Branch, "--timeout-seconds", ((int)Math.Ceiling(verification.Timeout.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture), "--", .. verification.Argv]);
        var id = await engine.StartAsync(spec, ct);
        var started = Stopwatch.StartNew();
        try
        {
            var deadline = verification.Timeout + (slack ?? DefaultSlack);
            ContainerState state;
            while (true)
            {
                state = await engine.InspectAsync(id, ct);
                if (state.Status != ContainerStatus.Running)
                    break;
                if (started.Elapsed > deadline)
                {
                    await engine.SignalAsync(id, "SIGKILL", CancellationToken.None);
                    return new VerificationRecord(verification.Argv, "detected", -1, "the verification container did not finish in time and was killed", true, started.Elapsed, true);
                }
                await Task.Delay(pollEvery, ct);
            }
            var logs = await engine.LogsTailAsync(id, LogBytes, ct);
            return Parse(logs, verification, state, started.Elapsed);
        }
        finally
        {
            await engine.RemoveAsync(id, CancellationToken.None);
        }
    }

    /// <summary>The last <see cref="VerifyBranchCli.Marker"/> line of the logs; a container that ended without one failed, with its logs (and a note if it ran out of memory) as the reason.</summary>
    private static VerificationRecord Parse(string logs, BranchVerification v, ContainerState state, TimeSpan elapsed)
    {
        var line = logs.Split('\n').LastOrDefault(l => l.StartsWith(VerifyBranchCli.Marker, StringComparison.Ordinal));
        if (line is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(line[VerifyBranchCli.Marker.Length..]);
                var r = doc.RootElement;
                return new VerificationRecord([.. r.GetProperty("argv").EnumerateArray().Select(a => a.GetString()!)], "detected", r.GetProperty("exit_code").GetInt32(),
                    r.GetProperty("output_tail").GetString() ?? "", r.GetProperty("timed_out").GetBoolean(), TimeSpan.FromMilliseconds(r.GetProperty("duration_ms").GetInt64()), true);
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // Falls through to the failure below.
            }
        }
        var note = state.OomKilled ? "the verification container ran out of memory. " : "the verification container ended without a record. ";
        var tail = logs.Length > 4000 ? logs[^4000..] : logs;
        return new VerificationRecord(v.Argv, "detected", state.ExitCode is { } code and not 0 ? code : -1, note + tail, false, elapsed, true);
    }
}

/// <summary>The volume names a run's containers use (the runner insists on them).</summary>
public static class RunnerNames
{
    public static string Work(string runId) => $"chargehand-work-{runId}";

    public static string Out(string runId) => $"chargehand-out-{runId}";
}
