using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chargehand.Modules.Memory.Api;

/// <summary>
/// One memory provider of the profile's <c>memory</c> list (ADR 0034, spec decisions 5, 6 and 17): an MCP server named in
/// <c>mcp_servers</c>, the tools that give it recall (and retain, invalidate) and how their arguments and results map onto
/// <see cref="IMemoryProvider"/>. Loading the profile runs <see cref="MemoryMapping.Validate(MemoryProviderSettings, IEnumerable{string})"/>
/// on every entry it returns, so a reader may rely on a defined server, a recall tool, placeholders from the allowed sets
/// and, when <paramref name="Retain"/> is true, a retain tool. An entry built any other way carries no such promise.
/// </summary>
/// <param name="Name">Unique in the list, <c>[a-z][a-z0-9-]*</c>. The source's label in the prompt, in the chain block
/// <c>memory/recall/&lt;name&gt;</c> and in span tags.</param>
/// <param name="Server">A key of <c>mcp_servers</c>.</param>
/// <param name="Namespace">The value of <c>{namespace}</c> (a Hindsight bank). Null for a source with no banks; see <see cref="EffectiveNamespace"/>.</param>
/// <param name="MaxFacts">Facts kept per recall; null is the default of <see cref="MemoryLimits"/>. Also the value of <c>{max_facts}</c>.</param>
/// <param name="MaxChars">Characters kept per recall, all facts together; null is the default of <see cref="MemoryLimits"/>.</param>
/// <param name="MaxFactChars">Characters kept per fact, cut with an ellipsis; null is the default of <see cref="MemoryLimits"/>.</param>
/// <param name="TimeoutSeconds">How long one recall or retain call may take; null is the default of <see cref="MemoryLimits"/>.</param>
/// <param name="Retain">A completed run writes to this provider. Needs <c>Tools.Retain</c>; false (the default) for a recall-only source.</param>
/// <param name="RetainTags">Tags of a retained item; null is <see cref="DefaultRetainTags"/>.</param>
public sealed record MemoryProviderSettings(
    string Name,
    string Server,
    MemoryTools Tools,
    string? Namespace = null,
    int? MaxFacts = null,
    int? MaxChars = null,
    int? MaxFactChars = null,
    int? TimeoutSeconds = null,
    bool Retain = false,
    IReadOnlyList<string>? RetainTags = null)
{
    /// <summary>The tags of a retained item when the entry names none.</summary>
    public static readonly IReadOnlyList<string> DefaultRetainTags = ["chargehand"];

    /// <summary>The value of <c>{namespace}</c> and the namespace of the memory scope: the entry's own, else its name.</summary>
    public string EffectiveNamespace => Namespace ?? Name;

    /// <summary>The entry's caps and timeout, each falling back to the default of <see cref="MemoryLimits"/> (spec, decisions 6 and 17).</summary>
    public MemoryLimits Limits
    {
        get
        {
            var defaults = new MemoryLimits();
            return new MemoryLimits(MaxFacts ?? defaults.MaxFacts, MaxChars ?? defaults.MaxChars,
                TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null, MaxFactChars ?? defaults.MaxFactChars);
        }
    }

    public IReadOnlyList<string> EffectiveRetainTags => RetainTags ?? DefaultRetainTags;
}

/// <summary>The tools of one memory provider. <c>Recall</c> is required (a provider takes part in recall through it);
/// <c>Retain</c> is required when the entry retains; <c>Invalidate</c> is optional and no run calls it.</summary>
public sealed record MemoryTools(ToolCall Recall, ToolCall? Retain = null, ToolCall? Invalidate = null);

