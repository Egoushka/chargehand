using ModelContextProtocol.Protocol;

namespace Chargehand.Mcp;

/// <summary>
/// A memory server answered, but not with something the provider's mapping can use: the tool reported an error, the answer is
/// not JSON, or the results path leads nowhere (ADR 0034). <c>MemoryStack</c> skips the provider and keeps the message as the
/// reason, so it names the fault and never quotes a fact; the message passes through <see cref="ChargehandException.Scrub"/>.
/// </summary>
public sealed class McpMemoryException(string message) : Exception(ChargehandException.Scrub(message))
{
    private const int MaxErrorTextLength = 200;

    /// <summary>Throws when the tool reported a failure (<c>isError</c>); its first text block, cut, is the reason.</summary>
    internal static void ThrowIfError(CallToolResult result)
    {
        if (result.IsError != true)
            return;
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text.Trim();
        throw new McpMemoryException(string.IsNullOrEmpty(text)
            ? "the tool reported an error"
            : $"the tool reported an error: {(text.Length <= MaxErrorTextLength ? text : text[..MaxErrorTextLength])}");
    }
}
