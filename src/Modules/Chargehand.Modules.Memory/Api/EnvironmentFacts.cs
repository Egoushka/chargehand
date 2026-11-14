using System.Text.RegularExpressions;
using Chargehand.Contracts;

namespace Chargehand.Modules.Memory.Api;

/// <summary>
/// Facts about running a repository that a session met and the next one should not rediscover ("tests need Docker",
/// "Gradle is missing"). A completed result states one as a claim that starts with <see cref="Prefix"/>; it is kept
/// per repository, and recalled at the next session's start beside the facts recalled for its task (issue 434).
/// </summary>
public static partial class EnvironmentFacts
{
    public const string Prefix = "Environment:";

    public const string Tag = "environment";

    /// <summary>What a run asks memory for at its start: the environment facts of one repository.</summary>
    public static string Query(string repositoryLabel) => $"Environment facts for repository {repositoryLabel}: what running, building or testing it needs";

    /// <summary>The claims of a completed result that state an environment fact, one line each, once each.</summary>
    public static IReadOnlyList<string> From(ResultContract result) => result.Status != ResultStatus.Completed
        ? []
        : [.. result.Claims
            .Select(c => Whitespace().Replace(c.Text, " ").Trim())
            .Where(t => t.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && t.Length > Prefix.Length)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The item a run leaves for its repository. It is not tied to a commit: the fact is about the environment, not the code.</summary>
    public static MemoryItem Item(IReadOnlyList<string> facts, string repositoryLabel, string runId, DateTimeOffset finished) =>
        new(string.Join("\n", [$"Repository: {repositoryLabel} (environment facts met by a session)", .. facts.Select(f => $"- {f}")]),
            "chargehand environment facts", finished, runId, ["chargehand", Tag]);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
