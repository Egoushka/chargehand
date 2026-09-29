using System.Collections.Concurrent;
using Chargehand.Config;
using Chargehand.Runtime;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Chargehand.Mcp;

/// <summary>
/// One lazily opened <see cref="McpClient"/> per profile server, shared by memory and services (ADR 0034) and disposed at
/// exit. A connection whose transport closed is opened again on the next request; a failed attempt is not remembered.
/// Every reason a server cannot be used, and only that, surfaces as <see cref="McpUnavailableException"/>. A secret that
/// no source resolves means no connection, never an anonymous one.
/// </summary>
public sealed class McpConnectionPool : IAsyncDisposable
{
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(30);

    private static readonly McpClientOptions ClientOptions = new()
    {
        ClientInfo = new() { Name = "chargehand", Version = typeof(McpConnectionPool).Assembly.GetName().Version?.ToString() ?? "0" },
    };

    private readonly IReadOnlyDictionary<string, McpServerSettings> _servers;
    private readonly Func<string, string> _secret;
    private readonly Func<string, McpServerSettings, CancellationToken, Task<IClientTransport>>? _transports;
    private readonly TimeSpan _connectTimeout;
    private readonly ConcurrentDictionary<string, Lazy<Task<McpClient>>> _clients = new();
    private readonly CancellationTokenSource _closing = new();

    /// <param name="servers">The profile's <c>mcp_servers</c>.</param>
    /// <param name="secret">Resolves a secret item through the profile's chain (<see cref="Profile.Secret"/>).</param>
    /// <param name="transports">Tests: builds the transport instead of the default one (HTTP for a url, stdio for a command),
    /// after the secrets the server needs resolved.</param>
    /// <param name="connectTimeout">How long opening a connection, handshake included, may take (default 30 s).</param>
    public McpConnectionPool(
        IReadOnlyDictionary<string, McpServerSettings> servers,
        Func<string, string> secret,
        Func<string, McpServerSettings, CancellationToken, Task<IClientTransport>>? transports = null,
        TimeSpan? connectTimeout = null)
    {
        _servers = servers;
        _secret = secret;
        _transports = transports;
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
    }

