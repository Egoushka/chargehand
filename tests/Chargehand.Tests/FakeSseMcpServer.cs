using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chargehand.Tests;

/// <summary>
/// The SDK's own MCP server over HTTP on a free loopback port: Streamable HTTP at the root and the legacy SSE transport
/// at /sse (what servers such as Chronicle speak), serving scripted tools. Records the calls and the Authorization
/// header of every request.
/// </summary>
internal sealed class FakeSseMcpServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeSseMcpServer(FakeTool[] tools)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
#pragma warning disable MCP9004 // legacy SSE is what this helper exists to serve
        builder.Services.AddMcpServer()
            .WithHttpTransport(o =>
            {
                o.Stateless = false;
                o.EnableLegacySse = true;
            })
            .WithTools([.. tools.Select(t => t.Build(Record))]);
#pragma warning restore MCP9004
        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            lock (Authorizations)
                Authorizations.Add(context.Request.Headers.Authorization.ToString());
            await next();
        });
        _app.MapMcp();
    }

    /// <summary>The server's root: where a Streamable HTTP client connects.</summary>
    public Uri Address { get; private set; } = null!;

    /// <summary>The URL a profile would give for a legacy SSE server: the /sse endpoint.</summary>
    public Uri SseEndpoint => new(Address, "/sse");

    /// <summary>Every tool call the server received, in order.</summary>
    public List<(string Tool, IDictionary<string, JsonElement> Arguments)> Calls { get; } = [];

    /// <summary>The Authorization header of every request, empty when it had none.</summary>
    public List<string> Authorizations { get; } = [];

    /// <summary>A server with one tool that answers every call with <paramref name="reply"/>, like Chronicle's <c>recall</c>.</summary>
    public static Task<FakeSseMcpServer> StartAsync(string toolName, string reply) =>
        StartAsync(new FakeTool(toolName, _ => FakeMcpServer.Text(reply)));

    public static async Task<FakeSseMcpServer> StartAsync(params FakeTool[] tools)
    {
        var server = new FakeSseMcpServer(tools);
        await server._app.StartAsync();
        server.Address = new Uri(server._app.Urls.First());
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private void Record(string tool, IDictionary<string, JsonElement> arguments)
    {
        lock (Calls)
            Calls.Add((tool, arguments));
    }
}
