using System.Text.Json;

namespace Chargehand.Tests;

/// <summary>
/// Finds changes between two versions of a JSON Schema that can reject an instance the old version accepted.
/// Conservative and syntactic: it walks properties, $defs, items, additionalProperties and the combinators by
/// position, and flags any keyword it cannot prove looser.
/// </summary>
internal static class SchemaCompat
{
    private static readonly string[] LowerBounds = ["minimum", "exclusiveMinimum", "minLength", "minItems", "minProperties"];
    private static readonly string[] UpperBounds = ["maximum", "exclusiveMaximum", "maxLength", "maxItems", "maxProperties"];
    private static readonly JsonElement True = JsonDocument.Parse("true").RootElement;
    private static readonly string[] Fixed = ["$id", "$ref", "const", "pattern", "format", "if", "not"];

    public static IReadOnlyList<string> Breaks(JsonElement before, JsonElement after)
    {
        var breaks = new List<string>();
        Walk("#", before, after, breaks);
        return breaks;
    }

    private static void Walk(string path, JsonElement before, JsonElement after, List<string> breaks)
    {
        if (before.ValueKind != JsonValueKind.Object || after.ValueKind != JsonValueKind.Object)
        {
            // Boolean schemas: anything -> true loosens, false -> anything loosens, the rest narrows.
            if (after.ValueKind != JsonValueKind.True && before.ValueKind != JsonValueKind.False && !JsonElement.DeepEquals(before, after))
                breaks.Add($"{path}: schema changed from {before.GetRawText()} to {after.GetRawText()}");
            return;
        }

        foreach (var key in Fixed)
        {
            var had = before.TryGetProperty(key, out var b);
            if (after.TryGetProperty(key, out var a) && (!had || !JsonElement.DeepEquals(a, b)))
                breaks.Add($"{path}: {key} {(had ? "changed" : "added")}");
            else if (had && key == "$id" && !after.TryGetProperty(key, out _))
                breaks.Add($"{path}: {key} removed");
        }

        if (after.TryGetProperty("type", out var afterType))
        {
            var afterTypes = Strings(afterType);
            if (!before.TryGetProperty("type", out var beforeType))
                breaks.Add($"{path}: type added");
            else
                foreach (var t in Strings(beforeType).Except(afterTypes))
                    breaks.Add($"{path}: type {t} removed");
        }

        if (after.TryGetProperty("enum", out var afterEnum))
        {
            if (!before.TryGetProperty("enum", out var beforeEnum))
                breaks.Add($"{path}: enum added");
            else
                foreach (var v in beforeEnum.EnumerateArray().Where(v => !afterEnum.EnumerateArray().Any(w => JsonElement.DeepEquals(v, w))))
                    breaks.Add($"{path}: enum value {v.GetRawText()} removed");
        }

        var required = before.TryGetProperty("required", out var r) ? Strings(r) : [];
        if (after.TryGetProperty("required", out var ar))
            foreach (var name in Strings(ar).Except(required))
                breaks.Add($"{path}: {name} newly required");

        foreach (var key in LowerBounds)
            if (after.TryGetProperty(key, out var a) && (!before.TryGetProperty(key, out var b) || a.GetDouble() > b.GetDouble()))
                breaks.Add($"{path}: {key} tightened to {a.GetRawText()}");
        foreach (var key in UpperBounds)
            if (after.TryGetProperty(key, out var a) && (!before.TryGetProperty(key, out var b) || a.GetDouble() < b.GetDouble()))
                breaks.Add($"{path}: {key} tightened to {a.GetRawText()}");
        if (after.TryGetProperty("uniqueItems", out var u) && u.ValueKind == JsonValueKind.True
            && !(before.TryGetProperty("uniqueItems", out var bu) && bu.ValueKind == JsonValueKind.True))
            breaks.Add($"{path}: uniqueItems added");

        // Absent additionalProperties means true.
        if (after.TryGetProperty("additionalProperties", out var aap))
            Walk($"{path}/additionalProperties", before.TryGetProperty("additionalProperties", out var bap) ? bap : True, aap, breaks);

        foreach (var key in new[] { "properties", "$defs", "definitions", "patternProperties" })
        {
            if (!before.TryGetProperty(key, out var bm))
                continue;
            var am = after.TryGetProperty(key, out var x) ? x : default;
            foreach (var p in bm.EnumerateObject())
            {
                if (am.ValueKind == JsonValueKind.Object && am.TryGetProperty(p.Name, out var ap))
                    Walk($"{path}/{key}/{p.Name}", p.Value, ap, breaks);
                else
                    breaks.Add($"{path}/{key}/{p.Name}: removed");
            }
        }

        foreach (var key in new[] { "items", "then", "else", "contains" })
            if (before.TryGetProperty(key, out var b) | after.TryGetProperty(key, out var a))
                Walk($"{path}/{key}", b.ValueKind == JsonValueKind.Undefined ? True : b, a.ValueKind == JsonValueKind.Undefined ? True : a, breaks);

        // allOf: every branch must stay as loose, new branches tighten. anyOf/oneOf: branches walked by position,
        // a removed branch narrows.
        foreach (var key in new[] { "allOf", "anyOf", "oneOf" })
        {
            var bs = before.TryGetProperty(key, out var b) ? b.EnumerateArray().ToList() : [];
            var az = after.TryGetProperty(key, out var a) ? a.EnumerateArray().ToList() : [];
            if (key == "allOf" ? az.Count > bs.Count : az.Count < bs.Count)
                breaks.Add($"{path}/{key}: branch count changed from {bs.Count} to {az.Count}");
            for (var i = 0; i < Math.Min(bs.Count, az.Count); i++)
                Walk($"{path}/{key}/{i}", bs[i], az[i], breaks);
        }
    }

    private static List<string> Strings(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? [.. e.EnumerateArray().Select(v => v.GetString()!)] : [e.GetString()!];
}
