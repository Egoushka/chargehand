using System.Collections.Frozen;
using System.Text.Json;

namespace Chargehand.Config;

/// <summary>
/// The rules a memory provider's tool mapping must meet before a run reads it (ADR 0034). <see cref="Profile.Load"/> applies
/// them and throws the problems as one error; <c>chargehand extensions check</c> reuses them. A problem names the entry and the
/// fault and never quotes an argument value, a URL or argv: any of them may hold a credential.
/// </summary>
public static class MemoryMapping
{
    /// <summary>What a recall tool's arguments may use: the request text, the entry's namespace, the entry's fact cap (an integer).</summary>
    public static readonly IReadOnlySet<string> RecallPlaceholders = new[] { "query", "namespace", "max_facts" }.ToFrozenSet();

    /// <summary>What a retain tool's arguments may use. <c>timestamp</c> is ISO 8601, <c>tags</c> an array, and a null
    /// <c>context</c>, <c>document_id</c> or <c>timestamp</c> drops its argument. <c>repository</c>, <c>commit</c> (12 hex) and
    /// <c>locators</c> (joined with <c>"; "</c>) say where the item's citations were checked, for a memory that keeps
    /// metadata beside the text; they are null, and drop their argument, for an item without provenance.</summary>
    public static readonly IReadOnlySet<string> RetainPlaceholders =
        new[] { "namespace", "text", "context", "document_id", "timestamp", "tags", "repository", "commit", "locators" }.ToFrozenSet();

    /// <summary>What an invalidate tool's arguments may use.</summary>
    public static readonly IReadOnlySet<string> InvalidatePlaceholders = new[] { "namespace", "id", "reason" }.ToFrozenSet();

