using System.Globalization;
using Chargehand.Contracts;

namespace Chargehand.Modules.Handover.Api;

public sealed partial class Handover
{
    /// <summary>Issue 788: green tests show little when they assert nothing. With <see cref="HandoverInput.MutationShare"/> and a mutation tester, the changed C# production files are
    /// mutated after the verification passed, and the branch is held back unless the tests kill at least that share of the mutants. A run that cannot finish holds it back too; a
    /// change that leaves no mutant (no production C# file, or nothing mutable in it) passes. A partial or bare handover, and one the repository's CI verifies, is not checked.</summary>
    private async Task<HandoverOutcome?> MutationCheckAsync(HandoverRun run, CancellationToken ct)
    {
        var input = run.Input;
        if (mutation is null || input.MutationShare is not { } share || input.Partial || input.Bare || input.CiVerifies || run.Verification is not { ExitCode: 0, TimedOut: false })
            return null;
        var files = MutatedFiles(run.Changed);
        if (files.Count == 0)
            return null;
        var report = await mutation.MutateAsync(new MutationRun(input.RunId, run.Bundle, input.Branch, input.BaseCommit, files, input.VerifyTimeout ?? TimeSpan.FromMinutes(30)), ct);
        if (MutationVerdict(report, share) is not { } reason)
            return null;
        return run.Refuse(reason, ErrorCode.VerificationFailed, run.Verification, run.Changed, verificationPaths: run.VerificationPaths, commit: run.Commit);
    }

    /// <summary>The changed files a .NET mutation tool mutates: C# production code, not tests or build files.</summary>
    public static IReadOnlyList<string> MutatedFiles(IReadOnlyList<string> changed) =>
        [.. changed.Where(p => p.EndsWith(".cs", StringComparison.Ordinal) && TestIntegrity.Classify(p) == ChangeKind.Production)];

    /// <returns>Why the branch is held back; null: the tests killed enough mutants, or there were none.</returns>
    public static string? MutationVerdict(MutationReport report, double share)
    {
        if (!report.Ran)
            return $"the mutation check could not run, so the tests were not shown to check the change: {report.Detail}";
        if (report.Score is not { } score || score >= share)
            return null;
        return string.Create(CultureInfo.InvariantCulture,
            $"the tests killed {score * 100:0.#}% of the mutants of the changed lines, below the required {share * 100:0.#}%: they pass, but do not show that they check the change");
    }
}
