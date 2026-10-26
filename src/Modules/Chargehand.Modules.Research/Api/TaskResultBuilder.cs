using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Research.Api;

/// <summary>Builds the <c>result/v1</c> of one driven task (ADR 0039). A task's claims are the session's, cited against the branch commit that chargehand has in its own
/// clone and run through the same resolver and support check as any other result. The tests appear twice and never merged: <c>verification</c> is chargehand's own run in a fresh
/// container, <c>session-tests</c> is what the session said it ran. Nothing here says a claim is true, or that the change is right.</summary>
public static class TaskResultBuilder
{
    /// <summary>result/v1: inline artifact content is at most 64 KiB (ADR 0009).</summary>
    public const int MaxInlineBytes = 65536;

    private const double CompletedConfidence = 0.7;

    private static readonly TaskArtifactsJsonContext Json = TaskArtifactsJsonContext.Default;

    public static async Task<ResultContract> BuildAsync(DrivenTaskInput input, IEvidenceResolver resolver,
        Func<ResultContract, EvidenceScope, CancellationToken, Task<ResultContract>>? supportCheck, CancellationToken ct)
    {
        var draft = new Draft(input);
        if (Ended(draft) is { } ended)
            return ended;

        if (input.Bare)
            return Bare(draft);

        var report = input.ReportJson is null ? null : DrivenReport.Parse(input.ReportJson);
        if (report is null)
            return draft.Unfinished(ErrorCode.InvalidResult, "the session finished but printed no usable report (a fenced JSON block with a summary and claims)",
                "Send the task again; the session must end with the JSON report. Nothing was pushed.");
        if (input.Handover is not { } handover)
            return draft.Failed(ErrorCode.SessionFailed, "the session finished but its branch was not handed over", "Send the task again.");

        // The session's claims, cited against the commit chargehand pushed (or would have).
        var commit = handover.Commit ?? input.BaseCommit;
        var (claims, evidence, questions) = Cite(report, commit);
        var scope = new EvidenceScope(input.RepositoryPath, commit, [], [], "", []);
        var summary = handover.TestIntegrity is { } integrity ? $"{report.Summary}\n\nTest integrity: {integrity.Reason}." : report.Summary;
        var contract = draft.Result(ResultStatus.Completed, summary, claims, evidence, questions, CompletedConfidence);
        contract = EvidenceFailures.MoveUnresolved(contract, await resolver.ResolveAsync(contract, scope, ct));
        if (supportCheck is not null && contract.Claims.Count > 0)
            contract = await supportCheck(contract, scope, ct);

        AddHandoverArtifacts(draft.Artifacts, input, handover);
        if (report.TestsCommand is not null)
            draft.Artifacts.Add(Inline("session-tests", "application/json", new SessionTestsArtifact("session", report.TestsCommand, report.TestsExitCode), Json.SessionTestsArtifact));
        return Handed(contract with { Artifacts = [.. draft.Artifacts] }, handover);
    }

    /// <summary>The bare arm's result (ADR 0080): the agent prints no report, so there are no claims; the task is done when it left a branch that was pushed. No commits is an
    /// <see cref="ErrorCode.InvalidResult"/>, the code of a session that finished without the result it owed, which the benchmark reads as a stop on the task's own account.</summary>
    private static ResultContract Bare(Draft draft)
    {
        if (draft.Input.Handover is not { } handover)
            return draft.Failed(ErrorCode.InvalidResult, "the agent left no commits", "Nothing was pushed.");
        AddHandoverArtifacts(draft.Artifacts, draft.Input, handover);
        var contract = draft.Result(ResultStatus.Completed, "The agent left a branch; chargehand ran no tests and opened no pull request.", confidence: CompletedConfidence);
        return Handed(contract with { Artifacts = [.. draft.Artifacts] }, handover);
    }