/// <summary>
/// One MCP tool call of a mapping: the tool's name and the template of its arguments, expanded per call. In the template a
/// string that is exactly one placeholder (<c>"{tags}"</c>) becomes the value with its own type (an array, an integer, an
/// ISO 8601 string), and a null value drops the argument; a placeholder inside longer text is replaced as text; numbers,
/// booleans and nulls are literal; objects and arrays are walked, and only their string values hold placeholders. The
/// placeholders each tool may use are <see cref="MemoryMapping.RecallPlaceholders"/>, <see cref="MemoryMapping.RetainPlaceholders"/>
/// and <see cref="MemoryMapping.InvalidatePlaceholders"/>.
/// </summary>
/// <param name="Results">Recall only (retain and invalidate answers are not read): how to read the tool's answer. Null means
/// <see cref="ResultMapping.Default"/>; see <see cref="EffectiveResults"/>.</param>
public sealed record ToolCall(string Tool, IReadOnlyDictionary<string, JsonElement> Arguments, ResultMapping? Results = null)
{
    public ResultMapping EffectiveResults => Results ?? ResultMapping.Default;
}

/// <summary>
/// How to read a recall tool's answer as facts. With <see cref="Format"/> <c>json</c>: the answer is the tool's
/// <c>structuredContent</c> if <see cref="Path"/> leads to an array in it, else its first text block parsed as JSON (a Python
/// MCP tool that returns <c>str</c> sends its text in both, wrapped as <c>{"result": "…"}</c> in the structured content);
/// <see cref="Path"/> leads from the root to the array of results; each element of the array is one candidate fact, whose
/// <see cref="Id"/> property is the fact's id (a missing one becomes the first 12 hex characters of the SHA-256 of the fact
/// text) and whose text is the first of <see cref="EffectiveText"/> that qualifies. With <see cref="FormatText"/> the whole first text block is one fact and the
/// other fields are unused. An error result, a path that does not lead to an array, or JSON that does not parse is a failure of
/// the provider (the stack skips it), never a fact.
/// </summary>
/// <param name="Path">Dotted property names leading to the array (<c>data.items</c>); null or empty means the root is the
/// array. A property name holds no dot and no index.</param>
/// <param name="Id">The element property holding the fact's id; default <c>id</c>.</param>
/// <param name="Text">Ordered entries, JSON: one string or an array of strings. An entry without a brace is a field name; an
/// entry with braces is a template in which <c>{field}</c> is replaced by that field's value. Fields are properties of the
/// element (any characters but braces; no path). An entry qualifies only if every field it names is present and non-empty
/// (a string with characters, or a number); the first that qualifies is the fact, and an element with none is skipped. Null
/// means <c>["text"]</c>; see <see cref="EffectiveText"/>.</param>
/// <param name="Format"><see cref="FormatJson"/> (default) or <see cref="FormatText"/>.</param>
public sealed record ResultMapping(
    string? Path = null,
    string Id = "id",
    [property: JsonConverter(typeof(StringOrListConverter))] IReadOnlyList<string>? Text = null,
    string Format = "json")
{
    public const string FormatJson = "json";
    public const string FormatText = "text";

    /// <summary>What a recall tool without a <c>results</c> block is read with: the shape Hindsight answers, <c>{"results":[{"id":…,"text":…}]}</c>.
    /// A <c>results</c> block that leaves out <c>path</c> reads the root instead.</summary>
    public static readonly ResultMapping Default = new(Path: "results");

    public IReadOnlyList<string> EffectiveText => Text ?? DefaultText;

    private static readonly string[] DefaultText = ["text"];
}

/// <summary>Reads a string as a list of one and an array of strings as is. Public so that a source-generated context in another assembly
/// (the profile's, ADR 0060) can construct it for <see cref="ResultMapping.Text"/>.</summary>
public sealed class StringOrListConverter : JsonConverter<IReadOnlyList<string>>
{
    public override IReadOnlyList<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return [reader.GetString()!];
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("expected a string or an array of strings");
        var items = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            items.Add(reader.TokenType == JsonTokenType.String ? reader.GetString()! : throw new JsonException("expected an array of strings"));
        return items;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
            writer.WriteStringValue(item);
        writer.WriteEndArray();
    }
}
