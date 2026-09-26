using System.Security.Cryptography;
using System.Text;

namespace Chargehand.Prompts;

/// <summary>Normalisation and hashing shared by registry and caller blocks (request/v1 describes the same rule).</summary>
public static class PromptText
{
    /// <summary>LF line endings, trailing whitespace stripped from every line.</summary>
    public static string Normalize(string text) =>
        string.Join('\n', text.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd()));

    public static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(text))));
}
