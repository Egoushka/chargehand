using System.Diagnostics;
using System.Text;
using Chargehand.Contracts;

namespace Chargehand.Containers;

/// <summary>An <see cref="IContainerEngine"/> that calls the <c>docker</c> CLI (ADR 0039). Arguments go through
/// <see cref="ProcessStartInfo.ArgumentList"/>, never a shell string; every name is checked before it reaches docker. <paramref name="leadingArgs"/>
/// go before every call (a test runs a fake docker script as <c>sh script</c>, which avoids a busy-executable race on Linux).</summary>
public sealed class DockerCliEngine(string docker = "docker", IReadOnlyList<string>? leadingArgs = null) : IContainerEngine, IWorkspaceEngine, IOutVolumeEngine
{
    private const int OutputCap = 64 * 1024;

    /// <summary>The most a file read out of an output volume may be (a bundle of the session's branch).</summary>
    public const long OutFileCap = 256L * 1024 * 1024;

    public async Task<string> StartAsync(ContainerSpec spec, CancellationToken ct)
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
                foreach (var line in ContainerTemplate.EnvFileLines(spec.Env))
                    await writer.WriteLineAsync(line.AsMemory(), ct);
            var result = await RunAsync(ContainerTemplate.RunArgs(spec, envFile), ct);
            if (result.ExitCode != 0)
                throw Unavailable("docker run failed", result, spec.Env);
            return result.Stdout.Trim();
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
        Require(await RunAsync(["kill", $"--signal={signal}", id], ct), "docker kill failed");
    }

    public async Task<ContainerState> InspectAsync(string id, CancellationToken ct)
    {
        RequirePlain(id);
        var result = await RunAsync(["inspect", "--format", "{{.State.Status}} {{.State.ExitCode}} {{.State.OOMKilled}}", id], ct);
        if (result.ExitCode != 0)
            return result.Stderr.Contains("No such", StringComparison.OrdinalIgnoreCase)
                ? new ContainerState(ContainerStatus.Missing, null, false)
                : throw Unavailable("docker inspect failed", result, null);
        var parts = result.Stdout.Trim().Split(' ');
        if (parts.Length != 3 || !int.TryParse(parts[1], out var exit))
            throw Unavailable("docker inspect answered something unexpected", result, null);
        var running = parts[0] is "running" or "created" or "restarting" or "paused";
        return new ContainerState(running ? ContainerStatus.Running : ContainerStatus.Exited, running ? null : exit, parts[2] == "true");
    }

    public async Task RemoveAsync(string id, CancellationToken ct)
    {
        RequirePlain(id);
        await RunAsync(["rm", "-f", id], ct);
    }

    public async Task KillAllAsync(CancellationToken ct)
    {
        var listed = await RunAsync(["ps", "-aq", "--filter", $"label={ContainerTemplate.RunLabel}"], ct);
        Require(listed, "docker ps failed");
        var ids = listed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(ContainerTemplate.IsPlainName).ToList();
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

    public async Task PrepareWorkspaceAsync(WorkspaceSpec spec, CancellationToken ct)
    {
        var args = ContainerTemplate.WorkspaceArgs(spec);
        await CreateVolumeAsync(spec.WorkVolume, spec.RunId, ct);
        var result = await RunAsync(args, ct);
        if (result.ExitCode != 0)
        {
            await RemoveVolumeAsync(spec.WorkVolume, CancellationToken.None);
            throw Unavailable("preparing the workspace failed", result, null);
        }
    }

    public async Task WriteOutFileAsync(OutFileSpec spec, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var result = await RunStreamAsync(ContainerTemplate.OutFileArgs(spec, write: true), content, Stream.Null, ct);
        if (result.ExitCode != 0)
            throw Unavailable($"writing {spec.Name} into the output volume failed", result, null);
    }

    public async Task<bool> ReadOutFileAsync(OutFileSpec spec, Stream destination, CancellationToken ct)
    {
        var result = await RunStreamAsync(ContainerTemplate.OutFileArgs(spec, write: false), default, destination, ct);
        if (result.ExitCode == ContainerTemplate.OutFileMissing)
            return false;
        if (result.ExitCode != 0)
            throw Unavailable($"reading {spec.Name} from the output volume failed", result, null);
        return true;
    }

    public async Task<string> StartEgressAsync(EgressSpec spec, CancellationToken ct)
    {
        var result = await RunAsync(ContainerTemplate.EgressArgs(spec), ct);
        if (result.ExitCode != 0)
            throw Unavailable("docker run of the egress proxy failed", result, null);
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

    /// <summary>Like <see cref="RunAsync"/> but with bytes in and bytes out: <paramref name="stdin"/> goes to the process, its stdout is copied to <paramref name="stdout"/> up to
    /// <see cref="OutFileCap"/> (more kills the process).</summary>
    private async Task<DockerResult> RunStreamAsync(IEnumerable<string> args, ReadOnlyMemory<byte> stdin, Stream stdout, CancellationToken ct)
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
                    await process.StandardInput.BaseStream.WriteAsync(stdin, ct);
                }
                catch (IOException)
                {
                    // The helper ended before reading its input; its exit code says why.
                }
                process.StandardInput.Close();
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
