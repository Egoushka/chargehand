using System.Globalization;
using System.Text.RegularExpressions;

namespace Chargehand.Modules.Handover.Api;

/// <summary>One mutation run on a session's branch: the changed files are mutated and the repository's tests run against each mutant.</summary>
/// <param name="Files">The changed production files to mutate.</param>
public sealed record MutationRun(string RunId, string BundlePath, string Branch, string BaseCommit, IReadOnlyList<string> Files, TimeSpan Timeout);

/// <param name="Ran">The mutation tool ran to its end; false: it could not run (no tool, a build error, a timeout), and <paramref name="Detail"/> says why.</param>
/// <param name="Score">The share of mutants the tests killed, 0 to 1; null: no mutant was made, so there is nothing to kill.</param>
public sealed record MutationReport(bool Ran, double? Score, string Detail);

/// <summary>What the mutation check found for a handover, kept in the task's <c>mutation</c> artifact.</summary>
/// <param name="Ran">False: the tool could not run, and <paramref name="NotRun"/> says why.</param>
/// <param name="Score">The share of mutants killed, 0 to 1; null: none was made, or the tool did not run.</param>
/// <param name="Threshold">The share the repository requires.</param>
/// <param name="Survivors">Surviving mutants when the tool reports the count; null otherwise.</param>
/// <param name="NotRun">Why the tool could not run; null when it did.</param>
public sealed record MutationOutcome(bool Ran, double? Score, double Threshold, string? NotRun, int? Survivors = null, int? Killed = null);

public interface IMutationTester
{
    Task<MutationReport> MutateAsync(MutationRun run, CancellationToken ct);
}

/// <summary>Stryker.NET on the branch, in the same fresh container as chargehand's verification (<see cref="IBranchVerifier"/>): the tool is installed into a scratch path, mutates the
/// files changed since the base commit (<c>--since</c>) and reports its mutation score on the console, which is read from the end of the output.</summary>
public sealed partial class StrykerMutationTester(IBranchVerifier verifier) : IMutationTester
{
    public const string Version = "4.16.0";

    public async Task<MutationReport> MutateAsync(MutationRun run, CancellationToken ct)
    {
        var record = await verifier.VerifyAsync(new BranchVerification(run.RunId, run.BundlePath, run.Branch, Argv(run.BaseCommit), run.Timeout), ct);
        if (record.TimedOut)
            return new MutationReport(false, null, "the mutation run timed out");
        return Read(record.ExitCode, record.OutputTail);
    }

    /// <summary>The base commit is a hex sha (<see cref="Handover"/> checks it), so it goes into the shell line as it is.</summary>
    public static IReadOnlyList<string> Argv(string baseCommit) =>
        ["sh", "-c", $"dotnet tool install dotnet-stryker --version {Version} --tool-path /tmp/chargehand-stryker >/dev/null && /tmp/chargehand-stryker/dotnet-stryker --since:{baseCommit} --reporter cleartext"];

    /// <summary>The last "final mutation score" line of the output; with none, a run that made no mutant passes and any other fails.</summary>
    public static MutationReport Read(int exitCode, string output)
    {
        if (exitCode != 0)
            return new MutationReport(false, null, $"the mutation run failed (exit {exitCode.ToString(CultureInfo.InvariantCulture)})");
        var score = Score().Matches(output).LastOrDefault();
        if (score is not null && double.TryParse(score.Groups["s"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) && double.IsFinite(percent))
            return new MutationReport(true, percent / 100, score.Value);
        if (score is not null || NoMutants().IsMatch(output))
            return new MutationReport(true, null, "no mutant was made of the changed files");
        return new MutationReport(false, null, "the mutation run reported no score");
    }

    [GeneratedRegex(@"final mutation score is (?<s>[0-9]+(\.[0-9]+)?|NaN) ?%", RegexOptions.IgnoreCase)]
    private static partial Regex Score();

    [GeneratedRegex(@"no mutants|0 mutants (created|tested)", RegexOptions.IgnoreCase)]
    private static partial Regex NoMutants();
}
