using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentClientProtocol;

/// <summary>A piece of prompt or response content. One flat shape for every <see cref="Type"/> (<c>text</c>, <c>image</c>,
/// <c>audio</c>, <c>resource_link</c>, <c>resource</c>), so a type an agent adds later still parses.</summary>
public sealed record ContentBlock
{
    public string Type { get; init; } = "";

    public string? Text { get; init; }

    /// <summary>Base64 data of an image or audio block.</summary>
    public string? Data { get; init; }

    public string? MimeType { get; init; }

    public string? Uri { get; init; }

    public string? Name { get; init; }

    /// <summary>The embedded resource of a <c>resource</c> block.</summary>
    public JsonElement? Resource { get; init; }

    [JsonPropertyName("_meta")]
    public JsonElement? Meta { get; init; }

    public static ContentBlock FromText(string text) => new() { Type = "text", Text = text };

    public static ContentBlock FromImage(string base64Data, string mimeType) => new() { Type = "image", Data = base64Data, MimeType = mimeType };
}

/// <summary>What a tool call produced: content, a diff or a terminal. One flat shape for every <see cref="Type"/>
/// (<c>content</c>, <c>diff</c>, <c>terminal</c>).</summary>
public sealed record ToolCallContent
{
    public string Type { get; init; } = "";

    public ContentBlock? Content { get; init; }

    /// <summary>The file a <c>diff</c> changes.</summary>
    public string? Path { get; init; }

    public string? OldText { get; init; }

    public string? NewText { get; init; }

    public string? TerminalId { get; init; }
}

/// <summary>A place in a file a tool call touched. <see cref="Line"/> is 1-based.</summary>
public sealed record ToolCallLocation(string Path, int? Line = null);
