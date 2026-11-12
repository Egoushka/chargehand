using System.Text;
using Chargehand.Contracts;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Modules.Handover.Api;

public sealed partial class Handover
{
    private const string BlindCommitMessage = "test: blind tests from the issue";

    /// <summary>A blind task (issue 789) hands over tests only: a path that is not a test fails it as <c>blind_not_tests</c>.</summary>
    private static HandoverOutcome? BlindOutput(HandoverRun run)
    {
        if (!run.Input.BlindTask)
            return null;
        var notTests = run.Changed.Where(p => TestIntegrity.Classify(p) != ChangeKind.Test).ToList();
        return notTests.Count == 0 ? null
            : run.Refuse($"blind_not_tests: the blind task changed files that are not tests ({string.Join(", ", notTests.Take(20))}); nothing was pushed", ErrorCode.VerificationFailed, run.Verification, run.Changed, commit: run.Commit);
    }

    /// <summary>The implementer's branch failed the blind tests: refused (so the caller can continue the session) unless the work is kept as partial.</summary>
    private static HandoverOutcome? BlindFailed(HandoverRun run, VerificationRecord verification)
    {
        if (verification.BlindTests is not { Failed: > 0 } blind || run.Input.Partial)
            return null;
        return run.Refuse($"blind_tests_failed: {BlindNames(blind)}", ErrorCode.VerificationFailed, verification, run.Changed, verificationPaths: run.VerificationPaths, commit: run.Commit);
    }

    private static string BlindNames(BlindTestResult blind) =>
        blind.Names.Count > 0 ? $"{blind.Failed} blind test(s) failed: {string.Join(", ", blind.Names.Take(20))}" : $"{blind.Failed} blind test(s) failed";

    /// <summary>The pull request body's lines on the blind tests: which failed on the kept branch, and which bundles were dropped.</summary>
    private static string BlindNote(VerificationRecord? verification)
    {
        if (verification?.BlindTests is not { } blind)
            return "";
        var text = new StringBuilder();
        if (blind.Failed > 0)
            text.Append("\nBlind tests (written from the issue alone) still fail: ").Append(BlindNames(blind)).Append(".\n");
        if (blind.Uncompilable.Count > 0)
            text.Append("\nblind_uncompilable: the blind tests did not compile on the base commit and were left out: ").Append(string.Join(", ", blind.Uncompilable)).Append(".\n");
        return text.ToString();
    }

    /// <summary>Passing blind tests go to the pull request as their own commit; a bundle that did not compile on the base stays out.</summary>
    private static async Task<HandoverOutcome?> CommitBlindAsync(HandoverRun run, CancellationToken ct)
    {
        var input = run.Input;
        if (input.BlindBundles is not { Count: > 0 } bundles || run.Verification?.BlindTests is not { Failed: 0 } blind)
            return null;
        var (scratch, env) = (run.Scratch, run.Env);
        var any = false;
        for (var i = 0; i < bundles.Count; i++)
        {
            if (blind.Uncompilable.Contains(bundles[i].Branch))
                continue;
            var reference = $"blind-{i}";
            if ((await GitProcess.RunAsync(scratch, env, ["-c", "fetch.fsckObjects=true", "-c", "core.hooksPath=/dev/null", "fetch", "--quiet", bundles[i].Path, $"refs/heads/{bundles[i].Branch}:refs/heads/{reference}"], GitTimeout, ct)).ExitCode != 0)
                return run.Refuse("chargehand could not read the blind tests bundle", ErrorCode.SessionFailed, run.Verification, run.Changed, commit: run.Commit);
            var names = await GitProcess.RunAsync(scratch, env, ["diff", "--name-only", "-z", "--diff-filter=AMR", input.BaseCommit, reference], GitTimeout, ct);
            var paths = names.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(p => TestIntegrity.Classify(p) == ChangeKind.Test).ToList();
            if (paths.Count == 0)
                continue;
            if ((await GitProcess.RunAsync(scratch, env, ["checkout", "--quiet", reference, "--", .. paths], GitTimeout, ct)).ExitCode != 0)
                return run.Refuse("chargehand could not lay the blind tests over the branch", ErrorCode.SessionFailed, run.Verification, run.Changed, commit: run.Commit);
            any = true;
        }
        if (!any)
            return null;
        var commit = await GitProcess.RunAsync(scratch, env, ["-c", "core.hooksPath=/dev/null", "-c", "user.name=chargehand", "-c", $"user.email={ContainerTemplate.SessionEmail}", "commit", "--quiet", "-m", BlindCommitMessage], GitTimeout, ct);
        if (commit.ExitCode != 0)
            return run.Refuse($"chargehand could not commit the blind tests: {commit.Tail}", ErrorCode.SessionFailed, run.Verification, run.Changed, commit: run.Commit);
        run.Commit = (await GitProcess.RunAsync(scratch, env, ["rev-parse", "HEAD"], GitTimeout, ct)).Stdout.Trim();
        await GitProcess.RunAsync(scratch, env, ["update-ref", $"refs/heads/{input.Branch}", run.Commit], GitTimeout, ct);
        return null;
    }
}
