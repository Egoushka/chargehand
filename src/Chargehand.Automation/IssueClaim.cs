using System.Globalization;
using System.Text.Json;

namespace Chargehand.Automation;

/// <summary>A claim's holder and expiry as the ref's object records them: a JSON object, or <c>key: value</c> lines, in a blob or in a commit's or tag's message. Null when either is missing or unreadable.</summary>
public sealed record IssueClaim(string Holder, DateTimeOffset ExpiresAt, DateTimeOffset? At)
{
    public static IssueClaim? Parse(string text, string objectType)
    {
        if (objectType is "commit" or "tag" && text.IndexOf("\n\n", StringComparison.Ordinal) is var cut and >= 0)
            text = text[(cut + 2)..];
        var fields = Fields(text.Trim());
        var holder = First(fields, "holder", "session", "run");
        var expires = Time(First(fields, "expires_at", "expires", "expiry"));
        return string.IsNullOrWhiteSpace(holder) || expires is not { } until ? null : new IssueClaim(holder, until, Time(First(fields, "claimed_at", "at", "since")));
    }

    private static Dictionary<string, string> Fields(string text)
    {
        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
        if (text.StartsWith('{'))
        {
            try
            {
                using var json = JsonDocument.Parse(text);
                if (json.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var p in json.RootElement.EnumerateObject().Where(p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number))
                        fields[p.Name] = p.Value.ToString();
            }
            catch (JsonException)
            {
                // Unreadable: no fields, so no claim.
            }
            return fields;
        }
        foreach (var line in text.Split('\n'))
            if (line.IndexOfAny([':', '=']) is var i and > 0)
                fields[line[..i].Trim()] = line[(i + 1)..].Trim();
        return fields;
    }

    private static string? First(Dictionary<string, string> fields, params string[] names) => names.Select(n => fields.GetValueOrDefault(n)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static DateTimeOffset? Time(string? text) =>
        text is null ? null : long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) ? DateTimeOffset.FromUnixTimeSeconds(epoch)
        : DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
}
