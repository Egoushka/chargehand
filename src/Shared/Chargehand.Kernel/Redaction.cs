using System.Text.RegularExpressions;

namespace Chargehand.Kernel;

/// <summary>What must not leave the machine, taken out of text that may reach a result, a log or a memory.</summary>
public static partial class Redaction
{
    /// <summary>
    /// Provider and gateway error text without what must not leave the machine: keys, key aliases, bearer tokens and
    /// spend figures. Every exception message that can reach result/v1 or the run log passes through here.
    /// </summary>
    public static string Scrub(string text)
    {
        text = ApiKey().Replace(text, "sk-[redacted]");
        text = AuthScheme().Replace(text, "${scheme} [redacted]");
        text = Named().Replace(text, "${k}[redacted]");
        return Spend().Replace(text, "${k}[redacted]");
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9_])sk-[^\s)""',]+")]
    private static partial Regex ApiKey();

    /// <summary>Key-ish identifiers: bare or compound (apiKey, key_hash, team_member, user_id, organization…).
    /// The value is excluded when it's actually a bearer/basic scheme word — <see cref="AuthScheme"/> owns that shape.</summary>
    [GeneratedRegex(@"(?<k>\b(?:\w*(?:key|alias|token|hash|authorization|organization)\w*|team_?member|team_?alias|user_?id|end_?user)\b["")]*\s*[:=]\s*[""(]*)(?!(?:bearer|basic)\b)[^\s,)""]+", RegexOptions.IgnoreCase)]
    private static partial Regex Named();

    [GeneratedRegex(@"\b(?<scheme>bearer|basic)\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthScheme();

    [GeneratedRegex(@"(?<k>\b(?:current cost|max budget|spend|budget)\s*[=:]\s*)\$?[0-9]+(?:\.[0-9]+)?(?:[eE][-+]?[0-9]+)?", RegexOptions.IgnoreCase)]
    private static partial Regex Spend();
}
