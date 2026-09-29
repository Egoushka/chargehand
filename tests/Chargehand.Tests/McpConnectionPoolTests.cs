using System.Diagnostics;
using Chargehand.Config;
using Chargehand.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Chargehand.Tests;

public class McpConnectionPoolTests
{
    private static string Secret(string item) =>
        item == "tok" ? "s3cret" : throw new InvalidOperationException($"no secret source resolved '{item}'");

    private static IReadOnlyDictionary<string, McpServerSettings> Servers(string name = "gw", string token = "tok") => new Dictionary<string, McpServerSettings>
    {
        [name] = new(Url: "https://mcp.example.internal/mcp", Headers: new Dictionary<string, string> { ["Authorization"] = $"Bearer {{secret:{token}}}" }),
    };

    private static IReadOnlyDictionary<string, McpServerSettings> Http(Uri endpoint, string? transport) => new Dictionary<string, McpServerSettings>
    {
        ["gw"] = new(Url: endpoint.ToString(), Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer {secret:tok}" }, Transport: transport),
    };

    private static FakeTool Ping() => new("ping", _ => FakeMcpServer.Text("pong"));

    [Fact]
    public async Task A_server_is_opened_once_and_reused()
    {
        var fakes = new List<FakeMcpServer>();
        await using var pool = new McpConnectionPool(Servers(), Secret, async (_, _, _) =>
        {
            var fake = new FakeMcpServer(Ping());
            fakes.Add(fake);
            return await fake.TransportAsync();
        });

        var first = await pool.GetAsync("gw", CancellationToken.None);
        var second = await pool.GetAsync("gw", CancellationToken.None);

        Assert.Same(first, second);
        Assert.Single(fakes);
        await fakes[0].DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_requests_share_one_connection()
    {
        var opened = 0;
        FakeMcpServer? fake = null;
        await using var pool = new McpConnectionPool(Servers(), Secret, async (_, _, _) =>
        {
            Interlocked.Increment(ref opened);
            fake = new FakeMcpServer(Ping());
            return await fake.TransportAsync();
        });

        var clients = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => pool.GetAsync("gw", CancellationToken.None)));

        Assert.Equal(1, opened);
        Assert.All(clients, c => Assert.Same(clients[0], c));
        await fake!.DisposeAsync();
    }

    [Fact]
    public async Task A_closed_connection_is_opened_again_on_the_next_request()
    {
        var fakes = new List<FakeMcpServer>();
        await using var pool = new McpConnectionPool(Servers(), Secret, async (_, _, _) =>
        {
            var fake = new FakeMcpServer(Ping());
            fakes.Add(fake);
            return await fake.TransportAsync();
        });

        var first = await pool.GetAsync("gw", CancellationToken.None);
        await first.DisposeAsync();
        var second = await pool.GetAsync("gw", CancellationToken.None);

        Assert.NotSame(first, second);
        Assert.Equal(2, fakes.Count);
        foreach (var f in fakes)
            await f.DisposeAsync();
    }

    [Fact]
    public async Task A_failed_connection_is_not_remembered()
    {
        var attempts = 0;
        FakeMcpServer? fake = null;
        await using var pool = new McpConnectionPool(Servers(), Secret, async (_, _, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new IOException("connection refused");
            fake = new FakeMcpServer(Ping());
            return await fake.TransportAsync();
        });

        var failed = await Assert.ThrowsAsync<McpUnavailableException>(() => pool.GetAsync("gw", CancellationToken.None));
        Assert.Equal("unreachable", failed.Code);
        Assert.Equal("gw: connection refused", failed.Message);

        Assert.NotNull(await pool.GetAsync("gw", CancellationToken.None));
        await fake!.DisposeAsync();
    }

