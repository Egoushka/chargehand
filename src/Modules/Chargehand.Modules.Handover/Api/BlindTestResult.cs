using System.Text.RegularExpressions;

namespace Chargehand.Modules.Handover.Api;

/// <summary>A tests-only branch of a blind task the verification lays over the branch: the bundle file on the host and its branch.</summary>
public sealed record TestsBundle(string Path, string Branch);

/// <summary>What the blind tests (issue 789) did on the implementer's branch: how many ran, how many failed and which, and which bundles were dropped because they did not compile on the base commit.</summary>
/// <param name="Count">Tests the runner reported, 0 when its summary line was not recognised.</param>
/// <param name="Failed">Failing tests; 1 at least when the run failed without a test name in its output.</param>
/// <param name="Names">The failing test names the output gave.</param>
/// <param name="Uncompilable">The tests bundles (by branch) that did not compile on the base commit; they are reported <c>blind_uncompilable</c> and never fail the implementer.</param>
public sealed record BlindTestResult(int Count, int Failed, IReadOnlyList<string> Names, IReadOnlyList<string> Uncompilable);

/// <summary>Reads a test run's output for <see cref="BlindTestResult"/>: <c>dotnet test</c> lines <c>  Failed Name [3 ms]</c> and its <c>Failed: n, Passed: m</c> summary.</summary>
public static partial class BlindTestOutput
{
    [GeneratedRegex(@"^\s*Failed\s+(?<name>\S.*?)\s+\[", RegexOptions.Multiline)]
    private static partial Regex FailedLine();

    [GeneratedRegex(@"Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+)")]
    private static partial Regex Summary();

    [GeneratedRegex(@"error (CS|TS|FS|BC)\d+|SyntaxError|cannot find symbol|compilation (failed|error)|Build FAILED", RegexOptions.IgnoreCase)]
    private static partial Regex CompileError();

    public static IReadOnlyList<string> FailedNames(string output) =>
        [.. FailedLine().Matches(output).Select(m => m.Groups["name"].Value).Distinct(StringComparer.Ordinal)];

    /// <summary>True when the output shows the tests did not build, as opposed to a test failing.</summary>
    public static bool DidNotCompile(string output) => CompileError().IsMatch(output);

    public static BlindTestResult Of(string output, int exitCode, IReadOnlyList<string> uncompilable)
    {
        var names = FailedNames(output);
        var summary = Summary().Match(output);
        var count = summary.Success ? int.Parse(summary.Groups["failed"].Value, System.Globalization.CultureInfo.InvariantCulture) + int.Parse(summary.Groups["passed"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var failed = exitCode == 0 ? 0 : Math.Max(1, Math.Max(names.Count, summary.Success ? int.Parse(summary.Groups["failed"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0));
        return new BlindTestResult(count, failed, names, uncompilable);
    }
}
