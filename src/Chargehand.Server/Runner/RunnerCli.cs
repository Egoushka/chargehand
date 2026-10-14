using System.Net;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Kernel;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Server;

/// <summary><c>chargehand runner --listen &lt;ip:port&gt; --images &lt;digest,...&gt; --egress-image &lt;digest&gt; [--max-containers N] [--allowed-hosts a,b]</c>:
/// the runner service in front of the container engine (ADR 0039). <c>--max-bundle-mb</c> is the most a repository bundle the server sends may be (ADR 0058; default 512). Profile-free; the bearer key is the environment variable
/// <c>CHARGEHAND_RUNNER_KEY</c>, never an argument, so it does not show in a process listing.</summary>
public static class RunnerCli
{
    public const string KeyVariable = "CHARGEHAND_RUNNER_KEY";

    public const string Usage = "usage: chargehand runner --listen <ip:port> --images <name@sha256:...,...> --egress-image <name@sha256:...> [--max-containers N] [--allowed-hosts a,b] [--source-roots /dir,...] [--outside-networks name,...] [--forwards listen=host:port,...] [--service-images name@sha256:...,...] [--max-bundle-mb N] [--keep-awake on|off] [--drain-on battery,lid] [--drain-file /path] [--host-source auto|proc|engine]  (key: CHARGEHAND_RUNNER_KEY)";