    [Fact]
    public async Task A_server_that_never_answers_is_unreachable_after_the_connect_timeout()
    {
        await using var pool = new McpConnectionPool(Servers(), Secret, (_, _, _) => Task.FromResult<IClientTransport>(new HangingTransport()),
            connectTimeout: TimeSpan.FromMilliseconds(200));

        var e = await Assert.ThrowsAsync<McpUnavailableException>(() => pool.GetAsync("gw", CancellationToken.None));

        Assert.Equal("unreachable", e.Code);
        Assert.Contains("gw", e.Message);
    }

    [Fact]
    public async Task The_callers_cancellation_propagates_and_is_not_a_failure_of_the_server()
    {
        await using var pool = new McpConnectionPool(Servers(), Secret, (_, _, _) => Task.FromResult<IClientTransport>(new HangingTransport()));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.GetAsync("gw", cts.Token));
    }

    [Fact]
    public async Task Disposing_the_pool_closes_its_connections()
    {
        FakeMcpServer? fake = null;
        var pool = new McpConnectionPool(Servers(), Secret, async (_, _, _) =>
        {
            fake = new FakeMcpServer(Ping());
            return await fake.TransportAsync();
        });
        var client = await pool.GetAsync("gw", CancellationToken.None);

        await pool.DisposeAsync();

        Assert.True(client.Completion.IsCompleted);
        await fake!.DisposeAsync();
    }

    [Fact]
    public async Task An_unknown_server_is_unavailable_and_says_which()
    {
        await using var pool = new McpConnectionPool(Servers(), Secret);
        var e = await Assert.ThrowsAsync<McpUnavailableException>(() => pool.GetAsync("nope", CancellationToken.None));
        Assert.Equal("unknown_server", e.Code);
        Assert.Contains("nope", e.Message);
    }

    [Fact]
    public async Task An_unresolved_secret_means_no_connection_and_names_the_item()
    {
        await using var pool = new McpConnectionPool(Servers(token: "missing"), Secret,
            (_, _, _) => throw new Xunit.Sdk.XunitException("must not connect without the credential"));
        var e = await Assert.ThrowsAsync<McpUnavailableException>(() => pool.GetAsync("gw", CancellationToken.None));
        Assert.Equal("secret_unresolved", e.Code);
        Assert.Contains("missing", e.Message);
        Assert.DoesNotContain("s3cret", e.Message);
    }

    [Fact]
    public async Task A_secret_source_that_throws_is_an_unresolved_secret_too()
    {
        await using var pool = new McpConnectionPool(Servers(), _ => throw new System.ComponentModel.Win32Exception("no such file s3cret-path"),
            (_, _, _) => throw new Xunit.Sdk.XunitException("must not connect without the credential"));
        var e = await Assert.ThrowsAsync<McpUnavailableException>(() => pool.GetAsync("gw", CancellationToken.None));
        Assert.Equal("secret_unresolved", e.Code);
        Assert.Contains("'tok'", e.Message);
        Assert.DoesNotContain("s3cret-path", e.Message);
    }

    [Fact]
    public async Task Each_secret_is_read_once_per_connection()
    {
        var reads = new List<string>();
        var servers = new Dictionary<string, McpServerSettings>
        {
            ["gw"] = new(Url: "https://mcp.example.internal/mcp", Headers: new Dictionary<string, string> { ["a"] = "{secret:tok}", ["b"] = "x {secret:tok}" }),
        };
        FakeMcpServer? fake = null;
        await using var pool = new McpConnectionPool(servers, item =>
        {
            reads.Add(item);
            return Secret(item);
        }, async (_, _, _) =>
        {
            fake = new FakeMcpServer(Ping());
            return await fake.TransportAsync();
        });

        await pool.GetAsync("gw", CancellationToken.None);

        Assert.Equal(["tok"], reads);
        await fake!.DisposeAsync();
    }

    [Fact]
    public void Http_options_carry_the_resolved_headers()
    {
        var options = McpConnectionPool.HttpOptions("gw", Servers()["gw"], Secret);
        Assert.Equal(new Uri("https://mcp.example.internal/mcp"), options.Endpoint);
        Assert.Equal("Bearer s3cret", options.AdditionalHeaders!["Authorization"]);
    }

    [Theory]
    [InlineData(null, HttpTransportMode.AutoDetect)]
    [InlineData("auto", HttpTransportMode.AutoDetect)]
    [InlineData("streamable-http", HttpTransportMode.StreamableHttp)]
    [InlineData("sse", HttpTransportMode.Sse)]
    public void Http_options_set_the_transport_mode_from_the_transport(string? transport, HttpTransportMode expected)
    {
        var settings = new McpServerSettings(Url: "http://localhost:8080/sse", Transport: transport);
        Assert.Equal(expected, McpConnectionPool.HttpOptions("archive", settings, Secret).TransportMode);
    }

    [Fact]
    public void Stdio_options_carry_the_declared_env_and_do_not_inherit_ours()
    {
        var settings = new McpServerSettings(Command: ["npx", "-y", "example-notes-mcp"], Env: new Dictionary<string, string> { ["NOTES_TOKEN"] = "{secret:tok}" });
        var options = McpConnectionPool.StdioOptions("notes", settings, Secret);
        Assert.Equal("npx", options.Command);
        Assert.Equal(["-y", "example-notes-mcp"], options.Arguments);
        Assert.False(options.InheritEnvironmentVariables);
        Assert.Equal("s3cret", options.EnvironmentVariables!["NOTES_TOKEN"]);
    }

    [Fact]
    public void Stdio_options_start_from_the_sdks_default_environment_and_nothing_else()
    {
        const string ours = "CHARGEHAND_TEST_MCP_LEAK";
        Environment.SetEnvironmentVariable(ours, "leaked");
        try
        {
            var options = McpConnectionPool.StdioOptions("notes", new McpServerSettings(Command: ["example-notes-mcp"]), Secret);

            Assert.Equal(StdioClientTransportOptions.GetDefaultEnvironmentVariables().Keys.Order(), options.EnvironmentVariables!.Keys.Order());
            Assert.DoesNotContain(ours, options.EnvironmentVariables.Keys);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ours, null);
        }
    }

    [Theory]
    [InlineData("sse")]
    [InlineData("auto")]
    public async Task A_legacy_sse_server_is_reached_and_every_request_carries_the_resolved_headers(string transport)
    {
        await using var server = await FakeSseMcpServer.StartAsync(Ping());
        await using var pool = new McpConnectionPool(Http(server.SseEndpoint, transport), Secret);

        var client = await pool.GetAsync("gw", CancellationToken.None);
        var result = await client.CallToolAsync("ping");

        Assert.Equal("pong", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.NotEmpty(server.Authorizations);
        Assert.All(server.Authorizations, a => Assert.Equal("Bearer s3cret", a));
    }

    [Theory]
    [InlineData("streamable-http")]
    [InlineData("auto")]
    public async Task A_streamable_http_server_is_reached_and_every_request_carries_the_resolved_headers(string transport)
    {
        await using var server = await FakeSseMcpServer.StartAsync(Ping());
        await using var pool = new McpConnectionPool(Http(server.Address, transport), Secret);

        var client = await pool.GetAsync("gw", CancellationToken.None);
        var result = await client.CallToolAsync("ping");

        Assert.Equal("pong", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.All(server.Authorizations, a => Assert.Equal("Bearer s3cret", a));
    }

    [Fact]
    public async Task Naming_streamable_http_for_an_sse_only_endpoint_is_unreachable()
    {
        await using var server = await FakeSseMcpServer.StartAsync(Ping());
        await using var pool = new McpConnectionPool(Http(server.SseEndpoint, "streamable-http"), Secret, connectTimeout: TimeSpan.FromSeconds(10));

        var e = await Assert.ThrowsAsync<McpUnavailableException>(() => pool.GetAsync("gw", CancellationToken.None));

        Assert.Equal("unreachable", e.Code);
    }

    /// <summary>A transport whose connection never completes.</summary>
    private sealed class HangingTransport : IClientTransport
    {
        public string Name => "hanging";

        public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }
    }
}
