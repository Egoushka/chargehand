using System.Text.RegularExpressions;
using Chargehand.Modules.Intake.Api;

namespace Chargehand.Automation;

/// <summary>The overlap check of the intake governor (ADR 0084, issue #737): a new start whose issue declares a scope (a line <c>Scope: src/a.cs, docs/</c>) waits while that scope overlaps the files
/// of an open chargehand pull request, or the declared scope of a task of the repository that runs, so two runs do not edit the same files. An issue that declares no scope is never held by it.
/// The files of each pull request are read once per poll.</summary>
internal sealed partial class ScopeOverlap(IIntakeHost host, IntakeState state)
{
    private readonly Dictionary<string, List<(int Number, IReadOnlyList<string> Files)>> _pulls = [];

    public void NewPoll() => _pulls.Clear();

    /// <summary>The paths the text declares on its <c>Scope:</c> lines, in order and without repeats; empty when it declares none. A path may be a file or a directory (<c>docs/</c>, <c>docs/**</c>).</summary>
    public static IReadOnlyList<string> Declared(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        return [.. ScopeLine().Matches(text).SelectMany(m => m.Groups[1].Value.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)).Select(Normal).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Whether two paths name the same file, or one is a directory that holds the other.</summary>
    public static bool Overlaps(string a, string b) => a == b || Under(a, b) || Under(b, a);

    /// <summary>Why a start that declares <paramref name="scope"/> must wait, or null: it declares none, or nothing open or running touches it.</summary>
    public async Task<string?> HoldAsync(string repository, int issue, IReadOnlyList<string> scope, CancellationToken ct)
    {
        if (scope.Count == 0)
            return null;
        foreach (var (number, files) in await PullsAsync(repository, ct))
            if (Shared(scope, files) is { } path)
                return $"its scope overlaps pull request #{number} ({path})";
        foreach (var task in state.Tasks.Where(t => t is { Phase: IntakePhase.Running, Scope: not null } && t.Repository == repository && t.Issue != issue))
            if (Shared(scope, task.Scope!) is { } path)
                return $"its scope overlaps the running task of #{task.Issue} ({path})";
        return null;
    }

    private static string? Shared(IReadOnlyList<string> scope, IReadOnlyList<string> other) =>
        scope.FirstOrDefault(s => other.Any(o => Overlaps(s, Normal(o))));

    private async Task<List<(int Number, IReadOnlyList<string> Files)>> PullsAsync(string repository, CancellationToken ct)
    {
        if (_pulls.TryGetValue(repository, out var known))
            return known;
        List<(int, IReadOnlyList<string>)> pulls = [];
        foreach (var pull in await host.OpenPullsAsync(repository, ct))
            if (GitHubIntake.IsChargehandBranch(pull.HeadBranch, pull.SameRepository))
                pulls.Add((pull.Number, await host.PullFilesAsync(repository, pull.Number, ct)));
        return _pulls[repository] = pulls;
    }

    private static bool Under(string path, string directory) => path.StartsWith(directory + "/", StringComparison.Ordinal);

    /// <summary>Without backticks, a leading <c>./</c> or <c>/</c>, a trailing <c>/</c>, <c>/*</c> or <c>/**</c>.</summary>
    private static string Normal(string path)
    {
        var p = path.Trim().Trim('`', '"', '\'').Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal))
            p = p[2..];
        p = p.TrimStart('/');
        while (p.EndsWith('*') || p.EndsWith('/'))
            p = p.TrimEnd('*').TrimEnd('/');
        return p;
    }

    [GeneratedRegex(@"^[ \t]*(?:[-*][ \t]+)?\**scope\**[ \t]*:\**[ \t]*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ScopeLine();
}
