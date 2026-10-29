using System.Net;
using System.Text.Json;

namespace Chargehand.Modules.Gateway.Api;

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

    /// <summary>The fallback of ADR 0088, also from the env file: the key is the secret; the rest (host, dollars, <c>input,output,cache_read,cache_write</c> per million tokens) is read
    /// with it so one file describes the whole batch. All four set, or none.</summary>
    public const string FallbackCredentialVariable = "CHARGEHAND_EGRESS_FALLBACK_CREDENTIAL";
    public const string FallbackHostVariable = "CHARGEHAND_EGRESS_FALLBACK_HOST";
    /// <summary>Optional: the fallback's base URL when it is not <c>https://&lt;host&gt;</c> (a port, a path prefix, plain http to a private host).</summary>
    public const string FallbackBaseUrlVariable = "CHARGEHAND_EGRESS_FALLBACK_BASE_URL";
    public const string FallbackUsdVariable = "CHARGEHAND_EGRESS_FALLBACK_USD";
    public const string FallbackPriceVariable = "CHARGEHAND_EGRESS_FALLBACK_PRICE";

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
            error.WriteLine(JsonSerializer.Serialize(new EgressLogLine(e.At, e.RunId, e.Host, e.Port, e.Allowed, e.Reason), GatewayJsonContext.Default.EgressLogLine)));
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
        var fallback = FallbackFromEnvironment(Environment.GetEnvironmentVariable, error, out var fallbackValid);
        Environment.SetEnvironmentVariable(FallbackCredentialVariable, null);
        if (!fallbackValid)
            return null;
        var gateway = new ModelGateway(key, credential, options.ModelHost, log: e =>
            error.WriteLine(JsonSerializer.Serialize(new GatewayLogLine(e.At, e.RunId, e.Status, e.Tokens, e.Reason, e.Fallback, e.Usd, e.TotalUsd, e.Calls, e.Closed, e.LimitedUntil), GatewayJsonContext.Default.GatewayLogLine)), fallback: fallback);
        gateway.Start(listen.Address.Equals(IPAddress.Any) ? $"http://+:{listen.Port}/" : $"http://{listen.Address}:{listen.Port}/");
        error.WriteLine($"model endpoint listening on {listen}, forwarding to {options.ModelHost}");
        return gateway;
    }

    /// <returns>The fallback the environment describes; null with <paramref name="valid"/> true when none is set, null with false (and a line on <paramref name="error"/>) when it is partly set or wrong. Never prints a value.</returns>
    public static GatewayFallback? FallbackFromEnvironment(Func<string, string?> env, TextWriter error, out bool valid)
    {
        var credential = env(FallbackCredentialVariable);
        var host = env(FallbackHostVariable);
        var usd = env(FallbackUsdVariable);
        var price = env(FallbackPriceVariable);
        valid = true;
        if (string.IsNullOrEmpty(credential) && string.IsNullOrEmpty(host) && string.IsNullOrEmpty(usd) && string.IsNullOrEmpty(price))
            return null;
        if (string.IsNullOrEmpty(credential) || string.IsNullOrEmpty(host) || !decimal.TryParse(usd, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var allowance) || allowance <= 0
            || ParsePrice(price) is not { } parsed)
        {
            error.WriteLine($"the fallback needs {FallbackCredentialVariable}, {FallbackHostVariable}, {FallbackUsdVariable} (above 0) and {FallbackPriceVariable} (four prices per million tokens) in the environment");
            valid = false;
            return null;
        }
        var baseUrl = env(FallbackBaseUrlVariable);
        var result = new GatewayFallback(credential, host, allowance, parsed, string.IsNullOrEmpty(baseUrl) ? null : baseUrl);
        if (result.Endpoint(out var refused) is null)
        {
            error.WriteLine($"{FallbackHostVariable} / {FallbackBaseUrlVariable}: {refused}");
            valid = false;
            return null;
        }
        return result;
    }

    /// <returns>The four prices (input, output, cache read, cache write per million tokens), or null when the text is not four non-negative numbers.</returns>
    private static FallbackPrice? ParsePrice(string? price)
    {
        var parts = price?.Split(',');
        if (parts is not { Length: 4 })
            return null;
        var n = new decimal[4];
        for (var i = 0; i < 4; i++)
            if (!decimal.TryParse(parts[i], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out n[i]) || n[i] < 0)
                return null;
        return new FallbackPrice(n[0], n[1], n[2], n[3]);
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
