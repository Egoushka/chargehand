using System.Net;
using System.Text.Json;

namespace Chargehand.Egress;

/// <param name="Listen">Where the proxy listens; inside a batch's egress container, 0.0.0.0 on its port.</param>
/// <param name="ModelListen">Where the model endpoint listens (<see cref="ModelGateway"/>); null: the container has none.</param>
/// <param name="ModelHost">The one host the model endpoint forwards to.</param>
public sealed record EgressOptions(IPEndPoint Listen, IReadOnlyList<string> Allow, IReadOnlyList<PortForward> Forwards, IPEndPoint? ModelListen = null, string ModelHost = EgressCli.DefaultModelHost);

/// <summary><c>chargehand egress --listen &lt;ip:port&gt; --allow &lt;host,*.suffix,...&gt;</c>: the allowlist proxy of a batch's egress container
/// (ADR 0039). Profile-free. Each connection decision is one JSON line on stderr, which is what <c>docker logs</c> shows.</summary>
public static class EgressCli
{
    public const string Usage = "usage: chargehand egress --listen <ip:port> --allow <host,*.suffix,...> [--forward <port>=<host>:<port>]... [--model-listen <ip:port> [--model-host <host>]]";

    public const string DefaultModelHost = "api.anthropic.com";

    /// <summary>The model endpoint's secrets arrive in the environment (the container's env file), never as arguments, which <c>docker inspect</c> and process listings show.</summary>
    public const string CredentialVariable = "CHARGEHAND_EGRESS_MODEL_CREDENTIAL";
    public const string KeyVariable = "CHARGEHAND_EGRESS_TOKEN_KEY";

    public static EgressOptions? Parse(IReadOnlyList<string> args, TextWriter error)
    {
        IPEndPoint? listen = null;
        List<string>? allow = null;
        List<PortForward> forwards = [];
        IPEndPoint? modelListen = null;
        var modelHost = DefaultModelHost;
        for (var i = 0; i < args.Count; i += 2)
        {
            if (i + 1 >= args.Count)
                return Fail(error);
            switch (args[i])
            {
                case "--listen" when IPEndPoint.TryParse(args[i + 1], out var endpoint):
                    listen = endpoint;
                    break;
                case "--forward" when PortForward.Parse(args[i + 1]) is { } forward:
                    forwards.Add(forward);
                    break;
                case "--model-listen" when IPEndPoint.TryParse(args[i + 1], out var modelEndpoint):
                    modelListen = modelEndpoint;
                    break;
                case "--model-host":
                    modelHost = args[i + 1];
                    break;
                case "--allow":
                    allow = [.. args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                    break;
                default:
                    return Fail(error);
            }
        }
        if (listen is null || allow is null || allow.Count == 0)
            return Fail(error);
        try
        {
            _ = new AllowlistMatcher(allow);
            if (ModelGateway.RefuseHost(modelHost) is { } refused)
                throw new ArgumentException($"--model-host: {refused}");
        }
        catch (ArgumentException e)
        {
            error.WriteLine(e.Message);
            return Fail(error);
        }
        return new EgressOptions(listen, allow, forwards, modelListen, modelHost);
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter error, CancellationToken ct)
    {
        if (Parse(args, error) is not { } options)
            return 2;
        await using var proxy = new AllowlistProxy(new AllowlistMatcher(options.Allow), log: e =>
            error.WriteLine(JsonSerializer.Serialize(new { at = e.At, run = e.RunId, host = e.Host, port = e.Port, allowed = e.Allowed, reason = e.Reason })));
        await proxy.StartAsync(options.Listen, ct);
        await using var gateway = StartModelGateway(options, error);
        if (options.ModelListen is not null && gateway is null)
            return 2;
        var forwards = options.Forwards.Select(f => new TcpForward(f)).ToList();
        try
        {
            foreach (var forward in forwards)
                await forward.StartAsync(ct);
            await Run(proxy, error, ct);
        }
        finally
        {
            foreach (var forward in forwards)
                await forward.DisposeAsync();
        }
        return 0;
    }

    private static ModelGateway? StartModelGateway(EgressOptions options, TextWriter error)
    {
        if (options.ModelListen is not { } listen)
            return null;
        var credential = Environment.GetEnvironmentVariable(CredentialVariable);
        var key = Environment.GetEnvironmentVariable(KeyVariable);
        // Nothing else in this process may read them: drop them from the environment as soon as they are held.
        Environment.SetEnvironmentVariable(CredentialVariable, null);
        Environment.SetEnvironmentVariable(KeyVariable, null);
        if (string.IsNullOrEmpty(credential) || string.IsNullOrEmpty(key) || key.Length != 64 || !key.All(Uri.IsHexDigit))
        {
            error.WriteLine($"the model endpoint needs {CredentialVariable} and {KeyVariable} (64 hex characters) in the environment");
            return null;
        }
        var gateway = new ModelGateway(key, credential, options.ModelHost, log: e =>
            error.WriteLine(JsonSerializer.Serialize(new { at = e.At, model_run = e.RunId, status = e.Status, tokens = e.Tokens, reason = e.Reason })));
        gateway.Start(listen.Address.Equals(IPAddress.Any) ? $"http://+:{listen.Port}/" : $"http://{listen.Address}:{listen.Port}/");
        error.WriteLine($"model endpoint listening on {listen}, forwarding to {options.ModelHost}");
        return gateway;
    }

    private static async Task Run(AllowlistProxy proxy, TextWriter error, CancellationToken ct)
    {
        error.WriteLine($"egress proxy listening on {proxy.LocalEndPoint}");
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
    }

    private static EgressOptions? Fail(TextWriter error)
    {
        error.WriteLine(Usage);
        return null;
    }
}
