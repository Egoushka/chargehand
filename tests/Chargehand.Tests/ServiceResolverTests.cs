using System.IO.Pipelines;
using Chargehand.Config;
using Chargehand.Mcp;
using Chargehand.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Chargehand.Tests;

/// <summary>Goal 0.6: a preset's services become grants; whatever does not resolve is dropped and reported, never fatal.</summary>
public class ServiceResolverTests
{
    private static string Secret(string item) => item == "docs-token" ? "s3cret" : throw new InvalidOperationException($"no secret source resolved '{item}'");

    private static readonly Dictionary<string, McpServerSettings> Servers = new()
    {
        ["team-docs"] = new(Url: "https://mcp.example.internal/docs", Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer {secret:docs-token}" }),
        ["no-token"] = new(Url: "https://mcp.example.internal/other", Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer {secret:absent}" }),
    };

    private static FakeMcpServer Docs(string searchDescription = "Search the docs") => new(
        new FakeTool("search_docs", _ => FakeMcpServer.Text("x"), searchDescription, ReadOnly: true),
        new FakeTool("read_doc", _ => FakeMcpServer.Text("x")),
        new FakeTool("write_note", _ => FakeMcpServer.Text("x")));

    private static McpConnectionPool PoolFor(FakeMcpServer docs, IReadOnlyDictionary<string, McpServerSettings>? servers = null) =>
        new(servers ?? Servers, Secret, async (name, _, _) => name is "team-docs" or "notes" ? await docs.TransportAsync() : throw new IOException("connection refused"));

    [Fact]
    public async Task Names_and_globs_expand_to_the_servers_tools()
    {
        await using var docs = Docs();
        await using var pool = PoolFor(docs);

        var resolved = await new ServiceResolver(pool).ResolveAsync([new ServiceUse("team-docs", ["search_*", "read_doc"])], CancellationToken.None);

        var grant = Assert.Single(resolved.Grants);
        Assert.Equal(["read_doc", "search_docs"], grant.Tools);
        Assert.Equal("Bearer s3cret", ((HttpServiceTransport)grant.Transport).Headers["Authorization"]);
        var report = Assert.Single(resolved.Report);
        Assert.Equal(["read_doc", "search_docs"], report.Tools);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public async Task A_requested_tool_the_server_lacks_is_reported_and_the_rest_granted()
    {
        await using var docs = Docs();
        await using var pool = PoolFor(docs);

        var resolved = await new ServiceResolver(pool).ResolveAsync([new ServiceUse("team-docs", ["search_docs", "missing_tool"])], CancellationToken.None);

        Assert.Equal(["search_docs"], Assert.Single(resolved.Grants).Tools);
        Assert.Equal(["tool_missing: missing_tool"], Assert.Single(resolved.Report).Issues);
    }

    [Fact]
    public async Task A_glob_that_matches_nothing_grants_nothing()
    {
        await using var docs = Docs();
        await using var pool = PoolFor(docs);

        var resolved = await new ServiceResolver(pool).ResolveAsync([new ServiceUse("team-docs", ["nope_*"])], CancellationToken.None);

        Assert.Empty(resolved.Grants);
        Assert.Equal(["tool_missing: nope_*"], Assert.Single(resolved.Report).Issues);
    }

    [Fact]
    public async Task Only_a_star_is_a_wildcard()
    {
        await using var docs = Docs();
        await using var pool = PoolFor(docs);

        var resolved = await new ServiceResolver(pool).ResolveAsync([new ServiceUse("team-docs", ["read.doc", "read_d?c"])], CancellationToken.None);

        Assert.Empty(resolved.Grants);
        Assert.Equal(["tool_missing: read.doc", "tool_missing: read_d?c"], Assert.Single(resolved.Report).Issues);
    }

    [Fact]
    public async Task An_unknown_server_is_dropped_with_an_issue()
    {
        await using var docs = Docs();
        await using var pool = PoolFor(docs);

        var resolved = await new ServiceResolver(pool).ResolveAsync([new ServiceUse("nowhere", ["a"])], CancellationToken.None);

        Assert.Empty(resolved.Grants);
        var report = Assert.Single(resolved.Report);
        Assert.Equal("nowhere", report.Server);
        Assert.Equal(["unknown_server: nowhere"], report.Issues);
    }

    [Fact]
    public async Task An_unresolved_secret_drops_the_service_and_names_the_item_only()
    {
        await using var docs = Docs();
        await using var pool = PoolFor(docs);

        var resolved = await new ServiceResolver(pool).ResolveAsync([new ServiceUse("no-token", ["a"])], CancellationToken.None);

        Assert.Empty(resolved.Grants);
        var issue = Assert.Single(Assert.Single(resolved.Report).Issues);
        Assert.StartsWith("secret_unresolved:", issue, StringComparison.Ordinal);
        Assert.Contains("absent", issue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_server_is_dropped_and_the_others_still_resolve()
    {
        var servers = new Dictionary<string, McpServerSettings>(Servers) { ["down"] = new(Url: "https://mcp.example.internal/down") };
        await using var docs = Docs();
        await using var pool = PoolFor(docs, servers);

        var resolved = await new ServiceResolver(pool).ResolveAsync([new ServiceUse("down", ["a"]), new ServiceUse("team-docs", ["read_doc"])], CancellationToken.None);

        Assert.Equal(["read_doc"], Assert.Single(resolved.Grants).Tools);
        Assert.StartsWith("unreachable:", Assert.Single(resolved.Report[0].Issues), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_grant_hash_follows_the_tool_descriptions()
    {
        await using var a = Docs("Search the docs");
        await using var b = Docs("Search the docs");
        await using var c = Docs("Search the docs, and the wiki");
        var use = new[] { new ServiceUse("team-docs", ["search_docs"]) };

        var (ha, hb, hc) = (await Hash(a, use), await Hash(b, use), await Hash(c, use));

        Assert.Equal(ha, hb);
        Assert.NotEqual(ha, hc);
    }

    [Fact]
    public async Task A_server_listed_twice_is_granted_once_with_the_union_of_its_tools()
    {
        await using var docs = Docs();
        await using var pool = PoolFor(docs);

        var resolved = await new ServiceResolver(pool).ResolveAsync(
            [new ServiceUse("team-docs", ["read_doc"]), new ServiceUse("team-docs", ["search_docs", "read_doc"])], CancellationToken.None);

        Assert.Equal(["read_doc", "search_docs"], Assert.Single(resolved.Grants).Tools);
        Assert.Single(resolved.Report);
    }

    [Theory]
    [InlineData(null, HttpServiceProtocol.StreamableHttp)]
    [InlineData(McpServerSettings.TransportAuto, HttpServiceProtocol.StreamableHttp)]
    [InlineData(McpServerSettings.TransportStreamableHttp, HttpServiceProtocol.StreamableHttp)]
    [InlineData(McpServerSettings.TransportSse, HttpServiceProtocol.Sse)]
    public async Task A_url_grant_carries_the_protocol_the_workers_runtime_needs(string? transport, HttpServiceProtocol expected)
    {
        var servers = new Dictionary<string, McpServerSettings> { ["team-docs"] = new(Url: "https://mcp.example.internal/docs", Transport: transport) };
        await using var docs = Docs();
        await using var pool = PoolFor(docs, servers);

        var resolved = await new ServiceResolver(pool).ResolveAsync([new ServiceUse("team-docs", ["read_doc"])], CancellationToken.None);

        var http = Assert.IsType<HttpServiceTransport>(Assert.Single(resolved.Grants).Transport);
        Assert.Equal(new Uri("https://mcp.example.internal/docs"), http.Url);
        Assert.Equal(expected, http.Protocol);
        Assert.Empty(http.Headers);
    }

    [Fact]
    public async Task A_stdio_grant_carries_the_command_and_only_the_declared_environment()
    {
        var servers = new Dictionary<string, McpServerSettings>
        {
            ["notes"] = new(Command: ["npx", "-y", "example-notes-mcp"], Env: new Dictionary<string, string> { ["NOTES_TOKEN"] = "{secret:docs-token}", ["MODE"] = "ro" }),
        };
        await using var docs = Docs();
        await using var pool = PoolFor(docs, servers);

        var resolved = await new ServiceResolver(pool).ResolveAsync([new ServiceUse("notes", ["read_doc"])], CancellationToken.None);

        var stdio = Assert.IsType<StdioServiceTransport>(Assert.Single(resolved.Grants).Transport);
        Assert.Equal(["npx", "-y", "example-notes-mcp"], stdio.Command);
        Assert.Equal(new Dictionary<string, string> { ["NOTES_TOKEN"] = "s3cret", ["MODE"] = "ro" }, stdio.Env);
    }

    [Fact]
    public async Task A_server_that_never_lists_its_tools_is_dropped_at_the_timeout()
    {
        await using var hanging = new HangingServer();
        await using var pool = new McpConnectionPool(Servers, Secret, async (_, _, _) => await hanging.TransportAsync());

        var resolved = await new ServiceResolver(pool, TimeSpan.FromMilliseconds(300)).ResolveAsync([new ServiceUse("team-docs", ["read_doc"])], CancellationToken.None);

        Assert.Empty(resolved.Grants);
        Assert.Equal(["unreachable: no answer within 0.3 s"], Assert.Single(resolved.Report).Issues);
    }

    [Fact]
    public async Task The_runs_own_cancellation_is_not_a_service_failure()
    {
        await using var hanging = new HangingServer();
        await using var pool = new McpConnectionPool(Servers, Secret, async (_, _, _) => await hanging.TransportAsync());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ServiceResolver(pool, TimeSpan.FromMinutes(1)).ResolveAsync([new ServiceUse("team-docs", ["read_doc"])], cts.Token));
    }

    [Fact]
    public async Task A_transport_is_built_without_connecting()
    {
        var connected = 0;
        await using var pool = new McpConnectionPool(Servers, Secret, (_, _, _) =>
        {
            connected++;
            throw new IOException("must not connect");
        });

        var transport = Assert.IsType<HttpServiceTransport>(pool.Transport("team-docs"));

        Assert.Equal("Bearer s3cret", transport.Headers["Authorization"]);
        Assert.Equal(0, connected);
    }

    private static async Task<string> Hash(FakeMcpServer docs, ServiceUse[] use)
    {
        await using var pool = PoolFor(docs);
        return Assert.Single((await new ServiceResolver(pool).ResolveAsync(use, CancellationToken.None)).Grants).Sha256;
    }

    /// <summary>An MCP server that completes the handshake and then never answers a tools/list.</summary>
    private sealed class HangingServer : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly Pipe _toServer = new();
        private readonly Pipe _toClient = new();

        public HangingServer()
        {
            var builder = Host.CreateEmptyApplicationBuilder(new());
            builder.Services.AddMcpServer()
                .WithStreamServerTransport(_toServer.Reader.AsStream(), _toClient.Writer.AsStream())
                .WithListToolsHandler(async (_, ct) =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return new ListToolsResult();
                })
                .WithCallToolHandler((_, _) => ValueTask.FromResult(new CallToolResult()));
            _host = builder.Build();
        }

        public async Task<IClientTransport> TransportAsync()
        {
            await _host.StartAsync();
            return new StreamClientTransport(_toServer.Writer.AsStream(), _toClient.Reader.AsStream());
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
