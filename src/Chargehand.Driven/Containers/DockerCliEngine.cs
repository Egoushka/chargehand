using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Containers;

/// <summary>An <see cref="IContainerEngine"/> that calls the <c>docker</c> CLI (ADR 0039). Arguments go through
/// <see cref="ProcessStartInfo.ArgumentList"/>, never a shell string; every name is checked before it reaches docker. <paramref name="leadingArgs"/>
/// go before every call (a test runs a fake docker script as <c>sh script</c>, which avoids a busy-executable race on Linux).
/// While a container runs, each inspect also measures its memory (ADR 0045, admission): the cgroup's <c>memory.peak</c> when <paramref name="cgroupRoot"/> shows the container's cgroup, else the
/// largest <c>docker stats</c> reading, taken at most every <paramref name="statsEvery"/> (5 s). The largest value seen is kept until the container is removed, because its cgroup is gone once it exits.</summary>
public sealed class DockerCliEngine(string docker = "docker", IReadOnlyList<string>? leadingArgs = null, string cgroupRoot = "/sys/fs/cgroup", TimeSpan? statsEvery = null, TimeSpan? retryEvery = null)
    : IContainerEngine, IWorkspaceEngine, IBundleWorkspaceEngine, IOutVolumeEngine, IOutTailEngine, IServiceEngine
{
    private const int OutputCap = 64 * 1024;

    private readonly ConcurrentDictionary<string, Peak> _peaks = new(StringComparer.Ordinal);
    private readonly TimeSpan _statsEvery = statsEvery ?? TimeSpan.FromSeconds(5);
    private readonly TimeSpan _retryEvery = retryEvery ?? TimeSpan.FromMilliseconds(250);

    private sealed record Peak(int Mb, long SampledAt);

    /// <summary>The most a file read out of an output volume may be (a bundle of the session's branch).</summary>
    public const long OutFileCap = 256L * 1024 * 1024;

    public async Task<string> StartAsync(ContainerSpec spec, CancellationToken ct)
    {
        var result = await RunWithEnvFileAsync(ContainerTemplate.EnvFileLines(spec.Env), envFile => ContainerTemplate.RunArgs(spec, envFile), ct);
        if (result.ExitCode != 0)
            throw Unavailable("docker run failed", result, spec.Env);
        return result.Stdout.Trim();
    }

    public async Task<string> StartServiceAsync(ServiceSpec spec, CancellationToken ct)
    {
        var result = spec.Env.Count == 0
            ? await RunAsync(ContainerTemplate.ServiceArgs(spec, null), ct)
            : await RunWithEnvFileAsync(ContainerTemplate.ServiceEnvLines(spec.Env), envFile => ContainerTemplate.ServiceArgs(spec, envFile), ct);
        if (result.ExitCode != 0)
            throw Unavailable($"docker run of the service '{spec.Name}' failed", result, spec.Env);
        return result.Stdout.Trim();
    }

    /// <summary>Writes <paramref name="lines"/> to a private temporary env file, runs the docker call that names it, and deletes the file. The values never reach a command line.</summary>
    private async Task<DockerResult> RunWithEnvFileAsync(IReadOnlyList<string> lines, Func<string, IReadOnlyList<string>> args, CancellationToken ct)
    {
        var dir = Directory.CreateTempSubdirectory("chargehand-env-");
        try
        {
            // CreateTempSubdirectory makes the directory private (0700); the file is created 0600 for the same reason.
            var envFile = Path.Combine(dir.FullName, "env");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var writer = new StreamWriter(new FileStream(envFile, options)))
                foreach (var line in lines)
                    await writer.WriteLineAsync(line.AsMemory(), ct);
            return await RunAsync(args(envFile), ct);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    public async Task SignalAsync(string id, string signal, CancellationToken ct)
    {
        RequirePlain(id);
        if (!ContainerTemplate.IsSignal(signal))
            throw new ArgumentException("invalid signal", nameof(signal));
        var result = await RunAsync(["kill", $"--signal={signal}", id], ct);
        // A container that already ended or is gone has nothing to signal: a cancel that loses the race with the session's own exit is still a cancel.
        if (result.ExitCode != 0 && (result.Stderr.Contains("is not running", StringComparison.Ordinal) || result.Stderr.Contains("No such container", StringComparison.OrdinalIgnoreCase)))
            return;
        Require(result, "docker kill failed");
    }

    public async Task<ContainerState> InspectAsync(string id, CancellationToken ct)
    {
        RequirePlain(id);
        var result = await RunAsync(["inspect", "--format", "{{.State.Status}} {{.State.ExitCode}} {{.State.OOMKilled}} {{.Id}}", id], ct);
        if (result.ExitCode != 0)
            return result.Stderr.Contains("No such", StringComparison.OrdinalIgnoreCase)
                ? new ContainerState(ContainerStatus.Missing, null, false)
                : throw Unavailable("docker inspect failed", result, null);
        var parts = result.Stdout.Trim().Split(' ');
        if (parts.Length is not (3 or 4) || !int.TryParse(parts[1], out var exit))
            throw Unavailable("docker inspect answered something unexpected", result, null);
        var status = parts[0] switch
        {
            "running" or "restarting" or "paused" => ContainerStatus.Running,
            "created" => ContainerStatus.Created,
            _ => ContainerStatus.Exited,
        };
        int? peak = parts[0] == "running" ? await SamplePeakAsync(id, parts.Length == 4 ? parts[3] : null, ct) : _peaks.TryGetValue(id, out var seen) ? seen.Mb : null;
        return new ContainerState(status, status == ContainerStatus.Exited ? exit : null, parts[2] == "true", peak);
    }

    /// <summary>Measures a running container and keeps the largest value. A reading that fails is skipped: the memory figure must never fail an inspect.</summary>
    private async Task<int?> SamplePeakAsync(string key, string? fullId, CancellationToken ct)
    {
        _peaks.TryGetValue(key, out var seen);
        var now = Stopwatch.GetTimestamp();
        var sampled = fullId is null ? null : CgroupPeakMb(fullId);
        if (sampled is null && (seen is null || Stopwatch.GetElapsedTime(seen.SampledAt, now) >= _statsEvery))
            sampled = await StatsMbAsync(key, ct);
        if (sampled is not { } mb)
            return seen?.Mb;
        return _peaks.AddOrUpdate(key, new Peak(mb, now), (_, old) => new Peak(Math.Max(old.Mb, mb), now)).Mb;
    }

    private async Task<int?> StatsMbAsync(string key, CancellationToken ct)
    {
        try
        {
            var stats = await RunAsync(["stats", "--no-stream", "--format", "{{.MemUsage}}", key], ct);
            return stats.ExitCode == 0 ? ParseUsageMb(stats.Stdout) : null;
        }
        catch (ChargehandException)
        {
            return null;
        }
    }

    /// <summary>The container's <c>memory.peak</c> in MiB, rounded up, from its cgroup under the systemd driver's or the cgroupfs driver's path; null when the file is not there or not readable
    /// (an older kernel, a runner that does not see the host's cgroups).</summary>
    private int? CgroupPeakMb(string fullId)
    {
        foreach (var path in new[] { Path.Combine(cgroupRoot, "system.slice", $"docker-{fullId}.scope", "memory.peak"), Path.Combine(cgroupRoot, "docker", fullId, "memory.peak") })
        {
            try
            {
                if (long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
                    return (int)Math.Min(int.MaxValue, (bytes + (1 << 20) - 1) >> 20);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    /// <summary>The use half of <c>docker stats</c>' <c>285.1MiB / 3GiB</c>, in MiB rounded up; null when it is not a size.</summary>
    internal static int? ParseUsageMb(string text)
    {
        var use = text.Split('/')[0].Trim();
        var unit = use.Length;
        while (unit > 0 && char.IsLetter(use[unit - 1]))
            unit--;
        if (unit <= 0 || !double.TryParse(use[..unit], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;
        double? mib = use[unit..] switch
        {
            "B" => value / (1 << 20),
            "KiB" => value / 1024,
            "MiB" => value,
            "GiB" => value * 1024,
            "TiB" => value * 1024 * 1024,
            "kB" => value * 1000 / (1 << 20),
            "MB" => value * 1_000_000 / (1 << 20),
            "GB" => value * 1_000_000_000 / (1 << 20),
            _ => null,
        };
        return mib is { } v and >= 0 and < int.MaxValue ? (int)Math.Ceiling(v) : null;
    }

    public async Task RemoveAsync(string id, CancellationToken ct)
    {
        RequirePlain(id);
        _peaks.TryRemove(id, out _);
        await RunAsync(["rm", "-f", id], ct);
    }

    public async Task KillAllAsync(CancellationToken ct)
    {
        var listed = await RunAsync(["ps", "-aq", "--filter", $"label={ContainerTemplate.RunLabel}"], ct);
        Require(listed, "docker ps failed");
        var ids = listed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(ContainerTemplate.IsPlainName).ToList();
        _peaks.Clear();
        if (ids.Count > 0)
            await RunAsync(["rm", "-f", .. ids], ct);
        var networks = await RunAsync(["network", "ls", "-q", "--filter", $"label={ContainerTemplate.RunLabel}"], ct);
        foreach (var network in networks.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(ContainerTemplate.IsPlainName))
            await RunAsync(["network", "rm", network], ct);
    }

    public async Task CreateNetworkAsync(string name, string batchId, CancellationToken ct)
    {
        RequirePlain(name);
        RequirePlain(batchId);
        Require(await RunAsync(["network", "create", "--internal", "--label", $"{ContainerTemplate.RunLabel}={batchId}", name], ct), "docker network create failed");
    }

    public async Task RemoveNetworkAsync(string name, CancellationToken ct)
    {
        RequirePlain(name);
        await RunAsync(["network", "rm", name], ct);
    }

    public async Task ConnectNetworkAsync(string container, string network, CancellationToken ct)
    {
        RequirePlain(container);
        RequirePlain(network);
        Require(await RunAsync(["network", "connect", network, container], ct), "docker network connect failed");
    }

    public async Task<bool> OwnsAsync(string id, CancellationToken ct)
    {
        RequirePlain(id);
        var result = await RunAsync(["inspect", "--format", $"{{{{index .Config.Labels \"{ContainerTemplate.RunLabel}\"}}}}", id], ct);
        return result.ExitCode == 0 && result.Stdout.Trim() is { Length: > 0 } label && label != "<no value>";
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        var listed = await RunAsync(["ps", "-aq", "--filter", $"label={ContainerTemplate.RunLabel}"], ct);
        Require(listed, "docker ps failed");
        return listed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }

    public async Task<EngineResources> ListAsync(CancellationToken ct)
    {
        async Task<IReadOnlyList<string>> Names(params string[] args)
        {
            var listed = await RunAsync(args, ct);
            Require(listed, $"docker {args[0]} failed");
            return [.. listed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(ContainerTemplate.IsPlainName)];
        }
        var label = $"label={ContainerTemplate.RunLabel}";
        return new EngineResources(await Names("ps", "-a", "--filter", label, "--format", "{{.Names}}"),
            await Names("network", "ls", "--filter", label, "--format", "{{.Name}}"),
            await Names("volume", "ls", "--filter", label, "--format", "{{.Name}}"));
    }

    public async Task PrepareWorkspaceAsync(WorkspaceSpec spec, CancellationToken ct)
    {
        var args = ContainerTemplate.WorkspaceArgs(spec);
        await CreateVolumeAsync(spec.WorkVolume, spec.RunId, ct);
        var result = await RunHelperAsync(args, default, null, Stream.Null, ct);
        if (result.ExitCode != 0)
        {
            await RemoveVolumeAsync(spec.WorkVolume, CancellationToken.None);
            throw Unavailable("preparing the workspace failed", result, null);
        }
    }

    /// <summary>How many session containers run now (ADR 0058, keep-awake): labelled containers that are running, less each batch's egress proxy and test services and the transfer, workspace and probe helpers.</summary>
    public async Task<int> RunningSessionsAsync(CancellationToken ct)
    {
        var listed = await RunAsync(["ps", "--filter", $"label={ContainerTemplate.RunLabel}", "--filter", "status=running", "--format", "{{.Names}}"], ct);
        Require(listed, "docker ps failed");
        string[] notSessions = ["chargehand-egress-", "chargehand-svc-", "chargehand-prep-", "chargehand-xfer-"];
        return listed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(name => name.StartsWith("chargehand-", StringComparison.Ordinal) && !notSessions.Any(p => name.StartsWith(p, StringComparison.Ordinal)));
    }

    /// <summary>The engine's own memory, load and processors, from a throwaway container (<see cref="ContainerTemplate.HostProbeArgs"/>); the text it printed.</summary>
    public async Task<string> ProbeHostAsync(string image, CancellationToken ct)
    {
        var result = await RunAsync(ContainerTemplate.HostProbeArgs(image), ct);
        if (result.ExitCode != 0)
            throw Unavailable("probing the engine's host failed", result, null);
        return result.Stdout;
    }

    public async Task PrepareBundleWorkspaceAsync(BundleWorkspaceSpec spec, CancellationToken ct)
    {
        var args = ContainerTemplate.BundleWorkspaceArgs(spec);
        await CreateVolumeAsync(spec.WorkVolume, spec.RunId, ct);
        var result = await RunHelperAsync(args, default, null, Stream.Null, ct);
        if (result.ExitCode != 0)
        {
            await RemoveVolumeAsync(spec.WorkVolume, CancellationToken.None);
            throw Unavailable("preparing the workspace from the bundle failed", result, null);
        }
    }

    public async Task WriteOutFileAsync(OutFileSpec spec, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var result = await RunHelperAsync(ContainerTemplate.OutFileArgs(spec, write: true), content, null, Stream.Null, ct);
        if (result.ExitCode != 0)
            throw Unavailable($"writing {spec.Name} into the output volume failed", result, null);
    }

    /// <summary>Streams <paramref name="content"/> to the helper's stdin: the repository's bundle is never held whole in memory.</summary>
    public async Task WriteOutStreamAsync(OutFileSpec spec, Stream content, long length, CancellationToken ct)
    {
        var result = await RunHelperAsync(ContainerTemplate.OutFileArgs(spec, write: true), default, content, Stream.Null, ct);
        if (result.ExitCode != 0)
            throw Unavailable($"writing {spec.Name} into the output volume failed", result, null);
    }

    public async Task<bool> ReadOutFileAsync(OutFileSpec spec, Stream destination, CancellationToken ct)
    {
        var result = await RunHelperAsync(ContainerTemplate.OutFileArgs(spec, write: false), default, null, destination, ct);
        if (result.ExitCode == ContainerTemplate.OutFileMissing)
            return false;
        if (result.ExitCode != 0)
            throw Unavailable($"reading {spec.Name} from the output volume failed", result, null);
        return true;
    }

    public async Task<bool> ReadOutTailAsync(OutFileSpec spec, int maxBytes, Stream destination, CancellationToken ct)
    {
        var result = await RunHelperAsync(ContainerTemplate.OutTailArgs(spec, maxBytes), default, null, destination, ct);
        if (result.ExitCode == ContainerTemplate.OutFileMissing)
            return false;
        if (result.ExitCode != 0)
            throw Unavailable($"reading the end of {spec.Name} from the output volume failed", result, null);
        return true;
    }

    /// <summary>The most times one helper is run for one call.</summary>
    private const int HelperAttempts = 4;

    /// <summary>Docker's own failure code, before the helper ran: the engine would not create or start it.</summary>
    private const int DockerFailed = 125;

    /// <summary>Runs a fixed-name helper (a transfer or a workspace helper, named <c>chargehand-xfer-&lt;run&gt;</c> or <c>chargehand-prep-&lt;run&gt;</c>), and asks again when the engine itself failed
    /// (<see cref="DockerFailed"/>): the engine blinked, or a copy under the helper's name is left over from a call that was killed between <c>create</c> and <c>start</c> (<c>--rm</c> removes only a
    /// container that ran). A leftover is removed first; docker's <c>rm</c> without <c>-f</c> refuses one that is running, which is another call for the same run and is waited for. A call cancelled in flight
    /// removes the container it may have made. Nothing is repeated once bytes were copied out. A stream is read once, so it is never asked again: what holds the name is removed before it runs instead.</summary>
    private async Task<DockerResult> RunHelperAsync(IReadOnlyList<string> args, ReadOnlyMemory<byte> stdin, Stream? stdinStream, Stream stdout, CancellationToken ct)
    {
        var name = NameOf(args);
        if (stdinStream is not null)
            await RemoveLeftoverAsync(name, ct);
        var from = stdout.CanSeek ? stdout.Position : 0;
        for (var attempt = 1; ; attempt++)
        {
            DockerResult result;
            try
            {
                result = await RunStreamAsync(args, stdin, stdinStream, stdout, ct);
            }
            catch (OperationCanceledException)
            {
                await TryRunAsync(["rm", "-f", name]);
                throw;
            }
            if (result.ExitCode != DockerFailed || stdinStream is not null || attempt == HelperAttempts || stdout.CanSeek && stdout.Position != from)
                return result;
            if (!(result.Stderr.Contains("is already in use", StringComparison.Ordinal) && await RemoveLeftoverAsync(name, ct)))
                await Task.Delay(_retryEvery * attempt, ct);
        }
    }

    /// <summary>Removes the container <paramref name="name"/> when it is chargehand's and not running.</summary>
    private async Task<bool> RemoveLeftoverAsync(string name, CancellationToken ct) =>
        await OwnsAsync(name, ct) && (await RunAsync(["rm", name], ct)).ExitCode == 0;

    /// <summary>Removes the transfer and workspace helpers that exist but are not running, which a runner does at start: a call killed with the runner leaves its helper created and never started, and nothing
    /// else would remove it. A helper that is running is a call still in progress and stays.</summary>
    /// <returns>The names removed.</returns>
    public async Task<IReadOnlyList<string>> RemoveStaleHelpersAsync(CancellationToken ct)
    {
        var listed = await RunAsync(["ps", "-a", "--filter", $"label={ContainerTemplate.RunLabel}", "--filter", "status=created", "--filter", "status=exited", "--filter", "status=dead", "--format", "{{.Names}}"], ct);
        Require(listed, "docker ps failed");
        List<string> removed = [];
        foreach (var name in listed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(n => (n.StartsWith(ContainerTemplate.TransferPrefix, StringComparison.Ordinal) || n.StartsWith(ContainerTemplate.PrepPrefix, StringComparison.Ordinal)) && ContainerTemplate.IsPlainName(n)))
            if ((await RunAsync(["rm", name], ct)).ExitCode == 0)
                removed.Add(name);
        return removed;
    }

    /// <summary>A cleanup that runs after a cancel: whatever it cannot do is left for the next start.</summary>
    private async Task TryRunAsync(IEnumerable<string> args)
    {
        try
        {
            await RunAsync(args, CancellationToken.None);
        }
        catch (ChargehandException)
        {
        }
    }

    public async Task<string> StartEgressAsync(EgressSpec spec, CancellationToken ct)
    {
        var result = spec.Gateway is { } gateway
            ? await RunWithEnvFileAsync(ContainerTemplate.EgressEnvLines(gateway), envFile => ContainerTemplate.EgressArgs(spec, envFile), ct)
            : await RunAsync(ContainerTemplate.EgressArgs(spec), ct);
        if (result.ExitCode != 0)
            throw Unavailable("docker run of the egress proxy failed", result, spec.Gateway is { } g ? new Dictionary<string, string> { ["credential"] = g.Credential, ["key"] = g.TokenKey } : null);
        return result.Stdout.Trim();
    }

    public async Task<string> LogsTailAsync(string id, int bytes, CancellationToken ct)
    {
        RequirePlain(id);
        var result = await RunAsync(["logs", "--tail", "200", id], ct);
        var text = result.Stdout + result.Stderr;
        return text.Length <= bytes ? text : text[^bytes..];
    }

    public async Task CreateVolumeAsync(string name, string runId, CancellationToken ct)
    {
        RequirePlain(name);
        RequirePlain(runId);
        Require(await RunAsync(["volume", "create", "--label", $"{ContainerTemplate.RunLabel}={runId}", name], ct), "docker volume create failed");
    }

    public async Task RemoveVolumeAsync(string name, CancellationToken ct)
    {
        RequirePlain(name);
        await RunAsync(["volume", "rm", "-f", name], ct);
    }

    /// <summary>The <c>--name</c> of a helper's command line.</summary>
    private static string NameOf(IReadOnlyList<string> args) => args[args.ToList().IndexOf("--name") + 1];

    private static void RequirePlain(string name)
    {
        if (!ContainerTemplate.IsPlainName(name))
            throw new ArgumentException("not a plain container, volume or run name", nameof(name));
    }

    private static void Require(DockerResult result, string what)
    {
        if (result.ExitCode != 0)
            throw Unavailable(what, result, null);
    }

    private static ChargehandException Unavailable(string what, DockerResult result, IReadOnlyDictionary<string, string>? secrets)
    {
        var detail = result.Stderr.Trim();
        foreach (var value in secrets?.Values ?? [])
            if (value.Length > 0)
                detail = detail.Replace(value, "[redacted]", StringComparison.Ordinal);
        detail = detail.Length > 400 ? detail[..400] : detail;
        return new ChargehandException(ErrorCode.ContainerUnavailable, $"{what} (exit {result.ExitCode}): {detail}",
            "Check that the Docker engine is running and reachable (`docker info`), then retry.");
    }

    private async Task<DockerResult> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(docker) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in leadingArgs ?? [])
            psi.ArgumentList.Add(a);
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("could not start docker");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ChargehandException(ErrorCode.ContainerUnavailable, $"could not start '{docker}': {e.Message}",
                "Install Docker, or set driven.runner in the profile to use a runner service.");
        }
        using (process)
        {
            process.StandardInput.Close();
            var stdout = Read(process.StandardOutput);
            var stderr = Read(process.StandardError);
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            return new DockerResult(process.ExitCode, await stdout, await stderr);
        }
    }

    /// <summary>Like <see cref="RunAsync"/> but with bytes in and bytes out: <paramref name="stdin"/> (or, when set, everything <paramref name="stdinStream"/> yields) goes to the process, its stdout is copied
    /// to <paramref name="stdout"/> up to <see cref="OutFileCap"/> (more kills the process).</summary>
    private async Task<DockerResult> RunStreamAsync(IEnumerable<string> args, ReadOnlyMemory<byte> stdin, Stream? stdinStream, Stream stdout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(docker) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in leadingArgs ?? [])
            psi.ArgumentList.Add(a);
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("could not start docker");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ChargehandException(ErrorCode.ContainerUnavailable, $"could not start '{docker}': {e.Message}",
                "Install Docker, or set driven.runner in the profile to use a runner service.");
        }
        using (process)
        {
            var stderr = Read(process.StandardError);
            var copied = CopyCappedAsync(process.StandardOutput.BaseStream, stdout, process, ct);
            try
            {
                try
                {
                    if (stdinStream is not null)
                        await stdinStream.CopyToAsync(process.StandardInput.BaseStream, ct);
                    else
                        await process.StandardInput.BaseStream.WriteAsync(stdin, ct);
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // The helper ended before reading its input; its exit code says why.
                }
                await copied;
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            return new DockerResult(process.ExitCode, "", await stderr);
        }
    }

    private static async Task CopyCappedAsync(Stream from, Stream to, Process process, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = await from.ReadAsync(buffer, ct)) > 0)
        {
            total += n;
            if (total > OutFileCap)
            {
                process.Kill(entireProcessTree: true);
                throw new ChargehandException(ErrorCode.ContainerUnavailable, $"a file in the output volume is larger than {OutFileCap / (1024 * 1024)} MiB",
                    "The session wrote more than chargehand will copy out; nothing from it is used.");
            }
            await to.WriteAsync(buffer.AsMemory(0, n), ct);
        }
    }

    private static async Task<string> Read(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buffer)) > 0)
            if (text.Length < OutputCap)
                text.Append(buffer, 0, n);
        return text.ToString();
    }

    private sealed record DockerResult(int ExitCode, string Stdout, string Stderr);
}
