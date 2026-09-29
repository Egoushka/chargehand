using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Chargehand.Tests;

/// <param name="Schema">The tool's input schema as JSON text; none: an empty object schema.</param>
/// <param name="ReadOnly">The tool's readOnlyHint; null leaves the hint out.</param>
internal sealed record FakeTool(string Name, Func<IDictionary<string, JsonElement>, CallToolResult> Handle, string? Description = null, string? Schema = null, bool? ReadOnly = null)
{
    /// <summary>The SDK tool that runs <see cref="Handle"/> and reports each call to <paramref name="record"/> first.</summary>
    internal McpServerTool Build(Action<string, IDictionary<string, JsonElement>> record)
    {
        var tool = McpServerTool.Create((RequestContext<CallToolRequestParams> ctx) =>
        {
            var arguments = ctx.Params!.Arguments ?? new Dictionary<string, JsonElement>();
            record(Name, arguments);
            return Handle(arguments);
        }, new McpServerToolCreateOptions { Name = Name, Description = Description ?? Name });
        if (Schema is not null)
            tool.ProtocolTool.InputSchema = JsonDocument.Parse(Schema).RootElement.Clone();
        if (ReadOnly is { } ro)
            tool.ProtocolTool.Annotations = new ToolAnnotations { ReadOnlyHint = ro };
        return tool;
    }
}

/// <summary>A real MCP server on two in-process pipes (the shape StdioMcpTests uses), serving scripted tools and recording calls.</summary>
internal sealed class FakeMcpServer : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly Pipe _toServer = new();
    private readonly Pipe _toClient = new();

    public FakeMcpServer(params FakeTool[] tools)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new());
        builder.Services.AddMcpServer()
            .WithStreamServerTransport(_toServer.Reader.AsStream(), _toClient.Writer.AsStream())
            .WithTools([.. tools.Select(t => t.Build(Record))]);
        _host = builder.Build();
    }

    /// <summary>Every call the server received, in order.</summary>
    public List<(string Tool, IDictionary<string, JsonElement> Arguments)> Calls { get; } = [];

    public static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };

    public static CallToolResult Error(string text) => new() { IsError = true, Content = [new TextContentBlock { Text = text }] };

    /// <summary>Starts the server and returns the client end of its pipes.</summary>
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

    private void Record(string tool, IDictionary<string, JsonElement> arguments)
    {
        lock (Calls)
            Calls.Add((tool, arguments));
    }
}
