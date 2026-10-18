using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Verification;
using Chargehand.Workspace;

namespace Chargehand;

/// <summary>What a writing node returns (ADR 0035): the branch, its diff and how it was verified, as artifacts, and the status
/// that follows from the verification.</summary>
internal static class ChangeRun
{
    private const double UntestedConfidence = 0.5;

    /// <summary>The feedback a failed check sends the worker as its next prompt.</summary>
    internal static string Feedback(VerifyOutcome outcome, int attempt, bool network) =>
        $"The verification command `{string.Join(' ', outcome.Plan.Argv)}` did not pass (attempt {attempt}): " +
        (outcome.TimedOut ? "it timed out and was killed" : $"exit code {outcome.ExitCode}") +
        $". It ran in a sandbox{(network ? "" : " with no network access")}. The end of its output:\n{outcome.OutputTail}\n" +
        "Fix the change and reply again ending with only the ```json result block.";

    /// <summary>The branch's commit message: the worker's summary, first line only, cut to 72 characters.</summary>
    internal static string CommitMessage(string summary)
    {
        var line = (summary.Split('\n', 2)[0]).Trim();
        var message = $"chargehand: {(line.Length == 0 ? "change" : line)}";
        return message.Length <= 72 ? message : message[..71] + "…";
    }

    internal static ResultContract Finish(ResultContract contract, BranchInfo? branch, string diff, VerifyOutcome? verification, int attempts, string sandboxKind,
        bool network, IReadOnlyList<string> changedPaths, VerifyPlan? plan, string delivery = DeliveryMode.Push, string? landFailure = null)
    {
        if (branch is null)
            return contract with
            {
                Status = ResultStatus.Failed,
                OpenQuestions = [.. contract.OpenQuestions, "The worker changed no files."],
                Error = new ResultError(ErrorCode.InvalidResult, "the worker changed no files", false,
                    "Name the change more concretely, or check the request against the repository at that commit."),
            };
        var artifacts = new List<Artifact>
        {
            Json("branch", "application/vnd.chargehand.branch+json", new { repository = branch.Repository, branch = branch.Branch, commit = branch.Commit, @base = branch.Base, delivery }),
            Inline("diff", "text/x-diff", diff),
            Json("verification", "application/json", VerificationRecord(verification, attempts, sandboxKind, network, VerifyCommand.VerificationPaths(changedPaths, plan))),
        };
        var merged = contract with { Artifacts = [.. contract.Artifacts, .. artifacts] };
        if (landFailure is not null)
            return merged with
            {
                Status = ResultStatus.Failed,
                OpenQuestions = [.. merged.OpenQuestions, $"The branch could not be fetched into the repository: {landFailure}"],
                Error = new ResultError(ErrorCode.DeliveryFailed, $"the branch could not be fetched into the repository: {landFailure}", false,
                    $"The branch {branch.Branch} is still in {branch.Repository}; fetch it by hand: git fetch {branch.Repository} {branch.Branch}:{branch.Branch}"),
            };
        if (verification is null)
            return merged with
            {
                OpenQuestions = [.. merged.OpenQuestions, "No test command found: the change was not tested. Name one with context.verify."],
                Confidence = Math.Min(merged.Confidence, UntestedConfidence),
            };
        if (verification.Passed)
            return merged;
        var what = verification.TimedOut ? "timed out" : $"exited {verification.ExitCode}";
        var message = $"the verification command `{string.Join(' ', verification.Plan.Argv)}` {what} after {attempts} attempt{(attempts == 1 ? "" : "s")}";
        return merged with
        {
            Status = ResultStatus.Failed,
            OpenQuestions = [.. merged.OpenQuestions, message],
            Error = new ResultError(ErrorCode.VerificationFailed, message, false,
                $"The branch {branch.Branch} in {branch.Repository} holds the attempt: read the verification artifact for the output, then fix it by hand or run again with a narrower request."),
        };
    }

    private static object VerificationRecord(VerifyOutcome? v, int attempts, string sandboxKind, bool network, IReadOnlyList<string> paths) => new
    {
        argv = v?.Plan.Argv,
        source = v?.Plan.Source ?? "none",
        sandbox = sandboxKind,
        network,
        exit_code = v?.ExitCode,
        passed = v?.Passed,
        timed_out = v?.TimedOut,
        attempts,
        duration_seconds = v is null ? (double?)null : Math.Round(v.Duration.TotalSeconds, 1),
        output_tail = v?.OutputTail,
        changed_verification_paths = paths,
    };

    private static Artifact Json(string kind, string mediaType, object value) =>
        Inline(kind, mediaType, JsonSerializer.Serialize(value, ContractJson.Options));

    private static Artifact Inline(string kind, string mediaType, string content) =>
        new(kind, mediaType, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))), Content: content);
}