    /// <summary>The problems in the list as a whole: each entry's own (see <see cref="Validate(MemoryProviderSettings, IReadOnlyDictionary{string, McpServerSettings})"/>)
    /// and a name used twice. Empty when the list is valid.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<MemoryProviderSettings> providers, IReadOnlyDictionary<string, McpServerSettings> servers)
    {
        var problems = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            problems.AddRange(Validate(provider, servers));
            if (provider.Name is not null && !names.Add(provider.Name))
                problems.Add($"{Who(provider)}: duplicate name; every memory needs its own");
        }
        return problems;
    }

    /// <summary>The problems in one entry, one string per fault: an unknown server (the message names the key and the defined
    /// ones), a bad name, a missing tool, a placeholder the tool does not offer or a malformed brace, a results mapping that
    /// cannot work, and limits below 1. Empty when the entry is valid. A name used twice is a fault of the list, not of an entry.</summary>
    public static IReadOnlyList<string> Validate(MemoryProviderSettings provider, IReadOnlyDictionary<string, McpServerSettings> servers)
    {
        var problems = new List<string>();
        var who = Who(provider);
        void Fail(string reason) => problems.Add($"{who}: {reason}");

        if (!McpServerSettings.ValidName(provider.Name ?? ""))
            Fail("the name must be lowercase letters, digits and hyphens, starting with a letter");
        if (string.IsNullOrEmpty(provider.Server))
            Fail("server is required");
        else if (!servers.ContainsKey(provider.Server))
            Fail($"server '{provider.Server}' is not defined in mcp_servers (defined: {(servers.Count == 0 ? "none" : string.Join(", ", servers.Keys.Order(StringComparer.Ordinal)))})");
        if (provider.Namespace is { Length: 0 })
            Fail("namespace must not be empty; leave it out to use the name");
        foreach (var (field, value) in new[] { ("max_facts", provider.MaxFacts), ("max_chars", provider.MaxChars), ("max_fact_chars", provider.MaxFactChars), ("timeout_seconds", provider.TimeoutSeconds) })
            if (value < 1)
                Fail($"{field} must be at least 1");

        if (provider.Tools is not { } tools)
            Fail("tools is required");
        else
        {
            if (tools.Recall is null)
                Fail("tools.recall is required: a provider takes part in recall through it");
            else
                CheckCall("recall", tools.Recall, RecallPlaceholders, Fail);
            if (tools.Retain is { } retain)
                CheckCall("retain", retain, RetainPlaceholders, Fail);
            if (tools.Invalidate is { } invalidate)
                CheckCall("invalidate", invalidate, InvalidatePlaceholders, Fail);
            if (provider.Retain && tools.Retain is null)
                Fail("retain is true but tools.retain is not set; a provider without a retain tool is recall-only and retain must stay false");
        }
        return problems;
    }

    private static string Who(MemoryProviderSettings provider) => string.IsNullOrEmpty(provider.Name) ? "memory entry" : $"memory.{provider.Name}";

    private static void CheckCall(string kind, ToolCall call, IReadOnlySet<string> offered, Action<string> fail)
    {
        if (string.IsNullOrWhiteSpace(call.Tool))
            fail($"{kind}.tool must name the MCP tool to call");
        if (call.Arguments is null)
            fail($"{kind}.arguments is required; write {{}} for none");
        else
            foreach (var (name, value) in call.Arguments)
                CheckArgument(kind, name, value, offered, fail);
        if (kind == "recall" && call.Results is { } results)
            CheckResults(results, fail);
    }

    /// <summary>Walks a template value: only string values hold placeholders, at any depth.</summary>
    private static void CheckArgument(string kind, string path, JsonElement value, IReadOnlySet<string> offered, Action<string> fail)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                foreach (var name in Braces(value.GetString()!))
                {
                    if (name is null || !IsIdentifier(name))
                        fail($"{kind} argument '{path}' has an unbalanced or empty brace, or braces around something that is no placeholder");
                    else if (!offered.Contains(name))
                        fail($"{kind} argument '{path}' uses {{{name}}}, which {kind} does not offer (offered: {string.Join(", ", offered.Order(StringComparer.Ordinal).Select(o => $"{{{o}}}"))})");
                }
                break;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                    CheckArgument(kind, $"{path}.{property.Name}", property.Value, offered, fail);
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in value.EnumerateArray())
                    CheckArgument(kind, $"{path}[{index++}]", item, offered, fail);
                break;
        }
    }

    private static void CheckResults(ResultMapping results, Action<string> fail)
    {
        if (results.Format is not (ResultMapping.FormatJson or ResultMapping.FormatText))
            fail($"results.format must be {ResultMapping.FormatJson} or {ResultMapping.FormatText}");
        if (string.IsNullOrEmpty(results.Id))
            fail("results.id must name the field holding the id");
        if (!string.IsNullOrEmpty(results.Path) && results.Path.Split('.').Any(segment => segment.Length == 0))
            fail("results.path must be property names joined by single dots");
        if (results.Text is not { } text)
            return;
        if (text.Count == 0)
            fail("results.text needs at least one entry");
        foreach (var entry in text)
            if (string.IsNullOrEmpty(entry))
                fail("results.text must not hold an empty entry");
            else if (Braces(entry).Any(field => field is null))
                fail("results.text has an entry with an unbalanced or empty brace");
    }

    /// <summary>The names in the <c>{name}</c> groups of the text, in order, and a null for each brace that belongs to no
    /// group: an unclosed or stray one, an empty pair, or a pair holding another opening brace.</summary>
    private static IEnumerable<string?> Braces(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '}')
                yield return null;
            if (text[i] != '{')
                continue;
            var close = text.IndexOf('}', i + 1);
            if (close < 0)
            {
                yield return null;
                yield break;
            }
            var inner = text[(i + 1)..close];
            if (inner.Length == 0 || inner.Contains('{', StringComparison.Ordinal))
                yield return null;
            else
            {
                yield return inner;
                i = close;
            }
        }
    }

    /// <summary>Placeholders are names, so a message may quote them; anything else between braces might be a value.</summary>
    private static bool IsIdentifier(string name) => (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
