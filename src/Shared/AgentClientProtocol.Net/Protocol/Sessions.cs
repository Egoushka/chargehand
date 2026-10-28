using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentClientProtocol;

public sealed record EnvVariable(string Name, string Value);

public sealed record HttpHeader(string Name, string Value);

/// <summary>An MCP server the agent should connect to. One shape for the three transports; build it with <see cref="Stdio"/>,
/// <see cref="Http"/> or <see cref="Sse"/>.</summary>
public sealed record McpServer
{
    public string Name { get; init; } = "";

    /// <summary><c>http</c> or <c>sse</c>; null for a stdio server, which the protocol leaves untyped.</summary>
    public string? Type { get; init; }

    public string? Command { get; init; }

    public IReadOnlyList<string>? Args { get; init; }

    public IReadOnlyList<EnvVariable>? Env { get; init; }

    public string? Url { get; init; }

    public IReadOnlyList<HttpHeader>? Headers { get; init; }

    public static McpServer Stdio(string name, string command, IReadOnlyList<string>? args = null, IReadOnlyList<EnvVariable>? env = null) =>
        new() { Name = name, Command = command, Args = args ?? [], Env = env ?? [] };

    public static McpServer Http(string name, string url, IReadOnlyList<HttpHeader>? headers = null) =>
        new() { Name = name, Type = "http", Url = url, Headers = headers ?? [] };

    public static McpServer Sse(string name, string url, IReadOnlyList<HttpHeader>? headers = null) =>
        new() { Name = name, Type = "sse", Url = url, Headers = headers ?? [] };
}

public sealed record SessionMode(string Id, string Name, string? Description = null);

public sealed record SessionModeState(string CurrentModeId, IReadOnlyList<SessionMode>? AvailableModes = null);

/// <summary>A model an agent offers. Part of the session-model proposal, which not every agent implements.</summary>
public sealed record ModelInfo(string ModelId, string Name, string? Description = null);

public sealed record SessionModelState(string CurrentModelId, IReadOnlyList<ModelInfo>? AvailableModels = null);

/// <summary>A setting an agent exposes for a session (model, mode, effort and the like).</summary>
/// <param name="Options">As the agent sent it: a list of <c>{value, name}</c>, or groups of them. <see cref="Values"/> flattens both.</param>
public sealed record ConfigOption(
    string Id,
    string Name,
    string? Description = null,
    string? Category = null,
    string? Type = null,
    JsonElement? CurrentValue = null,
    JsonElement? Options = null)
{
    /// <summary>The values a select option accepts, groups flattened.</summary>
    public IReadOnlyList<string> Values()
    {
        var values = new List<string>();
        if (Options is { ValueKind: JsonValueKind.Array } options)
            Collect(options, values);
        return values;
    }

    private static void Collect(JsonElement array, List<string> values)
    {
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;
            if (entry.TryGetProperty("options", out var group) && group.ValueKind == JsonValueKind.Array)
                Collect(group, values);
            else if (entry.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
                values.Add(value.GetString()!);
        }
    }
}

public sealed record NewSessionRequest(string Cwd, IReadOnlyList<McpServer> McpServers);

public sealed record NewSessionResult(
    string SessionId,
    SessionModeState? Modes = null,
    SessionModelState? Models = null,
    IReadOnlyList<ConfigOption>? ConfigOptions = null);

public sealed record LoadSessionRequest(string SessionId, string Cwd, IReadOnlyList<McpServer> McpServers);

public sealed record LoadSessionResult(
    SessionModeState? Modes = null,
    SessionModelState? Models = null,
    IReadOnlyList<ConfigOption>? ConfigOptions = null);

public sealed record SetModeRequest(string SessionId, string ModeId);

public sealed record SetModelRequest(string SessionId, string ModelId);

public sealed record SetConfigOptionRequest(string SessionId, string ConfigId, JsonElement Value);

public sealed record SetConfigOptionResult(IReadOnlyList<ConfigOption>? ConfigOptions = null);

public sealed record CancelNotification(string SessionId);

public sealed record PromptRequest(string SessionId, IReadOnlyList<ContentBlock> Prompt);

/// <summary>Token counts an agent reports for a turn.</summary>
public sealed record TokenUsage(
    long? InputTokens = null,
    long? OutputTokens = null,
    long? TotalTokens = null,
    long? ThoughtTokens = null,
    long? CachedReadTokens = null,
    long? CachedWriteTokens = null);

/// <param name="StopReason">One of <see cref="AcpStopReason"/>.</param>
/// <param name="Usage">Null when the agent reports no usage with the turn.</param>
public sealed record PromptResult(
    string StopReason,
    TokenUsage? Usage = null,
    [property: JsonPropertyName("_meta")] JsonElement? Meta = null);
