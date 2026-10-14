using System.Text.RegularExpressions;
using Chargehand.Kernel;

namespace Chargehand.Driven;

public enum ChangeKind { Production, Test, Build }

/// <summary>A branch whose every change is to tests or build files, for a goal that did not ask for that work.</summary>
public sealed record TestIntegrityFlag(IReadOnlyList<string> TestPaths, IReadOnlyList<string> BuildPaths)
{
    /// <summary>One line for the result summary, the task outcome and the pull request comment.</summary>
    public string Reason => $"every changed file is a test or build file ({TestPaths.Count} test, {BuildPaths.Count} build) and the goal did not ask for test or build work, so a passing run may only show that the tests or the build were changed to pass";
}

/// <summary>A green verification means little when the session made it green by editing only what decides the verdict (ADR 0046). This sorts the changed paths into tests, build files and
/// production code, and flags a branch with no production change whose goal did not ask for test or build work. Path rules only: package.json counts as a build file whole, because the
/// paths do not say whether its scripts changed. CI configuration is already refused before a push unless allowed.</summary>
public static partial class TestIntegrity
{
    public static ChangeKind Classify(string path)
    {
        var name = Path.GetFileName(path);
        var directories = path.Split('/')[..^1];
        if (directories.Any(d => TestDirectory().IsMatch(d)) || TestFile().IsMatch(name) || name.EndsWith(".snap", StringComparison.Ordinal))
            return ChangeKind.Test;
        if (directories.Contains("scripts") || BuildFile().IsMatch(name) || DiffScan.CiPaths([path]).Count > 0)
            return ChangeKind.Build;
        return ChangeKind.Production;
    }

    /// <summary>The goal names test, spec, build, CI, coverage, fixture or snapshot work. Deliberately wide: a goal that may be about tests is not flagged.</summary>
    public static bool GoalAsksForTestOrBuildWork(string goal) => TestWords().IsMatch(goal);

    /// <returns>Null when the branch changes production code, the goal asks for test or build work, or there are no changes.</returns>
    public static TestIntegrityFlag? Assess(IReadOnlyList<string> changed, string goal)
    {
        if (changed.Count == 0 || GoalAsksForTestOrBuildWork(goal))
            return null;
        var kinds = changed.Select(p => (Path: p, Kind: Classify(p))).ToList();
        if (kinds.Any(k => k.Kind == ChangeKind.Production))
            return null;
        return new TestIntegrityFlag([.. kinds.Where(k => k.Kind == ChangeKind.Test).Select(k => k.Path)], [.. kinds.Where(k => k.Kind == ChangeKind.Build).Select(k => k.Path)]);
    }

    /// <summary>The top of the pull request body.</summary>
    public static string Warning(TestIntegrityFlag flag) =>
        $"> **Warning: test integrity.** {char.ToUpperInvariant(flag.Reason[0])}{flag.Reason[1..]}. chargehand marks this pull request ready for review and does not merge it; a person decides.";

    [GeneratedRegex(@"^(tests?|__tests__|specs?|__snapshots__|__mocks__|fixtures?|testdata|snapshots?|e2e|[A-Za-z0-9_.]+\.Tests?)$", RegexOptions.IgnoreCase)]
    private static partial Regex TestDirectory();

    [GeneratedRegex(@"(^test_.+)|(_tests?\.[^.]+$)|(\.(test|spec)\.[^.]+$)|((?-i:[a-z0-9](Tests?|Specs?)\.[^.]+$))|(^conftest\.py$)", RegexOptions.IgnoreCase)]
    private static partial Regex TestFile();

    [GeneratedRegex(@"^(Makefile|GNUmakefile|justfile|package\.json|package-lock\.json|pyproject\.toml|pytest\.ini|tox\.ini|setup\.(py|cfg)|Cargo\.toml|go\.mod|go\.sum|global\.json|NuGet\.config|.+\.(csproj|fsproj|vbproj|sln|slnx|props|targets))$")]
    private static partial Regex BuildFile();

    [GeneratedRegex(@"\b(tests?|tested|testing|specs?|builds?|building|ci|coverage|fixtures?|snapshots?|e2e)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TestWords();
}
