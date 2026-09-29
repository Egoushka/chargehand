using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Chargehand.Contracts;

/// <summary>What <see cref="ResultSignature.Verify"/> found.</summary>
public sealed record SignatureCheck(bool Valid, string Reason);

/// <summary>
/// Signing and offline verification of a result (ADR 0036): ES256 over the RFC 8785 canonical form of the result without its
/// <c>signature</c> member. Verification works on the JSON text as received, so reformatting or reordering keys does not break it.
/// It proves that the holder of the key produced exactly this JSON; nothing about whether the claims are true.
/// </summary>
public static class ResultSignature
{
    public const string Algorithm = "ES256";

    /// <summary>RFC 8785 for a result: keys sorted by UTF-16 code units, no whitespace, numbers in ECMAScript form; the top-level
    /// <c>signature</c> member is left out.</summary>
    public static string Canonicalize(JsonElement result)
    {
        var sb = new StringBuilder();
        Write(sb, result, skipSignature: true);
        return sb.ToString();
    }

    /// <summary>The first 16 hex characters of the SHA-256 of the public key's SubjectPublicKeyInfo DER.</summary>
    public static string KeyId(ECDsa key) => Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..16];

    public static ResultContract Sign(ResultContract result, ECDsa privateKey)
    {
        var unsigned = result with { Signature = null };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(unsigned, ContractJson.Options));
        var bytes = Encoding.UTF8.GetBytes(Canonicalize(doc.RootElement));
        var value = privateKey.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return unsigned with { Signature = new SignatureBlock(Algorithm, KeyId(privateKey), Base64Url(value)) };
    }

    public static SignatureCheck Verify(string resultJson, ECDsa publicKey)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(resultJson);
        }
        catch (JsonException e)
        {
            return new SignatureCheck(false, $"not JSON: {e.Message}");
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("signature", out var sig) || sig.ValueKind != JsonValueKind.Object)
                return new SignatureCheck(false, "unsigned: the result has no signature member");
            if (Text(sig, "alg") != Algorithm)
                return new SignatureCheck(false, $"unsupported algorithm '{Text(sig, "alg")}'; only {Algorithm} is defined");
            if (Text(sig, "key_id") != KeyId(publicKey))
                return new SignatureCheck(false, "the signature was made with a different key (key_id does not match)");
            byte[] signature;
            try
            {
                signature = FromBase64Url(Text(sig, "value") ?? "");
            }
            catch (FormatException)
            {
                return new SignatureCheck(false, "the signature value is not base64url");
            }
            var bytes = Encoding.UTF8.GetBytes(Canonicalize(doc.RootElement));
            return publicKey.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                ? new SignatureCheck(true, "valid")
                : new SignatureCheck(false, "the signature does not match the result: it was changed after signing, or signed by another key");
        }
    }

    private static string? Text(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void Write(StringBuilder sb, JsonElement e, bool skipSignature = false)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var first = true;
                foreach (var p in e.EnumerateObject().Where(p => !(skipSignature && p.Name == "signature")).OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                        sb.Append(',');
                    first = false;
                    WriteString(sb, p.Name);
                    sb.Append(':');
                    Write(sb, p.Value);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var firstItem = true;
                foreach (var item in e.EnumerateArray())
                {
                    if (!firstItem)
                        sb.Append(',');
                    firstItem = false;
                    Write(sb, item);
                }
                sb.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(sb, e.GetString()!);
                break;
            case JsonValueKind.Number:
                sb.Append(Number(e.GetDouble()));
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            default:
                sb.Append("null");
                break;
        }
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                case < ' ': sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
    }

    /// <summary>ECMAScript Number-to-string (RFC 8785, 3.2.2.3): the shortest round-trip digits laid out by the exponent.</summary>
    internal static string Number(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v))
            throw new ArgumentException("JSON has no NaN or Infinity");
        if (v == 0)
            return "0";
        var sign = v < 0 ? "-" : "";
        var s = Math.Abs(v).ToString("R", CultureInfo.InvariantCulture);
        var exp10 = 0;
        if (s.IndexOf('E', StringComparison.Ordinal) is var e and >= 0)
        {
            exp10 = int.Parse(s[(e + 1)..], CultureInfo.InvariantCulture);
            s = s[..e];
        }
        var dot = s.IndexOf('.', StringComparison.Ordinal);
        var intPart = dot < 0 ? s : s[..dot];
        var digits = intPart + (dot < 0 ? "" : s[(dot + 1)..]);
        var n = intPart.Length + exp10;
        var lead = digits.Length - digits.TrimStart('0').Length;
        digits = digits[lead..].TrimEnd('0');
        n -= lead;
        var k = digits.Length;
        string body;
        if (k <= n && n <= 21)
            body = digits + new string('0', n - k);
        else if (0 < n && n <= 21)
            body = digits[..n] + "." + digits[n..];
        else if (-6 < n && n <= 0)
            body = "0." + new string('0', -n) + digits;
        else
        {
            var exponent = n - 1;
            var mantissa = k == 1 ? digits : digits[..1] + "." + digits[1..];
            body = mantissa + "e" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);
        }
        return sign + body;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}
