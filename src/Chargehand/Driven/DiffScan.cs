using System.Text.RegularExpressions;

namespace Chargehand.Driven;

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
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                current = line.StartsWith("+++ b/", StringComparison.Ordinal) ? line[6..] : null;
                continue;
            }
            if (line.Length == 0 || line[0] != '+')
                continue;
            var added = line[1..];
            foreach (var (kind, pattern) in Patterns)
                if (pattern.IsMatch(added))
                    findings.Add(new ScanFinding(kind, current));
            foreach (var literal in literals)
                if (literal.Length >= 8 && added.Contains(literal, StringComparison.Ordinal))
                    findings.Add(new ScanFinding("credential-literal", current));
        }
        foreach (var path in paths)
            if (SecretFile().IsMatch(path) && !ExampleFile().IsMatch(path))
                findings.Add(new ScanFinding("secret-file", path));
        return [.. findings];
    }

    /// <summary>The changed paths that configure a CI system. Pushing a branch that changes them can run them, with the repository's secrets, so
    /// chargehand does not push such a branch unless it is told to.</summary>
    public static IReadOnlyList<string> CiPaths(IReadOnlyList<string> paths) => [.. paths.Where(p => Ci().IsMatch(p))];

    [GeneratedRegex(@"AKIA[0-9A-Z]{16}")]
    private static partial Regex AwsKey();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKey();

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
