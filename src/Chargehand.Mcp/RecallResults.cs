using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.Config;
using Chargehand.Memory;
using ModelContextProtocol.Protocol;

namespace Chargehand.Mcp;

/// <summary>Reads a recall tool's answer as facts, the way its <see cref="ResultMapping"/> says.</summary>
public static partial class RecallResults
{
    private const int IdHashLength = 12;

    /// <summary>
    /// The facts in <paramref name="result"/>. With the <c>json</c> format the array at the mapping's path is read from the
    /// structured content and, when the path leads to no array there, from the first text block parsed as JSON (FastMCP puts a
    /// tool's <c>str</c> answer in both, wrapped as <c>{"result": "…"}</c> in the structured content). Each object of the array is a
    /// fact if one of the mapping's text entries qualifies; anything else in it is skipped. With the <c>text</c> format the first
    /// text block is one fact.
    /// </summary>
    /// <param name="mapping">Null reads <see cref="ResultMapping.Default"/>.</param>
    /// <exception cref="McpMemoryException">The tool reported an error, no answer leads to an array at the path, or the text is not JSON.</exception>
    public static IReadOnlyList<RecalledMemory> Read(CallToolResult result, ResultMapping? mapping)
    {
        mapping ??= ResultMapping.Default;
        McpMemoryException.ThrowIfError(result);
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        if (mapping.Format == ResultMapping.FormatText)
            return text is null ? throw new McpMemoryException("the answer has no text block") : text.Length == 0 ? [] : [new RecalledMemory(Hash(text), text)];

        if (result.StructuredContent is { } structured && ArrayAt(structured, mapping.Path) is { } inStructured)
            return Facts(inStructured, mapping);
        if (text is null)
            throw new McpMemoryException(result.StructuredContent is null
                ? "the answer has no text block and no structured content"
                : $"the results path '{mapping.Path}' leads to no array in the structured content, and there is no text block");
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(text);
        }
        catch (JsonException)
        {
            throw new McpMemoryException("the answer is not JSON");
        }
        return ArrayAt(root, mapping.Path) is { } items
            ? Facts(items, mapping)
            : throw new McpMemoryException($"the results path '{mapping.Path}' leads to no array in the answer");
    }

    /// <summary>The array at the dotted path (the root when the path is empty), or null when the path leads anywhere else.</summary>
    private static JsonElement? ArrayAt(JsonElement root, string? path)
    {
        var node = root;
        foreach (var name in string.IsNullOrEmpty(path) ? [] : path.Split('.'))
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(name, out node))
                return null;
        return node.ValueKind == JsonValueKind.Array ? node : null;
    }

    private static List<RecalledMemory> Facts(JsonElement items, ResultMapping mapping)
    {
        var facts = new List<RecalledMemory>();
        foreach (var item in items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
        {
            var text = mapping.EffectiveText.Select(entry => Render(entry, item)).FirstOrDefault(t => t is not null);
            if (text is not null)
                facts.Add(new RecalledMemory(Field(item, mapping.Id) ?? Hash(text), text));
        }
        return facts;
    }

    /// <summary>A field name is that field; an entry with braces is the entry with each <c>{field}</c> filled in. Null when a field it names is missing or empty.</summary>
    private static string? Render(string entry, JsonElement item)
    {
        if (!entry.Contains('{', StringComparison.Ordinal))
            return Field(item, entry);
        var complete = true;
        var text = Braced().Replace(entry, m =>
        {
            var value = Field(item, m.Groups[1].Value);
            complete &= value is not null;
            return value ?? "";
        });
        return complete ? text : null;
    }

    /// <summary>A property's text: a string with characters, or a number as JSON writes it. Null for anything else, absent included.
    /// A name with dots that is no property of its own walks nested objects (<c>metadata.commit</c>).</summary>
    private static string? Field(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) && !TryWalk(item, name, out value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() is { Length: > 0 } text ? text : null,
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static bool TryWalk(JsonElement item, string path, out JsonElement value)
    {
        value = item;
        foreach (var segment in path.Split('.'))
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
                return false;
        return true;
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..IdHashLength];

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex Braced();
}
