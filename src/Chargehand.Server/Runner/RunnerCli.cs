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
            error.WriteLine($"{KeyVariable} is not set: the runner needs a bearer key in that environment variable.");
            return Fail(error);
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
                return Fail(error);
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
                    return Fail(error);
            }
        }
        if (listen is null || images is not { Count: > 0 } || egress is null
            || !images.All(ContainerTemplate.IsImageReference) || !ContainerTemplate.IsImageReference(egress)
            || roots.Any(r => !r.StartsWith('/') || r.TrimEnd('/').Length == 0 || r.Contains("..", StringComparison.Ordinal))
            || forwards.Any(f => Chargehand.Egress.PortForward.Parse(f) is null))
            return Fail(error);
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

    private static RunnerSettings? Fail(TextWriter error)
    {
        error.WriteLine(Usage);
        return null;
    }
}