    public static RunnerSettings? Parse(IReadOnlyList<string> args, string? key, TextWriter error)
    {
        if (string.IsNullOrEmpty(key))
        {
            return Fail(error, $"{KeyVariable} is not set: the runner needs a bearer key in that environment variable.");
        }
        IPEndPoint? listen = null;
        List<string>? images = null;
        string? egress = null;
        var max = 8;
        List<string> hosts = [];
        List<string> roots = [];
        List<string> outside = [];
        List<string> forwards = [];
        List<string> serviceImages = [];
        var maxBundleMb = (int)(RunnerPolicy.DefaultMaxBundleBytes / (1024 * 1024));
        var keepAwake = false;
        var drainOn = "";
        string? drainFile = null;
        var hostSource = "auto";
        for (var i = 0; i < args.Count; i += 2)
        {
            if (i + 1 >= args.Count)
                return Fail(error, $"{args[i]} needs a value");
            switch (args[i])
            {
                case "--listen" when IPEndPoint.TryParse(args[i + 1], out var endpoint):
                    listen = endpoint;
                    break;
                case "--images":
                    images = [.. args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                    break;
                case "--egress-image":
                    egress = args[i + 1];
                    break;
                case "--max-containers" when int.TryParse(args[i + 1], out var n) && n is >= 1 and <= 64:
                    max = n;
                    break;
                case "--outside-networks":
                    outside = [.. args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                    break;
                case "--forwards":
                    forwards = [.. args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                    break;
                case "--max-bundle-mb" when int.TryParse(args[i + 1], out var mb) && mb is >= 1 and <= 4096:
                    maxBundleMb = mb;
                    break;
                case "--keep-awake" when args[i + 1] is "on" or "off":
                    keepAwake = args[i + 1] == "on";
                    break;
                case "--drain-on" when DrainRules.ParseOn(args[i + 1], null) is not null:
                    drainOn = args[i + 1];
                    break;
                case "--drain-file" when args[i + 1].StartsWith('/') && !args[i + 1].Contains("..", StringComparison.Ordinal):
                    drainFile = args[i + 1];
                    break;
                case "--host-source" when args[i + 1] is "auto" or "proc" or "engine":
                    hostSource = args[i + 1];
                    break;
                case "--service-images":
                    serviceImages = [.. args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                    break;
                case "--source-roots":
                    roots = [.. args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                    break;
                case "--allowed-hosts":
                    hosts = [.. args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                    break;
                default:
                    return Fail(error, KnownOptions.Contains(args[i]) ? $"{args[i]} has an invalid value '{args[i + 1]}'" : $"unknown argument '{args[i]}'");
            }
        }
        if (listen is null)
            return Fail(error, "--listen is required (ip:port)");
        if (images is not { Count: > 0 })
            return Fail(error, "--images is required (name@sha256:<digest>, comma separated)");
        if (images.FirstOrDefault(i => !ContainerTemplate.IsImageReference(i)) is { } badImage)
            return Fail(error, $"--images: '{badImage}' is not name@sha256:<64 hex> (a tag is not accepted)");
        if (egress is null)
            return Fail(error, "--egress-image is required (name@sha256:<digest>)");
        if (!ContainerTemplate.IsImageReference(egress))
            return Fail(error, $"--egress-image: '{egress}' is not name@sha256:<64 hex> (a tag is not accepted)");
        if (roots.FirstOrDefault(r => !r.StartsWith('/') || r.TrimEnd('/').Length == 0 || r.Contains("..", StringComparison.Ordinal)) is { } badRoot)
            return Fail(error, $"--source-roots: '{badRoot}' must be an absolute path without '..'");
        if (serviceImages.FirstOrDefault(i => !ContainerTemplate.IsImageReference(i)) is { } badService)
            return Fail(error, $"--service-images: '{badService}' is not name@sha256:<64 hex> (a tag is not accepted)");
        if (forwards.FirstOrDefault(f => Chargehand.Modules.Gateway.Api.PortForward.Parse(f) is null) is { } badForward)
            return Fail(error, $"--forwards: '{badForward}' is not listen-port=host:port");
        // Each of these starts fine and then silently refuses work: say so now.
        if (roots.Count == 0)
            error.WriteLine("note: no --source-roots, so the runner prepares no workspace from a path (a server that sends the repository as a bundle needs none)");
        if (outside.Count == 0)
            error.WriteLine("note: no --outside-networks, so an egress container can join no network but the default one");
        if (forwards.Count == 0)
            error.WriteLine("note: no --forwards, so an egress container carries no forward and a session cannot call the server back");
        var drain = DrainRules.ParseOn(drainOn, drainFile)!;
        if (keepAwake && !OperatingSystem.IsMacOS())
            error.WriteLine("note: --keep-awake does nothing here: it holds a Mac awake with caffeinate");
        if ((drain.OnBattery || drain.OnLidClosed) && !OperatingSystem.IsMacOS())
            error.WriteLine("note: --drain-on battery and lid read macOS's pmset and ioreg, so they never drain here; --drain-file works everywhere");
        return new RunnerSettings(listen.Port, key, new RunnerPolicy(images, egress, max, SourceRoots: roots, OutsideNetworks: outside, Forwards: forwards, ServiceImages: serviceImages, MaxBundleBytes: maxBundleMb * 1024L * 1024), listen.Address.ToString(), hosts,
            new RunnerOptions(keepAwake, drain.Any ? drain : null, hostSource));
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter error, CancellationToken ct)
    {
        if (Parse(args, Environment.GetEnvironmentVariable(KeyVariable), error) is not { } settings)
            return 2;
        var engine = new DockerCliEngine();
        await ClearLeftoversAsync(engine, error, ct);
        var options = settings.Options ?? new RunnerOptions();
        // The runner's own /proc, unless it is on a machine whose engine is a virtual machine: then the engine is asked (ADR 0058).
        IHostSource host = options.HostSource == "engine" || (options.HostSource == "auto" && !OperatingSystem.IsLinux()) ? new EngineHostProbe(engine, settings.Policy.Images[0]) : new HostProbe();
        if (options.Drain is { } rules)
            host = new DrainingHost(host, new HostDrain(rules, new MacPowerReader()));
        var app = RunnerServer.Create(settings, engine, host);
        await app.StartAsync(ct);
        error.WriteLine($"runner listening on {string.Join(", ", app.Urls)}");
        using var holder = new CaffeinateHolder();
        var awake = options.KeepAwake && OperatingSystem.IsMacOS() ? Task.Run(() => new KeepAwake(engine.RunningSessionsAsync, holder, log: message => error.WriteLine(message)).RunAsync(ct), CancellationToken.None) : Task.CompletedTask;
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
        await awake;
        await app.StopAsync(CancellationToken.None);
        return 0;
    }

    /// <summary>A runner that was killed mid-transfer or mid-workspace leaves a helper container created and never started; the engine would keep it. An engine that does not answer now is a note: the first transfer of
    /// the run clears its own helper anyway.</summary>
    internal static async Task ClearLeftoversAsync(DockerCliEngine engine, TextWriter error, CancellationToken ct)
    {
        try
        {
            var removed = await engine.RemoveStaleHelpersAsync(ct);
            if (removed.Count > 0)
                error.WriteLine($"removed {removed.Count} leftover helper containers: {string.Join(", ", removed)}");
        }
        catch (ChargehandException e)
        {
            error.WriteLine($"note: leftover helper containers were not cleared ({e.Message})");
        }
    }

    private static readonly HashSet<string> KnownOptions = ["--listen", "--images", "--egress-image", "--max-containers", "--outside-networks", "--forwards", "--source-roots", "--allowed-hosts", "--service-images", "--max-bundle-mb", "--keep-awake", "--drain-on", "--drain-file", "--host-source"];

    /// <summary>The reason first, on its own line, then the usage: the usage names every flag, so alone it does not say which argument was wrong.</summary>
    private static RunnerSettings? Fail(TextWriter error, string reason)
    {
        error.WriteLine(reason);
        error.WriteLine(Usage);
        return null;
    }
}
