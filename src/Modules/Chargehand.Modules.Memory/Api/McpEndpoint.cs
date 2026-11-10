namespace Chargehand.Modules.Memory.Api;

/// <summary>
/// How to reach a profile's MCP server from outside this pool, with every <c>{secret:item}</c> replaced (ADR 0034): either a
/// <paramref name="Url"/> or a <paramref name="Command"/>. <paramref name="Values"/> are the headers of a URL server or the
/// environment of a command. <c>ToString</c> hides the values, so an endpoint can be logged.
/// </summary>
/// <param name="Sse">The URL speaks legacy SSE rather than Streamable HTTP.</param>
public sealed record McpEndpoint(Uri? Url, bool Sse, IReadOnlyList<string>? Command, IReadOnlyDictionary<string, string> Values)
{
    public override string ToString() => $"{nameof(McpEndpoint)} {{ Sse = {Sse}, Values = [{string.Join(", ", Values.Keys)}] }}";
}
