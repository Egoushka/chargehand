using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Modules.Runners.Api;

/// <summary>The networks, volumes, workspaces, helper containers and the egress proxy of <see cref="DockerCliEngine"/>.</summary>
public sealed partial class DockerCliEngine
{
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
}
