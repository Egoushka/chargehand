using System.Net;
using Chargehand.Containers;
using Chargehand.Runner;

namespace Chargehand.Server;

/// <summary><c>chargehand runner --listen &lt;ip:port&gt; --images &lt;digest,...&gt; --egress-image &lt;digest&gt; [--max-containers N] [--allowed-hosts a,b]</c>:
/// the runner service in front of the container engine (ADR 0039). Profile-free; the bearer key is the environment variable
/// <c>CHARGEHAND_RUNNER_KEY</c>, never an argument, so it does not show in a process listing.</summary>
public static class RunnerCli
{
    public const string KeyVariable = "CHARGEHAND_RUNNER_KEY";

    public const string Usage = "usage: chargehand runner --listen <ip:port> --images <name@sha256:...,...> --egress-image <name@sha256:...> [--max-containers N] [--allowed-hosts a,b] [--source-roots /dir,...] [--outside-networks name,...] [--forwards listen=host:port,...]  (key: CHARGEHAND_RUNNER_KEY)";

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
        if (forwards.FirstOrDefault(f => Chargehand.Egress.PortForward.Parse(f) is null) is { } badForward)
            return Fail(error, $"--forwards: '{badForward}' is not listen-port=host:port");
        // Each of these starts fine and then silently refuses work: say so now.
        if (roots.Count == 0)
            error.WriteLine("note: no --source-roots, so the runner prepares no workspace");
        if (outside.Count == 0)
            error.WriteLine("note: no --outside-networks, so an egress container can join no network but the default one");
        if (forwards.Count == 0)
            error.WriteLine("note: no --forwards, so an egress container carries no forward and a session cannot call the server back");
        return new RunnerSettings(listen.Port, key, new RunnerPolicy(images, egress, max, SourceRoots: roots, OutsideNetworks: outside, Forwards: forwards), listen.Address.ToString(), hosts);
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter error, CancellationToken ct)
    {
        if (Parse(args, Environment.GetEnvironmentVariable(KeyVariable), error) is not { } settings)
            return 2;
        var app = RunnerServer.Create(settings, new DockerCliEngine());
        await app.StartAsync(ct);
        error.WriteLine($"runner listening on {string.Join(", ", app.Urls)}");
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
        await app.StopAsync(CancellationToken.None);
        return 0;
    }

    private static readonly HashSet<string> KnownOptions = ["--listen", "--images", "--egress-image", "--max-containers", "--outside-networks", "--forwards", "--source-roots", "--allowed-hosts"];

    /// <summary>The reason first, on its own line, then the usage: the usage names every flag, so alone it does not say which argument was wrong.</summary>
    private static RunnerSettings? Fail(TextWriter error, string reason)
    {
        error.WriteLine(reason);
        error.WriteLine(Usage);
        return null;
    }
}