    /// <summary>The connected client of a profile server. Cancelling <paramref name="ct"/> stops this call waiting; the
    /// attempt it joined goes on for the other callers.</summary>
    /// <exception cref="McpUnavailableException">The server is not listed, a secret is unresolved, or it did not connect.</exception>
    public async Task<McpClient> GetAsync(string server, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_closing.IsCancellationRequested, this);
        if (!_servers.ContainsKey(server))
            throw new McpUnavailableException(McpUnavailableException.UnknownServer, server, "no such server in mcp_servers");
        var (entry, client) = await OpenOrShareAsync(server, ct);
        if (!client.Completion.IsCompleted)
            return client;
        // Its transport closed (the server went away): forget it and open a fresh one.
        _clients.TryRemove(KeyValuePair.Create(server, entry));
        await client.DisposeAsync();
        return (await OpenOrShareAsync(server, ct)).Client;
    }

    /// <summary>
    /// What a worker's runtime needs to reach a profile server itself (ADR 0034): the URL or command, with every
    /// <c>{secret:item}</c> value replaced. Nothing connects. A <c>transport</c> of <c>auto</c> (or none) is Streamable HTTP
    /// here; a runtime's config cannot fall back to SSE as this pool's client does. Blocks while a command secret source runs.
    /// </summary>
    /// <exception cref="McpUnavailableException">The server is not listed, or a secret it needs is unresolved.</exception>
    public ServiceTransport Transport(string server)
    {
        if (!_servers.TryGetValue(server, out var settings))
            throw new McpUnavailableException(McpUnavailableException.UnknownServer, server, "no such server in mcp_servers");
        var secret = SecretReader(server);
        return settings.Url is not null
            ? new HttpServiceTransport(new Uri(settings.Url), Resolved(settings.Headers, secret),
                settings.Transport == McpServerSettings.TransportSse ? HttpServiceProtocol.Sse : HttpServiceProtocol.StreamableHttp)
            : new StdioServiceTransport(settings.Command!, Resolved(settings.Env, secret));
    }

    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync();
        foreach (var entry in _clients.Values.Where(e => e.IsValueCreated && e.Value.IsCompletedSuccessfully))
            await entry.Value.Result.DisposeAsync();
        _clients.Clear();
        _closing.Dispose();
    }

    internal static HttpClientTransportOptions HttpOptions(string name, McpServerSettings settings, Func<string, string> secret) => new()
    {
        Endpoint = new Uri(settings.Url!),
        Name = name,
        TransportMode = settings.Transport switch
        {
            null or McpServerSettings.TransportAuto => HttpTransportMode.AutoDetect,
            McpServerSettings.TransportStreamableHttp => HttpTransportMode.StreamableHttp,
            McpServerSettings.TransportSse => HttpTransportMode.Sse,
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Transport, "not a transport of an MCP server"),
        },
        AdditionalHeaders = settings.Headers?.ToDictionary(h => h.Key, h => SecretTemplate.Resolve(h.Value, secret)),
    };

    /// <summary>The SDK's default environment (PATH, HOME and the like) plus the declared <c>env</c>; nothing else of ours
    /// is inherited (ADR 0034), since it can hold this process's own API key and credentials.</summary>
    internal static StdioClientTransportOptions StdioOptions(string name, McpServerSettings settings, Func<string, string> secret)
    {
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        foreach (var (key, value) in settings.Env ?? new Dictionary<string, string>())
            environment[key] = SecretTemplate.Resolve(value, secret);
        return new()
        {
            Name = name,
            Command = settings.Command![0],
            Arguments = [.. settings.Command.Skip(1)],
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment,
            StandardErrorLines = line => Console.Error.WriteLine($"[mcp {name}] {ChargehandException.Scrub(line)}"),
        };
    }

    private async Task<(Lazy<Task<McpClient>> Entry, McpClient Client)> OpenOrShareAsync(string server, CancellationToken ct)
    {
        var entry = _clients.GetOrAdd(server, name => new Lazy<Task<McpClient>>(() => OpenAsync(name)));
        try
        {
            return (entry, await entry.Value.WaitAsync(ct));
        }
        catch when (entry.Value.IsFaulted)
        {
            _clients.TryRemove(KeyValuePair.Create(server, entry));
            throw;
        }
    }

    private async Task<McpClient> OpenAsync(string name)
    {
        var settings = _servers[name];
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        attempt.CancelAfter(_connectTimeout);
        IClientTransport? transport = null;
        try
        {
            var secret = SecretReader(name);

            // Every secret first, off the caller's thread (a command source can take its full time limit): none left means no connection.
            await Task.Run(() =>
            {
                foreach (var template in (settings.Headers?.Values ?? []).Concat(settings.Env?.Values ?? []))
                    SecretTemplate.Resolve(template, secret);
            }, CancellationToken.None);
            transport = _transports is null
                ? settings.Url is not null ? new HttpClientTransport(HttpOptions(name, settings, secret)) : new StdioClientTransport(StdioOptions(name, settings, secret))
                : await _transports(name, settings, attempt.Token);
            var client = await McpClient.CreateAsync(transport, ClientOptions, cancellationToken: attempt.Token);
            if (!_closing.IsCancellationRequested)
                return client;
            await client.DisposeAsync();
            throw new OperationCanceledException(_closing.Token);
        }
        catch (Exception e)
        {
            await DisposeQuietlyAsync(transport);
            if (e is McpUnavailableException || (e is OperationCanceledException && _closing.IsCancellationRequested))
                throw;
            throw new McpUnavailableException(McpUnavailableException.Unreachable, name,
                e is OperationCanceledException ? $"did not connect within {_connectTimeout.TotalSeconds:0.#} s" : e.Message);
        }
    }

    /// <summary>The profile's secrets for one server: each item is read once, and a failure names the item, never a value.</summary>
    private Func<string, string> SecretReader(string server)
    {
        var read = new Dictionary<string, string>();
        return item =>
        {
            if (read.TryGetValue(item, out var known))
                return known;
            try
            {
                return read[item] = _secret(item);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new McpUnavailableException(McpUnavailableException.SecretUnresolved, server,
                    e is InvalidOperationException ? e.Message : $"secret '{item}' could not be read ({e.GetType().Name})");
            }
        };
    }

    private static Dictionary<string, string> Resolved(IReadOnlyDictionary<string, string>? values, Func<string, string> secret) =>
        (values ?? new Dictionary<string, string>()).ToDictionary(v => v.Key, v => SecretTemplate.Resolve(v.Value, secret));

    private static async Task DisposeQuietlyAsync(IClientTransport? transport)
    {
        if (transport is not IAsyncDisposable disposable)
            return;
        try
        {
            await disposable.DisposeAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // the transport never opened, or the client the SDK created has already closed it
        }
    }
}