    /// <summary>The result of a session that did not finish, or null when it did.</summary>
    private static ResultContract? Ended(Draft draft)
    {
        var s = draft.Input.Session;
        return s.Status switch
        {
            SessionStatus.NeedsInput => draft.Result(ResultStatus.NeedsInput, "The session needs answers before it can continue.",
                questions: s.Questions.Count > 0 ? s.Questions : ["The session asked for input but named no question."]),
            SessionStatus.Stalled => draft.Unfinished(ErrorCode.SessionStalled, $"the session was stopped: {s.Reason.Replace('_', ' ')}",
                "Send the task again with a narrower goal, or raise the driven preset's limits (max_minutes, no_progress_minutes, max_turns). The session log is in the session-log artifact."),
            SessionStatus.RateLimited when s.Reason == GatewayRefusal.BudgetReason => draft.Unfinished(ErrorCode.RateLimited, "the model gateway refused the session over a spend budget",
                "No start can succeed until the budget resets; the task's branch and output are kept. Send the task again after the reset.", ChargehandException.Retryable(ErrorCode.RateLimited)),
            SessionStatus.RateLimited => draft.Unfinished(ErrorCode.RateLimited, "the session ended on a usage or rate limit",
                "Retry once the limit lifts. The batch holds its remaining tasks until then.", ChargehandException.Retryable(ErrorCode.RateLimited)),
            SessionStatus.TokenCap => draft.Unfinished(ErrorCode.CostCapReached, "the session reached its token cap and was stopped",
                "Raise max_tokens_total in the request (and the preset's max_tokens), or give the task a narrower goal."),
            SessionStatus.Failed when s.Reason == SessionDriver.ChecksFailed => draft.Unfinished(ErrorCode.VerificationFailed,
                $"the branch failed {s.Checks?.Count(c => !c.Passed) ?? 0} of {s.Checks?.Count ?? 0} checks after the session's retries: {string.Join(", ", s.Checks?.Where(c => !c.Passed).Select(c => $"`{c.Command}`") ?? [])}",
                "Read the checks artifact (output_tail of each failed check), then fix the change or narrow the goal and send the task again."),
            SessionStatus.Failed => draft.Unfinished(ErrorCode.SessionFailed, $"the session did not finish: {(s.Reason.Length > 0 ? s.Reason.Replace('_', ' ') : "it failed")}",
                "Read the session log (the session-log artifact) and send the task again."),
            _ => null,
        };
    }

    /// <summary>The report's claims with the file lines they cite; a claim that cites none is a question.</summary>
    private static (List<Claim> Claims, List<Evidence> Evidence, List<string> Questions) Cite(DrivenReport report, string commit)
    {
        List<Claim> claims = [];
        List<Evidence> evidence = [];
        List<string> questions = [];
        foreach (var claim in report.Claims)
        {
            var locators = claim.Evidence.Where(DrivenReport.IsFileLocator).Distinct().ToList();
            if (locators.Count == 0)
            {
                questions.Add($"Unverified: {claim.Text} (it cites no code lines chargehand can check)");
                continue;
            }
            List<string> ids = [];
            foreach (var locator in locators)
            {
                var existing = evidence.FirstOrDefault(e => e.Locator == locator);
                if (existing is null)
                    evidence.Add(existing = new Evidence($"e{evidence.Count + 1}", EvidenceKind.File, locator, commit));
                ids.Add(existing.Id);
            }
            claims.Add(new Claim(claim.Text, ids, CompletedConfidence));
        }
        if (report.Dropped > 0)
            questions.Add($"The session reported {report.Dropped} more claims than chargehand reads ({DrivenReport.MaxClaims}); they were not checked.");
        return (claims, evidence, questions);
    }

    /// <summary>A finished session's result as its handover left it: completed only when the branch is pushed with its pull request.</summary>
    private static ResultContract Handed(ResultContract contract, HandoverOutcome handover) => handover.Status switch
    {
        HandoverStatus.Pushed => contract,
        HandoverStatus.PrFailed => Reject(contract, ErrorCode.PrFailed, $"the pull request could not be opened ({handover.Reason}); the branch {handover.Branch} is pushed",
            "The branch is pushed. Open the pull request by hand, or check the token's permission to open pull requests."),
        _ => Reject(contract, handover.Error ?? ErrorCode.SessionFailed, handover.Reason, NotPushedAction(handover.Error)),
    };

