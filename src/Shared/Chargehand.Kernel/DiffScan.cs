using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chargehand.Kernel;

/// <param name="Kind">The pattern's name; never the matched text.</param>
public sealed record ScanFinding(string Kind, string? Path);

/// <summary>The backstop before a push (ADR 0039): looks at what a session added, and at what it named its files, for what must not leave for a remote.
/// It is a short pattern list plus the literal credentials the session could have read; it finds the common accidents, not a determined leak.
/// Findings carry a pattern name and a path, never the matched text.</summary>
public static partial class DiffScan
{
    private static readonly (string Kind, Regex Pattern)[] Patterns =
    [
        ("aws", AwsKey()),
        ("private-key", PrivateKey()),
        ("github-token", GithubToken()),
        ("anthropic-key", AnthropicKey()),
        ("slack-token", SlackToken()),
    ];

    /// <param name="diff">A unified diff; only its added lines are read.</param>
    /// <param name="literals">Values that must not appear (the credentials a session had); under 8 characters they are ignored.</param>
    /// <param name="paths">The changed paths, for the file-name check.</param>
    public static IReadOnlyList<ScanFinding> Scan(string diff, IReadOnlyList<string> literals, IReadOnlyList<string> paths)
    {
        var findings = new HashSet<ScanFinding>();
        string? current = null;
        // Only the lines between "diff --git" and the first "@@" are headers; in a hunk "+++ x" is the added line "++ x".
        bool inHunk = false, afterOldHeader = false;
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                (current, inHunk, afterOldHeader) = (null, false, false);
                continue;
            }
            if (!inHunk)
            {
                if (line.StartsWith("--- ", StringComparison.Ordinal))
                {
                    afterOldHeader = true;
                    continue;
                }
                if (afterOldHeader && line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    (current, afterOldHeader) = (line.StartsWith("+++ b/", StringComparison.Ordinal) ? line[6..] : null, false);
                    continue;
                }
                afterOldHeader = false;
                inHunk = line.StartsWith("@@", StringComparison.Ordinal);
            }
            if (line.Length == 0 || line[0] != '+')
                continue;
            foreach (var kind in ScanText(line[1..], literals))
                findings.Add(new ScanFinding(kind, current));
        }
        foreach (var path in paths)
            if (SecretFile().IsMatch(path) && !ExampleFile().IsMatch(path))
                findings.Add(new ScanFinding("secret-file", path));
        return [.. findings];
    }

    /// <summary>The names of the patterns (and <c>credential-literal</c>) that free text matches, for text that is not a diff, such as a handoff's items; never the matched text.</summary>
    public static IReadOnlyList<string> ScanText(string text, IReadOnlyList<string> literals)
    {
        var kinds = new List<string>();
        foreach (var (kind, pattern) in Patterns)
            if (pattern.IsMatch(text))
                kinds.Add(kind);
        if (literals.Any(literal => literal.Length >= 8 && text.Contains(literal, StringComparison.Ordinal)))
            kinds.Add("credential-literal");
        return kinds;
    }

    /// <summary>Text with what <see cref="ScanText"/> finds replaced by <c>[redacted:&lt;kind&gt;]</c> (a credential literal by <c>[redacted]</c>), and the kinds that were found (ADR 0059). For a transcript, which is
    /// JSON lines: a literal is also looked for in its JSON-escaped form, and a private key is taken from its header to its footer, or, with no footer, to the end of the JSON string it is in. Like the scan, it finds the common accidents and the
    /// credentials the server holds; it cannot know a password the model read from a repository's file.</summary>
    public static (string Text, IReadOnlyList<string> Kinds) Redact(string text, IReadOnlyList<string> literals)
    {
        var kinds = new List<string>();
        if (PrivateKeyBlock().IsMatch(text))
        {
            text = PrivateKeyBlock().Replace(text, "[redacted:private-key]");
            kinds.Add("private-key");
        }
        foreach (var (kind, pattern) in Patterns)
            if (pattern.IsMatch(text))
            {
                text = pattern.Replace(text, $"[redacted:{kind}]");
                kinds.Add(kind);
            }
        var found = false;
        foreach (var literal in literals.Where(l => l.Length >= 8))
            foreach (var form in new[] { literal, JsonEncodedText.Encode(literal).Value }.Distinct())
                if (text.Contains(form, StringComparison.Ordinal))
                {
                    text = text.Replace(form, "[redacted]", StringComparison.Ordinal);
                    found = true;
                }
        if (found)
            kinds.Add("credential-literal");
        return (text, kinds);
    }

    /// <summary>The changed paths that configure a CI system. Pushing a branch that changes them can run them, with the repository's secrets, so
    /// chargehand does not push such a branch unless it is told to.</summary>
    public static IReadOnlyList<string> CiPaths(IReadOnlyList<string> paths) => [.. paths.Where(p => Ci().IsMatch(p))];

    [GeneratedRegex(@"AKIA[0-9A-Z]{16}")]
    private static partial Regex AwsKey();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----.*?(-----END [A-Z ]*PRIVATE KEY-----|(?=\x22)|$)", RegexOptions.Singleline)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(@"gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{50,}")]
    private static partial Regex GithubToken();

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_-]{20,}")]
    private static partial Regex AnthropicKey();

    [GeneratedRegex(@"xox[baprs]-[A-Za-z0-9-]{10,}")]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"(^|/)\.env(\..+)?$|(^|/)id_(rsa|dsa|ecdsa|ed25519)$|\.(pem|p12|pfx|key)$|(^|/)\.netrc$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretFile();

    [GeneratedRegex(@"(^|/)\.env\.(example|sample|template)$", RegexOptions.IgnoreCase)]
    private static partial Regex ExampleFile();

    [GeneratedRegex(@"^\.github/(workflows|actions)/|^\.gitlab-ci\.ya?ml$|^azure-pipelines\.ya?ml$|^\.circleci/|(^|/)Jenkinsfile$|^\.buildkite/|^bitbucket-pipelines\.yml$|^\.drone\.yml$")]
    private static partial Regex Ci();
}
