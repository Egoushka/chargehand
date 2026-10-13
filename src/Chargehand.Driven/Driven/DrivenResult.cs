using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Results;
using Chargehand.Verification;

namespace Chargehand.Driven;

/// <param name="Session">How the session ended; its usage and questions.</param>
/// <param name="ReportJson">The last fenced JSON block the session printed; null when it printed none.</param>
/// <param name="Handover">Null when the session did not get as far as a handover.</param>
/// <param name="Children">The runs the session started through its token (research, review): their run-store rows.</param>
/// <param name="RepositoryPath">chargehand's scratch clone that holds the branch commit, for the citations.</param>
/// <param name="Priced">The credential has a dollar price (an API key): the result carries dollars; else they are unknown, never 0.</param>
/// <param name="Kept">The work an unfinished task leaves for a continuation (ADR 0045); null: none.</param>
public sealed record DrivenTaskInput(string TaskId, string RunId, string TraceId, string Model, string ClaudeVersion, SessionOutcome Session, string? ReportJson,
    HandoverOutcome? Handover, IReadOnlyList<RunSummary> Children, string RepositoryPath, string BaseCommit, bool Priced, KeptWork? Kept = null);

/// <summary>Builds the <c>result/v1</c> of one driven task and of a batch (ADR 0039). A task's claims are the session's, cited against the branch commit that chargehand has in its own
/// clone and run through the same resolver and support check as any other result. The tests appear twice and never merged: <c>verification</c> is chargehand's own run in a fresh
/// container, <c>session-tests</c> is what the session said it ran. Nothing here says a claim is true, or that the change is right.</summary>
public static class DrivenResult
{
    private const double CompletedConfidence = 0.7;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };

    public static async Task<ResultContract> BuildTaskAsync(DrivenTaskInput input, IEvidenceResolver resolver,
        Func<ResultContract, EvidenceScope, CancellationToken, Task<ResultContract>>? supportCheck, CancellationToken ct)
    {
        var chain = new PromptChain([], new AsSent($"claude-code/{input.ClaudeVersion}", "session", input.Model, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        var s = input.Session;
        var usage = new Usage(s.InputTokens, s.OutputTokens, s.CacheReadTokens, 0, input.Priced ? s.CostUsd : null);
        List<Artifact> artifacts = [];
        if (input.Children.Count > 0)
            artifacts.Add(Inline("review", "application/json", new { runs = input.Children.Select(c => new { run_id = c.RunId, preset = c.Preset, status = Snake(c.Status.ToString()) }) }));
        if (s.StreamSha256 is { } streamHash)
            artifacts.Add(new Artifact("session-log", "application/jsonl", streamHash, Uri: $"volume:{RunnerNames.Out(input.RunId)}/stream.jsonl"));
        if (s.GovernorLogSha256 is { } governorHash)
            artifacts.Add(new Artifact("governor-log", "application/jsonl", governorHash, Uri: $"volume:{RunnerNames.Out(input.RunId)}/{SessionDriver.GovernorFile}"));
        if (input.Kept is { } kept)
            artifacts.Add(Inline(ContinuationSource.KeptWorkKind, "application/json", kept));

        ResultContract Result(ResultStatus status, string summary, IReadOnlyList<Claim>? claims = null, IReadOnlyList<Evidence>? evidence = null, IReadOnlyList<string>? questions = null,
            double confidence = 0, ResultError? error = null) =>
            new("result/v1", input.RunId, "session", input.TraceId, chain, status, summary, claims ?? [], evidence ?? [], [.. artifacts], questions ?? [], confidence, usage, error);
        ResultContract Failed(ErrorCode code, string message, string action, IReadOnlyList<string>? questions = null, bool retryable = false) =>
            Result(ResultStatus.Failed, message, questions: questions ?? [message], error: new ResultError(code, message, retryable, action));

        // A partial handover (ADR 0044) leaves the work of an unfinished task on a branch: the result says where, and the task is still failed.
        ResultContract Unfinished(ErrorCode code, string message, string action, bool retryable = false)
        {
            if (input.Handover is not { } partial)
                return Failed(code, message, action, retryable: retryable);
            AddHandoverArtifacts(artifacts, input, partial);
            var where = partial.Status switch
            {
                HandoverStatus.Pushed => $"The unfinished work is on branch {partial.Branch} in a partial draft pull request ({partial.PullRequest?.Url}): read it, finish it by hand, or send the task again with a narrower goal.",
                HandoverStatus.PrFailed => $"The unfinished work is pushed to branch {partial.Branch}, but the pull request could not be opened ({partial.Reason}). Open it by hand.",
                _ => $"The unfinished work was not pushed ({partial.Reason}); the bundle is kept in the run's output volume. {action}",
            };
            return Failed(code, partial.Status == HandoverStatus.NotPushed ? message : $"{message}; its work is on a partial draft pull request", where, retryable: retryable);
        }

        switch (s.Status)
        {
            case SessionStatus.NeedsInput:
                return Result(ResultStatus.NeedsInput, "The session needs answers before it can continue.", questions: s.Questions.Count > 0 ? s.Questions : ["The session asked for input but named no question."]);
            case SessionStatus.Stalled:
                return Unfinished(ErrorCode.SessionStalled, $"the session was stopped: {s.Reason.Replace('_', ' ')}",
                    "Send the task again with a narrower goal, or raise the driven preset's limits (max_minutes, no_progress_minutes, max_turns). The session log is in the session-log artifact.");
            case SessionStatus.RateLimited:
                return Unfinished(ErrorCode.RateLimited, "the session ended on a usage or rate limit",
                    "Retry once the limit lifts. The batch holds its remaining tasks until then.", ChargehandException.Retryable(ErrorCode.RateLimited));
            case SessionStatus.TokenCap:
                return Unfinished(ErrorCode.CostCapReached, "the session reached its token cap and was stopped",
                    "Raise max_tokens_total in the request (and the preset's max_tokens), or give the task a narrower goal.");
            case SessionStatus.Failed:
                return Unfinished(ErrorCode.SessionFailed, $"the session did not finish: {(s.Reason.Length > 0 ? s.Reason.Replace('_', ' ') : "it failed")}",
                    "Read the session log (the session-log artifact) and send the task again.");
        }

        var report = input.ReportJson is null ? null : DrivenReport.Parse(input.ReportJson);
        if (report is null)
            return Failed(ErrorCode.InvalidResult, "the session finished but printed no usable report (a fenced JSON block with a summary and claims)",
                "Send the task again; the session must end with the JSON report. Nothing was pushed.");
        if (input.Handover is not { } handover)
            return Failed(ErrorCode.SessionFailed, "the session finished but its branch was not handed over", "Send the task again.");

        // The session's claims, cited against the commit chargehand pushed (or would have).
        var commit = handover.Commit ?? input.BaseCommit;
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

        var scope = new EvidenceScope(input.RepositoryPath, commit, [], [], "", []);
        var summary = handover.TestIntegrity is { } integrity ? $"{report.Summary}\n\nTest integrity: {integrity.Reason}." : report.Summary;
        var contract = Result(ResultStatus.Completed, summary, claims, evidence, questions, CompletedConfidence);
        contract = ResultAssembler.MoveUnresolved(contract, await resolver.ResolveAsync(contract, scope, ct));
        if (supportCheck is not null && contract.Claims.Count > 0)
            contract = await supportCheck(contract, scope, ct);

        AddHandoverArtifacts(artifacts, input, handover);
        if (report.TestsCommand is not null)
            artifacts.Add(Inline("session-tests", "application/json", new { claimed_by = "session", command = report.TestsCommand, exit_code = report.TestsExitCode }));
        contract = contract with { Artifacts = [.. artifacts] };

        return handover.Status switch
        {
            HandoverStatus.Pushed => contract,
            HandoverStatus.PrFailed => Reject(contract, ErrorCode.PrFailed, $"the pull request could not be opened ({handover.Reason}); the branch {handover.Branch} is pushed",
                "The branch is pushed. Open the pull request by hand, or check the token's permission to open pull requests."),
            _ => Reject(contract, handover.Error ?? ErrorCode.SessionFailed, handover.Reason, handover.Error switch
            {
                ErrorCode.VerificationFailed => "Read the verification artifact (output_tail), fix the change or the tests, and send the task again. Nothing was pushed.",
                ErrorCode.PushRejected => "Check the push credential and that the branch name is free. Nothing was pushed; the bundle is kept in the run's output volume.",
                _ => "Read the reason above and the session-log artifact, and send the task again. Nothing was pushed.",
            }),
        };
    }

    /// <summary>The branch, changes, pull-request and verification artifacts of a handover.</summary>
    private static void AddHandoverArtifacts(List<Artifact> artifacts, DrivenTaskInput input, HandoverOutcome handover)
    {
        var pushed = handover.Status is HandoverStatus.Pushed or HandoverStatus.PrFailed;
        artifacts.Add(Inline("branch", "application/vnd.chargehand.branch+json", new { branch = handover.Branch, commit = handover.Commit, @base = input.BaseCommit, pushed }));
        if (handover.Changes is { } changes)
            artifacts.Add(Changes(changes, input.BaseCommit, handover.Commit));
        if (handover.PullRequest is { } pr)
            artifacts.Add(Inline("pull-request", "application/vnd.chargehand.pull-request+json", new { url = pr.Url, number = pr.Number, draft = true, head = handover.Branch }));
        if (handover.Verification is { } v)
            artifacts.Add(Inline("verification", "application/json", new
            {
                argv = v.Argv,
                source = v.Source,
                sandbox = "container",
                network = v.Network,
                exit_code = v.ExitCode,
                passed = v.ExitCode == 0 && !v.TimedOut,
                timed_out = v.TimedOut,
                attempts = 1,
                duration_seconds = Math.Round(v.Duration.TotalSeconds, 1),
                output_tail = v.OutputTail,
                changed_verification_paths = handover.ChangedVerificationPaths,
                test_integrity = handover.TestIntegrity is { } flag ? new { flagged = true, reason = flag.Reason, test_paths = flag.TestPaths, build_paths = flag.BuildPaths } : null,
            }));
    }

    private static ResultContract Reject(ResultContract contract, ErrorCode code, string message, string action) =>
        contract with { Status = ResultStatus.Failed, Summary = message, Confidence = 0, Error = new ResultError(code, message, false, action) };

    /// <summary>The batch's own result: completed when every task ended in a draft pull request, else failed <c>tasks_incomplete</c> naming the tasks that did not; failed
    /// <c>cancelled</c> when the caller cancelled the batch before it finished (the same code as any cancelled run, <c>status</c> keeps its enum). Each task also has its
    /// own result under its own run id; this one lists them in the <c>driven-batch</c> artifact.</summary>
    /// <param name="cancelledByCaller">The caller (not a shutdown) cancelled the batch.</param>
    /// <param name="CredentialDelivery">How the model credential reached the containers (<c>gateway</c>: a per-task token, the real credential only in the egress container; <c>environment</c>: the real credential in the session container's environment).</param>
    public static ResultContract BuildBatch(string runId, string traceId, string description, BatchOutcome outcome, string credentialDelivery, bool priced, string claudeVersion,
        bool cancelledByCaller = false)
    {
        var chain = new PromptChain([], new AsSent($"claude-code/{claudeVersion}", "driven", "batch", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        var rows = outcome.Tasks.Select(t => new
        {
            id = t.Id,
            run_id = t.RunId,
            status = Snake(t.State.ToString()),
            branch = t.Branch,
            pr_url = t.PrUrl,
            usd = priced ? (decimal?)t.Usd : null,
            tokens = t.Tokens,
            error_code = t.Error is { } e ? Snake(e.ToString()) : null,
            detail = t.Detail,
        }).ToList();
        var artifact = Inline("driven-batch", "application/json", new { credential_delivery = credentialDelivery, tasks = rows });
        var usage = new Usage(outcome.Tokens, 0, 0, 0, priced ? outcome.Usd : null);
        if (outcome.AllCompleted)
            return new ResultContract("result/v1", runId, "driven", traceId, chain, ResultStatus.Completed, $"All {outcome.Tasks.Count} tasks ended in a draft pull request. {description}".Trim(), [], [], [artifact], [], 0.9, usage);
        var incomplete = outcome.Incomplete;
        var message = $"{incomplete.Count} of {outcome.Tasks.Count} tasks did not produce a pull request";
        var action = $"Tasks that did not finish: {string.Join(", ", incomplete)}. Read each task's own result under its run id, fix what it names and send those tasks again."
            + (outcome.Action is null ? "" : $" {outcome.Action}");
        var code = ErrorCode.TasksIncomplete;
        if (cancelledByCaller)
        {
            message = $"cancelled by the caller: {outcome.Tasks.Count - incomplete.Count} of {outcome.Tasks.Count} tasks finished";
            action = $"Tasks that did not finish: {string.Join(", ", incomplete)}; nothing from them was pushed. Send those tasks again if they are still wanted.";
            code = ErrorCode.Cancelled;
        }
        else if (outcome.Error == ErrorCode.RateLimited)
        {
            message = $"the provider's usage or rate limit stopped the batch: {outcome.Tasks.Count - incomplete.Count} of {outcome.Tasks.Count} tasks finished";
            code = ErrorCode.RateLimited;
        }
        var questions = outcome.Tasks.Where(t => t.State != TaskState.Completed).Select(t => $"Task {t.Id}: {Snake(t.State.ToString()).Replace('_', ' ')}: {t.Detail ?? "no detail"}").ToList();
        return new ResultContract("result/v1", runId, "driven", traceId, chain, ResultStatus.Failed, message, [], [], [artifact], questions, 0.5, usage,
            new ResultError(code, message, ChargehandException.Retryable(code), action));
    }

    /// <summary>The <c>changes</c> artifact: every changed path with its line counts, and the diff. It is inline, so it stays within 64 KiB: the diff goes first
    /// (<c>diff</c> null, <c>diff_truncated</c> true), then paths from the end; <c>files_total</c> keeps the real count.</summary>
    private static Artifact Changes(ChangeSummary changes, string baseCommit, string? commit)
    {
        object Content(int files, bool withDiff) => new
        {
            @base = baseCommit,
            commit,
            files_total = changes.Files.Count,
            files = changes.Files.Take(files).Select(f => new { path = f.Path, added = f.Added, removed = f.Removed, binary = f.Added is null && f.Removed is null }),
            diff = withDiff ? changes.Diff : null,
            diff_truncated = changes.DiffTruncated || (!withDiff && changes.Diff is not null),
        };
        bool Fits(object value) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value, Json)) <= ResultAssembler.MaxInlineBytes;

        var count = changes.Files.Count;
        var content = Content(count, withDiff: true);
        if (!Fits(content))
            content = Content(count, withDiff: false);
        while (!Fits(content) && count > 0)
            content = Content(count = count * 3 / 4, withDiff: false);
        return Inline("changes", "application/vnd.chargehand.changes+json", content);
    }

    private static Artifact Inline(string kind, string mediaType, object value)
    {
        var content = JsonSerializer.Serialize(value, Json);
        return new Artifact(kind, mediaType, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))), Content: content);
    }

    private static string Snake(string name) => JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
}