    private static string NotPushedAction(ErrorCode? error) => error switch
    {
        ErrorCode.VerificationFailed => "Read the verification artifact (output_tail), fix the change or the tests, and send the task again. Nothing was pushed.",
        ErrorCode.PushRejected => "Check the push credential and that the branch name is free. Nothing was pushed; the bundle is kept in the run's output volume.",
        _ => "Read the reason above and the session-log artifact, and send the task again. Nothing was pushed.",
    };

    /// <summary>The branch, changes, pull-request and verification artifacts of a handover.</summary>
    private static void AddHandoverArtifacts(List<Artifact> artifacts, DrivenTaskInput input, HandoverOutcome handover)
    {
        var pushed = handover.Status is HandoverStatus.Pushed or HandoverStatus.PrFailed;
        artifacts.Add(Inline("branch", "application/vnd.chargehand.branch+json", new BranchArtifact(handover.Branch, handover.Commit, input.BaseCommit, pushed), Json.BranchArtifact));
        if (handover.Changes is { } changes)
            artifacts.Add(Changes(changes, input.BaseCommit, handover.Commit));
        if (handover.PullRequest is { } pr)
            artifacts.Add(Inline("pull-request", "application/vnd.chargehand.pull-request+json", new PullRequestArtifact(pr.Url, pr.Number, true, handover.Branch), Json.PullRequestArtifact));
        if (handover.Verification is { } v)
            artifacts.Add(Inline("verification", "application/json", Verification(v, handover), Json.VerificationArtifact));
    }

    private static VerificationArtifact Verification(VerificationRecord v, HandoverOutcome handover) =>
        new(v.Argv, v.Source, "container", v.Network, v.ExitCode, v.ExitCode == 0 && !v.TimedOut, v.TimedOut, 1, Math.Round(v.Duration.TotalSeconds, 1), v.OutputTail,
            handover.ChangedVerificationPaths,
            handover.TestIntegrity is { } flag ? new TestIntegrityArtifact(true, flag.Reason, flag.TestPaths, flag.BuildPaths) : null);

    private static ResultContract Reject(ResultContract contract, ErrorCode code, string message, string action) =>
        contract with { Status = ResultStatus.Failed, Summary = message, Confidence = 0, Error = new ResultError(code, message, false, action) };

