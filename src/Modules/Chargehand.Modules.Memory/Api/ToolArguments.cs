using System.Collections;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chargehand.Modules.Memory.Api;

/// <summary>Expands the argument template of a mapped tool call (<see cref="ToolCall"/>) into the arguments of one MCP call.</summary>
public static partial class ToolArguments
{
    /// <summary>
    /// A string that is exactly one <c>{name}</c> becomes that value with its own type, and a null value drops the argument (also
    /// inside an object or an array); a <c>{name}</c> inside longer text is replaced by the value written as JSON writes it
    /// (a null as nothing); numbers, booleans and nulls are copied; objects and arrays are walked.
    /// </summary>
    /// <exception cref="McpMemoryException">A placeholder that <paramref name="values"/> does not offer. <see cref="MemoryMapping"/> refuses these at load.</exception>
    public static Dictionary<string, object?> Expand(IReadOnlyDictionary<string, JsonElement> template, IReadOnlyDictionary<string, object?> values)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, node) in template)
            if (TryExpand(node, values, out var expanded))
                arguments[name] = expanded;
        return arguments;
    }

    /// <returns>False when the node is a placeholder whose value is null: the caller leaves it out.</returns>
    private static bool TryExpand(JsonElement node, IReadOnlyDictionary<string, object?> values, out object? expanded)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.String:
                var text = node.GetString()!;
                if (Whole().Match(text) is { Success: true } whole)
                {
                    expanded = Value(values, whole.Groups[1].Value);
                    return expanded is not null;
                }
                expanded = Placeholder().Replace(text, m => Format(Value(values, m.Groups[1].Value)));
                return true;
            case JsonValueKind.Object:
                var members = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in node.EnumerateObject())
                    if (TryExpand(property.Value, values, out var member))
                        members[property.Name] = member;
                expanded = members;
                return true;
            case JsonValueKind.Array:
                var items = new List<object?>();
                foreach (var element in node.EnumerateArray())
                    if (TryExpand(element, values, out var item))
                        items.Add(item);
                expanded = items;
                return true;
            default:
                expanded = node;
                return true;
        }
    }

    private static object? Value(IReadOnlyDictionary<string, object?> values, string name) =>
        values.TryGetValue(name, out var value) ? value : throw new McpMemoryException("an argument uses a placeholder this call does not offer");

    private static string Format(object? value)
    {
        switch (value)
        {
            case null:
                return "";
            case string text:
                return text;
            case JsonElement { ValueKind: JsonValueKind.String } element:
                return element.GetString()!;
        }
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteValue(writer, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Writes the values a call offers (strings, numbers, booleans, lists of them, JSON as given) without reflection.</summary>
    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case string text: writer.WriteStringValue(text); break;
            case bool flag: writer.WriteBooleanValue(flag); break;
            case int number: writer.WriteNumberValue(number); break;
            case long number: writer.WriteNumberValue(number); break;
            case double number: writer.WriteNumberValue(number); break;
            case decimal number: writer.WriteNumberValue(number); break;
            case JsonElement element: element.WriteTo(writer); break;
            case IEnumerable items:
                writer.WriteStartArray();
                foreach (var item in items)
                    WriteValue(writer, item);
                writer.WriteEndArray();
                break;
            default: throw new McpMemoryException($"a call value of type {value.GetType().Name} cannot be written into an argument");
        }
    }

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\A\{([A-Za-z_][A-Za-z0-9_]*)\}\z")]
    private static partial Regex Whole();
}
