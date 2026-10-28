using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentClientProtocol;

/// <summary>Name and version of a client or an agent, as each reports itself.</summary>
public sealed record Implementation(string Name, string? Title = null, string? Version = null);

public sealed record FileSystemCapabilities(bool ReadTextFile, bool WriteTextFile);

/// <summary>What this client offers the agent. The client sets it from the callbacks it was given: it never claims a method it would decline.</summary>
public sealed record ClientCapabilities(FileSystemCapabilities? Fs = null, bool? Terminal = null);

public sealed record InitializeRequest(int ProtocolVersion, ClientCapabilities ClientCapabilities, Implementation? ClientInfo = null);

public sealed record PromptCapabilities(bool Image = false, bool Audio = false, bool EmbeddedContext = false);

public sealed record McpCapabilities(bool Http = false, bool Sse = false);

/// <param name="SessionCapabilities">As the agent sent it: the list, resume, fork and close capabilities are still moving.</param>
public sealed record AgentCapabilities(
    bool LoadSession = false,
    PromptCapabilities? PromptCapabilities = null,
    McpCapabilities? McpCapabilities = null,
    JsonElement? SessionCapabilities = null,
    [property: JsonPropertyName("_meta")] JsonElement? Meta = null);

/// <param name="Id">The value to give <see cref="AcpClient.AuthenticateAsync"/>.</param>
public sealed record AuthMethod(string Id, string Name, string? Description = null);

public sealed record InitializeResult(
    int ProtocolVersion,
    AgentCapabilities? AgentCapabilities = null,
    Implementation? AgentInfo = null,
    IReadOnlyList<AuthMethod>? AuthMethods = null);

public sealed record AuthenticateRequest(string MethodId);

/// <summary>The empty result of a call that returns nothing.</summary>
public sealed record EmptyResult;
