using System.Text.RegularExpressions;

namespace Chargehand.Mcp;

/// <summary><c>{secret:item}</c> in an MCP server's header and environment values (ADR 0034): replaced with the item's value
/// through the profile's secrets chain when a connection opens, so a profile never holds a credential.</summary>
public static partial class SecretTemplate
{
    /// <summary>Replaces every placeholder with <paramref name="secret"/> of its item. A value is never scanned for
    /// placeholders. The function's exception propagates; it names the item, never a value.</summary>
    public static string Resolve(string template, Func<string, string> secret) =>
        Placeholder().Replace(template, m => secret(m.Groups["item"].Value));

    public static bool HasSecret(string text) => Placeholder().IsMatch(text);

    [GeneratedRegex(@"\{secret:(?<item>[A-Za-z0-9._-]+)\}")]
    private static partial Regex Placeholder();
}
