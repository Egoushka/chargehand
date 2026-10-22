using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Chargehand.Automation;

/// <summary>A decision an approvals server posted back (docs/guide/phone-answers.md): <c>{decision, id, action, edited, at, sig}</c>, where
/// <c>sig = HMAC-SHA256(key, decision|id|action|at|h)</c> in lower-case hex and <c>h</c> is the SHA-256 hex of <c>edited</c> as JSON with its keys sorted, or empty when nothing was edited.</summary>
/// <param name="Decision">The decision's own id; a retry of the same decision carries the same id.</param>
/// <param name="Id">The item id: <see cref="ApprovalsSink.ItemId"/>.</param>
/// <param name="Edited">The editable fields the person changed, by path (<c>body.answer</c>).</param>
/// <param name="AtText"><see cref="At"/> as it was sent: the signature covers the text.</param>
public sealed record ApprovalDecision(string Decision, string Id, string Action, IReadOnlyDictionary<string, string> Edited, DateTimeOffset At, string AtText, string Signature)
{
    /// <summary>How far <see cref="At"/> may be from this server's clock: a captured decision cannot be replayed later, and a retry from the phone is signed anew.</summary>
    public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(15);

    /// <summary>The typed answer, trimmed; null when none was typed.</summary>
    public string? Answer => Edited.GetValueOrDefault("body.answer")?.Trim() is { Length: > 0 } text ? text : null;

    /// <summary>The decision in <paramref name="json"/>, or null with the reason when it is not one.</summary>
    public static (ApprovalDecision? Decision, string? Problem) Parse(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
            return (null, "the body is not a JSON object");
        string? Text(string name) => json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (Text("decision") is not { Length: > 0 } decision || Text("id") is not { Length: > 0 } id || Text("action") is not { Length: > 0 } action || Text("sig") is not { Length: > 0 } sig)
            return (null, "decision, id, action and sig are required");
        if (Text("at") is not { } atText || !DateTimeOffset.TryParse(atText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
            return (null, "at must be an ISO 8601 time");
        var edited = new Dictionary<string, string>(StringComparer.Ordinal);
        if (json.TryGetProperty("edited", out var fields) && fields.ValueKind != JsonValueKind.Null)
        {
            if (fields.ValueKind != JsonValueKind.Object)
                return (null, "edited must be an object");
            foreach (var field in fields.EnumerateObject())
            {
                if (field.Value.ValueKind != JsonValueKind.String)
                    return (null, "every edited field must be text");
                edited[field.Name] = field.Value.GetString()!;
            }
        }
        return (new ApprovalDecision(decision, id, action, edited, at, atText, sig), null);
    }

    /// <summary>Whether the signature is the key's and <see cref="At"/> is within <see cref="MaxSkew"/> of <paramref name="now"/>; the signature is compared in constant time.</summary>
    public bool Verify(string key, DateTimeOffset now)
    {
        if ((now - At).Duration() > MaxSkew)
            return false;
        var message = $"{Decision}|{Id}|{Action}|{AtText}|{EditedHash(Edited)}";
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(message)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(Signature.ToLowerInvariant()));
    }

    /// <summary>The SHA-256 hex of <see cref="Canonical"/>, empty when nothing was edited.</summary>
    public static string EditedHash(IReadOnlyDictionary<string, string> edited) =>
        Canonical(edited) is { Length: > 0 } text ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))) : "";

    /// <summary>The edited fields as JavaScript's <c>JSON.stringify</c> writes them with the keys sorted by UTF-16 code unit: the signer is a Node server, so every character it leaves as is stays as is here.</summary>
    public static string Canonical(IReadOnlyDictionary<string, string> edited)
    {
        if (edited.Count == 0)
            return "";
        var text = new StringBuilder("{");
        foreach (var (key, value) in edited.OrderBy(e => e.Key, StringComparer.Ordinal))
            text.Append(text.Length > 1 ? "," : "").Append(Quote(key)).Append(':').Append(Quote(value));
        return text.Append('}').ToString();
    }

    private static string Quote(string s)
    {
        var text = new StringBuilder("\"");
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var lone = char.IsHighSurrogate(c) ? i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]) : char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(s[i - 1]));
            text.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when c < ' ' || lone => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }
        return text.Append('"').ToString();
    }

    /// <summary>The kind, batch and task an item id names, or null when it is not one of <paramref name="source"/>'s.</summary>
    public static (string Kind, string BatchRunId, string TaskId)? Parse(string source, string id)
    {
        var parts = id.Split(':', 4);
        if (parts.Length != 4 || parts[0] != source || parts[2].Length == 0 || parts[3].Length == 0)
            return null;
        var kind = parts[1].Split('.', 2)[0];
        return kind is QuestionKinds.Question or QuestionKinds.Partial ? (kind, parts[2], parts[3]) : null;
    }
}
