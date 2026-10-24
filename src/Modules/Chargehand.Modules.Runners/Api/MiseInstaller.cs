using System.Diagnostics;
using System.Globalization;

namespace Chargehand.Modules.Runners.Api;

/// <summary>One toolchain install (ADR 0078): which run, which image, which volumes.</summary>
/// <param name="RunId">The session's run id; the helper's is <c>&lt;run id&gt;-install</c>.</param>
/// <param name="Image">The session image by digest: the helper runs the image the session runs.</param>
/// <param name="EgressImage">The egress proxy's image, as the batch's own is.</param>
/// <param name="WorkVolume">The run's work volume, which holds the pinned checkout the helper reads the pins from.</param>
/// <param name="Home">The repository's home volume (<see cref="HomeVolumes.Name"/>), which the helper writes the toolchains into.</param>
/// <param name="Outside">The network the helper's egress joins to reach the toolchain hosts.</param>
/// <param name="Timeout">Default 15 minutes; a helper that runs longer is killed.</param>
public sealed record MiseInstall(string RunId, string Image, string EgressImage, string WorkVolume, string OutVolume, string Home, int MemoryMb, double Cpus, int Pids,
    string Outside = BatchNetwork.DefaultOutside, TimeSpan? Timeout = null);

/// <param name="Tools">The lines <c>mise ls --current</c> printed, which the session's environment note lists.</param>
/// <param name="LogTail">The end of the helper's output, for a failure's message.</param>
public sealed record MiseInstallResult(int ExitCode, bool TimedOut, TimeSpan Elapsed, IReadOnlyList<string> Tools, string LogTail)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

/// <summary>Runs the install helper of ADR 0078 before a session: a container of the session image with no model in it, on a network of its own whose egress reaches
/// <see cref="HomeVolumes.ToolchainHosts"/> and nothing else, so the session's own allowlist does not grow. The network, the egress container and the helper are removed afterwards, whatever happened.</summary>
public sealed class MiseInstaller(IContainerEngine engine, TimeSpan? poll = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);

    private const int LogBytes = 64 * 1024;
    private const int TailChars = 4000;
    private static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(1);

    public async Task<MiseInstallResult> RunAsync(MiseInstall install, CancellationToken ct)
    {
        var helper = install.RunId + ContainerTemplate.InstallSuffix;
        var net = await new BatchNetwork(engine).CreateAsync(helper, install.EgressImage, HomeVolumes.ToolchainHosts, ct, install.Outside);
        string? id = null;
        try
        {
            var proxy = net.ProxyUrl(helper);
            var spec = new ContainerSpec(helper, install.Image, install.WorkVolume, install.OutVolume, net.Network, new Dictionary<string, string> { ["HTTPS_PROXY"] = proxy, ["HTTP_PROXY"] = proxy },
                install.MemoryMb, install.Cpus, install.Pids, ContainerTemplate.InstallCommand, Home: install.Home, Install: true);
            id = await engine.StartAsync(spec, ct);
            var started = Stopwatch.StartNew();
            var deadline = install.Timeout ?? DefaultTimeout;
            ContainerState state;
            while ((state = await engine.InspectAsync(id, ct)).Status == ContainerStatus.Running)
            {
                if (started.Elapsed > deadline)
                {
                    await engine.SignalAsync(id, "SIGKILL", CancellationToken.None);
                    return new MiseInstallResult(-1, true, started.Elapsed, [], Tail(await engine.LogsTailAsync(id, LogBytes, ct)));
                }
                await Task.Delay(poll ?? DefaultPoll, ct);
            }
            var logs = await engine.LogsTailAsync(id, LogBytes, ct);
            return new MiseInstallResult(state.ExitCode ?? -1, false, started.Elapsed, state.ExitCode == 0 ? ToolsIn(logs) : [], Tail(logs));
        }
        finally
        {
            if (id is not null)
                await engine.RemoveAsync(id, CancellationToken.None);
            await new BatchNetwork(engine).RemoveAsync(net, CancellationToken.None);
        }
    }

    /// <summary>The lines after the last <see cref="ContainerTemplate.ToolsMarker"/>, without blank ones.</summary>
    private static IReadOnlyList<string> ToolsIn(string logs)
    {
        var lines = logs.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var marker = Array.FindLastIndex(lines, l => l == ContainerTemplate.ToolsMarker);
        return marker < 0 ? [] : [.. lines[(marker + 1)..].Select(l => l.TrimEnd()).Where(l => l.Length > 0)];
    }

    private static string Tail(string logs) => logs.Length > TailChars ? logs[^TailChars..] : logs;

    /// <summary>A line for the task's events: how long the install took and what it left.</summary>
    public static string Describe(MiseInstallResult result) =>
        string.Create(CultureInfo.InvariantCulture, $"toolchain installed in {result.Elapsed.TotalSeconds:0.#} s ({result.Tools.Count} tools)");
}