    /// <summary>The <c>changes</c> artifact: every changed path with its line counts, and the diff. It is inline, so it stays within 64 KiB: the diff goes first
    /// (<c>diff</c> null, <c>diff_truncated</c> true), then paths from the end; <c>files_total</c> keeps the real count.</summary>
    private static Artifact Changes(ChangeSummary changes, string baseCommit, string? commit)
    {
        ChangesArtifact Content(int files, bool withDiff) => new(baseCommit, commit, changes.Files.Count,
            changes.Files.Take(files).Select(f => new ChangedFile(f.Path, f.Added, f.Removed, f.Added is null && f.Removed is null)),
            withDiff ? changes.Diff : null, changes.DiffTruncated || (!withDiff && changes.Diff is not null));
        static bool Fits(ChangesArtifact value) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value, Json.ChangesArtifact)) <= MaxInlineBytes;

        var count = changes.Files.Count;
        var content = Content(count, withDiff: true);
        if (!Fits(content))
            content = Content(count, withDiff: false);
        while (!Fits(content) && count > 0)
            content = Content(count = count * 3 / 4, withDiff: false);
        return Inline("changes", "application/vnd.chargehand.changes+json", content, Json.ChangesArtifact);
    }

    /// <summary>The <c>checks</c> artifact; the output tails go when it would not fit inline.</summary>
    private static Artifact Checks(IReadOnlyList<CheckResult> checks)
    {
        ChecksArtifact Content(bool tails) => new(checks.All(c => c.Passed), checks.Max(c => c.Attempt),
            [.. checks.Select(c => new CheckArtifact(c.Kind, c.Command, c.Passed, c.ExitCode, c.ExpectedExitCode, c.ExpectedOutput, tails ? c.OutputTail : null))], !tails);
        var content = Content(tails: true);
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(content, Json.ChecksArtifact)) > MaxInlineBytes)
            content = Content(tails: false);
        return Inline("checks", "application/json", content, Json.ChecksArtifact);
    }

    private static Artifact Inline<T>(string kind, string mediaType, T value, JsonTypeInfo<T> type)
    {
        var content = JsonSerializer.Serialize(value, type);
        return new Artifact(kind, mediaType, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))), Content: content);
    }

    private static string Snake(string name) => JsonNamingPolicy.SnakeCaseLower.ConvertName(name);

    /// <summary>The parts every result of the task shares: its prompt chain, usage and the artifacts gathered so far.</summary>
    private sealed class Draft
    {
        private readonly PromptChain _chain;
        private readonly Usage _usage;

        public Draft(DrivenTaskInput input)
        {
            Input = input;
            _chain = new PromptChain([], new AsSent($"claude-code/{input.ClaudeVersion}", "session", input.Model, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            var s = input.Session;
            _usage = new Usage(s.InputTokens, s.OutputTokens, s.CacheReadTokens, 0, input.Priced ? s.CostUsd : null);
            if (input.Children.Count > 0)
                Artifacts.Add(Inline("review", "application/json", new ReviewArtifact(input.Children.Select(c => new ReviewRun(c.RunId, c.Preset, Snake(c.Status.ToString())))), Json.ReviewArtifact));
            if (s.StreamSha256 is { } streamHash)
                Artifacts.Add(new Artifact("session-log", "application/jsonl", streamHash, Uri: $"volume:{RunnerNames.Out(input.RunId)}/stream.jsonl"));
            if (s.GovernorLogSha256 is { } governorHash)
                Artifacts.Add(new Artifact("governor-log", "application/jsonl", governorHash, Uri: $"volume:{RunnerNames.Out(input.RunId)}/{SessionDriver.GovernorFile}"));
            if (input.Kept is { } kept)
                Artifacts.Add(Inline(KeptWork.ArtifactKind, "application/json", kept, Json.KeptWork));
            if (s.Checks is { Count: > 0 } checks)
                Artifacts.Add(Checks(checks));
        }

        public DrivenTaskInput Input { get; }

        public List<Artifact> Artifacts { get; } = [];

        public ResultContract Result(ResultStatus status, string summary, IReadOnlyList<Claim>? claims = null, IReadOnlyList<Evidence>? evidence = null,
            IReadOnlyList<string>? questions = null, double confidence = 0, ResultError? error = null) =>
            new("result/v1", Input.RunId, "session", Input.TraceId, _chain, status, summary, claims ?? [], evidence ?? [], [.. Artifacts], questions ?? [], confidence, _usage, error);

        public ResultContract Failed(ErrorCode code, string message, string action, IReadOnlyList<string>? questions = null, bool retryable = false) =>
            Result(ResultStatus.Failed, message, questions: questions ?? [message], error: new ResultError(code, message, retryable, action));

        /// <summary>A partial handover (ADR 0044) leaves the work of an unfinished task on a branch: the result says where, and the task is still failed.</summary>
        public ResultContract Unfinished(ErrorCode code, string message, string action, bool retryable = false)
        {
            if (Input.Handover is not { } partial)
                return Failed(code, message, action, retryable: retryable);
            AddHandoverArtifacts(Artifacts, Input, partial);
            var where = partial.Status switch
            {
                HandoverStatus.Pushed => $"The unfinished work is on branch {partial.Branch} in a partial draft pull request ({partial.PullRequest?.Url}): read it, finish it by hand, or send the task again with a narrower goal.",
                HandoverStatus.PrFailed => $"The unfinished work is pushed to branch {partial.Branch}, but the pull request could not be opened ({partial.Reason}). Open it by hand.",
                _ => $"The unfinished work was not pushed ({partial.Reason}); the bundle is kept in the run's output volume. {action}",
            };
            return Failed(code, partial.Status == HandoverStatus.NotPushed ? message : $"{message}; its work is on a partial draft pull request", where, retryable: retryable);
        }
    }
}
