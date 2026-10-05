using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Results;
using Chargehand.Verification;

namespace Chargehand.Tests;

/// <summary>ADR 0039: the result of one task and of a batch. The claims are the session's, checked against the branch chargehand pushed; the tests are chargehand's own run, kept apart from
/// what the session said it ran.</summary>
public class DrivenResultTests
{
    private static string G(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {text}");
        return text;
    }

    /// <summary>The scratch clone chargehand holds after a handover: a README of five lines and a Retry.cs at the branch commit.</summary>
    private sealed class Repo : IDisposable
    {
        public TempDir Dir { get; } = new();
        public string Commit { get; }
        public string Base { get; }

        public Repo()
        {
            G(Dir.Path, "init", "-q", "-b", "main");
            File.WriteAllText(Path.Combine(Dir.Path, "README.md"), "one\ntwo\nthree\nfour\nfive\n");
            G(Dir.Path, "add", "-A");
            G(Dir.Path, "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-q", "-m", "base");
            Base = G(Dir.Path, "rev-parse", "HEAD").Trim();
            File.WriteAllText(Path.Combine(Dir.Path, "Retry.cs"), "class Retry { }\n// retries twice\n");
            G(Dir.Path, "add", "-A");
            G(Dir.Path, "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-q", "-m", "feat: retry");
            Commit = G(Dir.Path, "rev-parse", "HEAD").Trim();
        }

        public void Dispose() => Dir.Dispose();
    }

    private const string Report = """{"summary":"Added a retry.","claims":[{"text":"The client retries twice.","evidence":["Retry.cs:1-2"]},{"text":"The README has five lines.","evidence":["README.md:1-5"]}],"tests":{"command":"npm test","exit_code":0}}""";

    private static SessionOutcome Session(SessionStatus status = SessionStatus.Completed, string reason = "", IReadOnlyList<string>? questions = null) =>
        new(status, reason, 12, 90_000, 4_000, 300_000, 0.42m, 0, questions ?? [], true, StreamSha256: new string('a', 64));

    private static VerificationRecord Verified(int exit = 0) =>
        new(["npm", "test"], "detected", exit, exit == 0 ? "ok" : "2 failed", false, TimeSpan.FromSeconds(41), true);

    private static HandoverOutcome Pushed(Repo r) =>
        new(HandoverStatus.Pushed, "", null, "chargehand/run1", r.Commit, new PullRequestRef("https://example.test/o/r/pull/7", 7), Verified(), ["Retry.cs"], ["package.json"], []);

    private static DrivenTaskInput Input(Repo r, SessionOutcome? session = null, string? report = Report, HandoverOutcome? handover = null, bool priced = false) =>
        new("t1", "run1", new string('0', 32), "claude-sonnet-5-5", "2.1.283", session ?? Session(), report, handover ?? Pushed(r),
            [new RunSummary("run-summary/v1", "run-child-1", RunState.Completed, "default", DateTimeOffset.UtcNow, "run1"), new RunSummary("run-summary/v1", "run-child-2", RunState.Completed, "review", DateTimeOffset.UtcNow, "run1")],
            r.Dir.Path, r.Base, priced);

    private static Task<ResultContract> Build(DrivenTaskInput input, Func<ResultContract, EvidenceScope, CancellationToken, Task<ResultContract>>? support = null) =>
        DrivenResult.BuildTaskAsync(input, new GitEvidenceResolver(), support, default);

    private static Func<ResultContract, EvidenceScope, CancellationToken, Task<ResultContract>> AllSupported =>
        (c, _, _) => Task.FromResult(c with { Claims = [.. c.Claims.Select(x => x with { Support = ClaimSupport.Supported })] });

    private static void AssertValid(ResultContract result)
    {
        var element = JsonSerializer.SerializeToElement(result, ContractJson.Options);
        Assert.Empty(ContractSchemas.Validate(ContractSchemas.Result, element));
        foreach (var a in result.Artifacts)
            if (a.Content is { } content)
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))), a.Sha256);
    }

    private static JsonElement Artifact(ResultContract r, string kind) => JsonDocument.Parse(r.Artifacts.Single(a => a.Kind == kind).Content!).RootElement;

    // ---- the report ----

    [Fact]
    public void A_report_is_parsed_into_a_summary_claims_and_the_sessions_own_test_record()
    {
        var report = DrivenReport.Parse(Report);
        Assert.Equal("Added a retry.", report!.Summary);
        Assert.Equal(["The client retries twice.", "The README has five lines."], report.Claims.Select(c => c.Text));
        Assert.Equal(["Retry.cs:1-2"], report.Claims[0].Evidence);
        Assert.Equal(("npm test", 0), (report.TestsCommand, report.TestsExitCode));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"claims":[]}""")]
    [InlineData("""{"summary":"","claims":[]}""")]
    [InlineData("""{"summary":"s","claims":"nope"}""")]
    [InlineData("""{"summary":"s","claims":[{"text":"","evidence":["a:1"]}]}""")]
    [InlineData("""{"summary":"s","claims":[{"text":"t","evidence":[1]}]}""")]
    [InlineData("[]")]
    public void A_report_that_is_not_one_is_refused(string json) => Assert.Null(DrivenReport.Parse(json));

    [Fact]
    public void A_report_with_more_than_the_allowed_claims_keeps_the_first_ones_and_says_so()
    {
        var claims = string.Join(",", Enumerable.Range(1, 80).Select(i => $$"""{"text":"claim {{i}}","evidence":["a.cs:1"]}"""));
        var report = DrivenReport.Parse($$"""{"summary":"s","claims":[{{claims}}]}""")!;
        Assert.Equal(DrivenReport.MaxClaims, report.Claims.Count);
        Assert.Equal(80 - DrivenReport.MaxClaims, report.Dropped);
    }

    // ---- one task ----

    [Fact]
    public async Task A_pushed_task_is_completed_with_checked_claims_and_every_artifact_valid()
    {
        using var repo = new Repo();
        var result = await Build(Input(repo), AllSupported);
        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal("run1", result.TaskId);
        Assert.Equal("Added a retry.", result.Summary);
        Assert.Equal(2, result.Claims.Count);
        Assert.All(result.Claims, c => Assert.Equal(ClaimSupport.Supported, c.Support));
        Assert.All(result.Evidence, e => Assert.Equal(repo.Commit, e.Commit));
        Assert.Equal(new Usage(90_000, 4_000, 300_000, 0, null), result.Usage);        // not priced: unknown, never a silent 0
        Assert.Equal(["branch", "pull-request", "review", "session-log", "session-tests", "verification"], result.Artifacts.Select(a => a.Kind).Order());
        AssertValid(result);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Dollars_are_recorded_only_where_the_credential_is_priced()
    {
        using var repo = new Repo();
        Assert.Equal(0.42m, (await Build(Input(repo, priced: true), AllSupported)).Usage.Usd);
        Assert.Null((await Build(Input(repo, priced: false), AllSupported)).Usage.Usd);
    }

    [Fact]
    public async Task The_branch_and_pull_request_artifacts_say_what_was_pushed_and_that_it_is_a_draft()
    {
        using var repo = new Repo();
        var result = await Build(Input(repo), AllSupported);
        var branch = Artifact(result, "branch");
        Assert.Equal("chargehand/run1", branch.GetProperty("branch").GetString());
        Assert.Equal(repo.Commit, branch.GetProperty("commit").GetString());
        Assert.Equal(repo.Base, branch.GetProperty("base").GetString());
        Assert.True(branch.GetProperty("pushed").GetBoolean());
        var pr = Artifact(result, "pull-request");
        Assert.True(pr.GetProperty("draft").GetBoolean());
        Assert.Equal(7, pr.GetProperty("number").GetInt32());
        Assert.Equal("https://example.test/o/r/pull/7", pr.GetProperty("url").GetString());
    }

    [Fact]
    public async Task The_changes_artifact_lists_each_path_with_its_line_counts_and_the_diff()
    {
        using var repo = new Repo();
        var changes = new ChangeSummary([new FileChange("Retry.cs", 12, 3), new FileChange("logo.png", null, null)], "diff --git a/Retry.cs b/Retry.cs\n+retry\n", false);
        var result = await Build(Input(repo, handover: Pushed(repo) with { Changes = changes }), AllSupported);
        AssertValid(result);
        var a = Artifact(result, "changes");
        Assert.Equal((repo.Base, repo.Commit), (a.GetProperty("base").GetString(), a.GetProperty("commit").GetString()));
        Assert.Equal(2, a.GetProperty("files_total").GetInt32());
        var files = a.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(("Retry.cs", 12, 3, false), (files[0].GetProperty("path").GetString(), files[0].GetProperty("added").GetInt32(), files[0].GetProperty("removed").GetInt32(), files[0].GetProperty("binary").GetBoolean()));
        Assert.True(files[1].GetProperty("binary").GetBoolean());
        Assert.Equal(JsonValueKind.Null, files[1].GetProperty("added").ValueKind);
        Assert.Equal(changes.Diff, a.GetProperty("diff").GetString());
        Assert.False(a.GetProperty("diff_truncated").GetBoolean());

        // No summary (an older handover, or none ran): no artifact.
        Assert.DoesNotContain((await Build(Input(repo), AllSupported)).Artifacts, x => x.Kind == "changes");
    }

    [Fact]
    public async Task A_changes_artifact_too_large_to_inline_drops_the_diff_then_paths_and_says_so()
    {
        using var repo = new Repo();
        var many = Enumerable.Range(0, 3000).Select(i => new FileChange($"src/a/very/deep/directory/tree/that/goes/on/file{i:D5}.cs", i, 1)).ToList();
        var changes = new ChangeSummary(many, new string('x', Handover.MaxChangesDiffBytes - 1) + "\n", true);
        var result = await Build(Input(repo, handover: Pushed(repo) with { Changes = changes }), AllSupported);
        AssertValid(result);
        var content = result.Artifacts.Single(x => x.Kind == "changes").Content!;
        Assert.InRange(Encoding.UTF8.GetByteCount(content), 1, ResultAssembler.MaxInlineBytes);
        var a = JsonDocument.Parse(content).RootElement;
        Assert.Equal(3000, a.GetProperty("files_total").GetInt32());
        Assert.InRange(a.GetProperty("files").GetArrayLength(), 1, 2999);
        Assert.Equal(JsonValueKind.Null, a.GetProperty("diff").ValueKind);
        Assert.True(a.GetProperty("diff_truncated").GetBoolean());
    }

    [Fact]
    public async Task Chargehands_verification_and_the_sessions_test_claim_are_two_records_never_merged()
    {
        using var repo = new Repo();
        var result = await Build(Input(repo), AllSupported);
        var verification = Artifact(result, "verification");
        Assert.Equal("container", verification.GetProperty("sandbox").GetString());
        Assert.Equal(0, verification.GetProperty("exit_code").GetInt32());
        Assert.Equal(["package.json"], verification.GetProperty("changed_verification_paths").EnumerateArray().Select(p => p.GetString()));
        var claimed = Artifact(result, "session-tests");
        Assert.Equal("session", claimed.GetProperty("claimed_by").GetString());
        Assert.Equal("npm test", claimed.GetProperty("command").GetString());
        Assert.False(claimed.TryGetProperty("sandbox", out _));
    }

    [Fact]
    public async Task The_review_artifact_lists_the_nested_runs_and_the_session_log_is_a_reference_never_inline()
    {
        using var repo = new Repo();
        var result = await Build(Input(repo), AllSupported);
        var review = Artifact(result, "review");
        Assert.Equal(["run-child-1", "run-child-2"], review.GetProperty("runs").EnumerateArray().Select(r => r.GetProperty("run_id").GetString()));
        Assert.Equal(["default", "review"], review.GetProperty("runs").EnumerateArray().Select(r => r.GetProperty("preset").GetString()));
        var log = result.Artifacts.Single(a => a.Kind == "session-log");
        Assert.Null(log.Content);
        Assert.Equal("volume:chargehand-out-run1/stream.jsonl", log.Uri);
        Assert.Equal(new string('a', 64), log.Sha256);
    }

    [Fact]
    public async Task A_citation_that_does_not_resolve_moves_the_claim_to_open_questions()
    {
        using var repo = new Repo();
        var report = """{"summary":"s","claims":[{"text":"Real claim.","evidence":["README.md:1-2"]},{"text":"Invented claim.","evidence":["Missing.cs:1-9"]},{"text":"Past the end.","evidence":["README.md:40-50"]}]}""";
        var result = await Build(Input(repo, report: report), AllSupported);
        Assert.Equal(["Real claim."], result.Claims.Select(c => c.Text));
        Assert.Contains(result.OpenQuestions, q => q.Contains("Invented claim.") && q.Contains("Unverified"));
        Assert.Contains(result.OpenQuestions, q => q.Contains("Past the end."));
    }

    [Fact]
    public async Task A_claim_that_cites_nothing_usable_is_an_open_question_not_a_claim()
    {
        using var repo = new Repo();
        var report = """{"summary":"s","claims":[{"text":"No citation at all.","evidence":[]},{"text":"A URL is not a file.","evidence":["https://example.test/x"]},{"text":"Fine.","evidence":["README.md:1"]}]}""";
        var result = await Build(Input(repo, report: report), AllSupported);
        Assert.Equal(["Fine."], result.Claims.Select(c => c.Text));
        Assert.Equal(2, result.OpenQuestions.Count(q => q.Contains("No citation at all.") || q.Contains("A URL is not a file.")));
    }

    [Fact]
    public async Task The_support_check_runs_on_the_branch_commit_and_its_verdict_is_kept()
    {
        using var repo = new Repo();
        EvidenceScope? seen = null;
        var result = await Build(Input(repo), (c, scope, _) =>
        {
            seen = scope;
            return Task.FromResult(SupportCheck.Apply(c, [new ClaimVerdict(0, SupportVerdict.Supported, ""), new ClaimVerdict(1, SupportVerdict.Unsupported, "the README has 5 lines but says nothing of it")]));
        });
        Assert.Equal(repo.Commit, seen!.Commit);
        Assert.Equal(repo.Dir.Path, seen.RepositoryPath);
        Assert.Equal(["The client retries twice."], result.Claims.Select(c => c.Text));
        Assert.Contains(result.OpenQuestions, q => q.Contains("Unsupported by its citation"));
    }

    [Fact]
    public async Task No_support_check_leaves_claims_unchecked_and_says_nothing_about_support()
    {
        using var repo = new Repo();
        var result = await Build(Input(repo), support: null);
        Assert.All(result.Claims, c => Assert.Null(c.Support));
    }

    // ---- when it did not end in a pull request ----

    [Fact]
    public async Task A_session_that_needs_input_is_needs_input_with_its_questions_and_no_branch_artifacts()
    {
        using var repo = new Repo();
        var result = await Build(Input(repo, Session(SessionStatus.NeedsInput, questions: ["Which database?", "Keep the old API?"]), report: null, handover: null));
        Assert.Equal(ResultStatus.NeedsInput, result.Status);
        Assert.Equal(["Which database?", "Keep the old API?"], result.OpenQuestions);
        Assert.DoesNotContain(result.Artifacts, a => a.Kind is "branch" or "pull-request" or "verification");
        AssertValid(result);
    }

    [Theory]
    [InlineData(SessionStatus.Stalled, "no_progress", ErrorCode.SessionStalled)]
    [InlineData(SessionStatus.Stalled, "repeating", ErrorCode.SessionStalled)]
    [InlineData(SessionStatus.TokenCap, "token_cap", ErrorCode.CostCapReached)]
    [InlineData(SessionStatus.Failed, "no_result", ErrorCode.SessionFailed)]
    public async Task A_session_that_ended_badly_is_failed_with_its_code_and_a_reason_and_a_handover_it_never_reached(SessionStatus status, string reason, ErrorCode code)
    {
        using var repo = new Repo();
        var result = await Build(Input(repo, Session(status, reason), report: null, handover: null));
        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Equal(code, result.Error!.Code);
        Assert.Contains(reason.Replace('_', ' '), result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.Error.Retryable);
        Assert.NotNull(result.Error.Action);
        AssertValid(result);
    }

    [Fact]
    public async Task A_finished_session_with_no_usable_report_is_invalid_result()
    {
        using var repo = new Repo();
        var result = await Build(Input(repo, report: "the session printed prose and no JSON", handover: null));
        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Equal(ErrorCode.InvalidResult, result.Error!.Code);
    }

    [Fact]
    public async Task A_branch_that_was_not_pushed_keeps_its_verification_and_says_why()
    {
        using var repo = new Repo();
        var refused = new HandoverOutcome(HandoverStatus.NotPushed, "the tests failed in chargehand's own verification (exit 1)", ErrorCode.VerificationFailed, "chargehand/run1", repo.Commit, null, Verified(1), ["Retry.cs"], [], []);
        var result = await Build(Input(repo, handover: refused), AllSupported);
        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Equal(ErrorCode.VerificationFailed, result.Error!.Code);
        Assert.Contains("verification", result.Error.Message);
        Assert.False(Artifact(result, "branch").GetProperty("pushed").GetBoolean());
        Assert.Equal(1, Artifact(result, "verification").GetProperty("exit_code").GetInt32());
        Assert.DoesNotContain(result.Artifacts, a => a.Kind == "pull-request");
        Assert.Equal(2, result.Claims.Count);                                            // the session's claims are still reported, marked by the failure
        AssertValid(result);
    }

    [Fact]
    public async Task A_pull_request_that_failed_after_the_push_still_names_the_pushed_branch()
    {
        using var repo = new Repo();
        var failed = new HandoverOutcome(HandoverStatus.PrFailed, "GitHub answered 403", ErrorCode.PrFailed, "chargehand/run1", repo.Commit, null, Verified(), ["Retry.cs"], [], []);
        var result = await Build(Input(repo, handover: failed), AllSupported);
        Assert.Equal(ErrorCode.PrFailed, result.Error!.Code);
        Assert.True(Artifact(result, "branch").GetProperty("pushed").GetBoolean());
        Assert.Contains("chargehand/run1", result.Error.Message);
        AssertValid(result);
    }

    [Fact]
    public async Task A_finding_in_the_diff_is_a_session_failure_naming_the_kind_and_never_the_value()
    {
        using var repo = new Repo();
        var blocked = new HandoverOutcome(HandoverStatus.NotPushed, "the diff looks like it carries a secret (aws in config.txt); nothing was pushed", ErrorCode.SessionFailed, "chargehand/run1", repo.Commit,
            null, null, ["config.txt"], [], [new ScanFinding("aws", "config.txt")]);
        var result = await Build(Input(repo, handover: blocked), AllSupported);
        Assert.Equal(ErrorCode.SessionFailed, result.Error!.Code);
        Assert.Contains("aws", result.Error.Message);
        Assert.DoesNotContain(result.Artifacts, a => a.Kind == "verification");
    }

    [Fact]
    public async Task A_result_can_be_signed_and_verified_like_any_other()
    {
        using var repo = new Repo();
        var result = await Build(Input(repo), AllSupported);
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var signed = ResultSignature.Sign(result, key);
        Assert.True(ResultSignature.Verify(JsonSerializer.Serialize(signed, ContractJson.Options), key).Valid);
    }

    // ---- a batch ----

    private static TaskOutcome Done(string id, string run, long tokens = 1000, decimal usd = 0) =>
        new(id, TaskState.Completed, run, null, null, usd, tokens, $"chargehand/{run}", $"https://example.test/o/r/pull/{id.Length}");

    [Fact]
    public void A_batch_where_every_task_ended_in_a_pull_request_is_completed_and_lists_each()
    {
        var outcome = new BatchOutcome([Done("t1", "run-a"), Done("t2", "run-b")], null);
        var result = DrivenResult.BuildBatch("run-batch", new string('0', 32), "Two changes", outcome, "oauth-token-via-gateway", priced: false, "2.1.283");
        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Null(result.Error);
        var rows = Artifact(result, "driven-batch").GetProperty("tasks").EnumerateArray().ToList();
        Assert.Equal(["t1", "t2"], rows.Select(r => r.GetProperty("id").GetString()));
        Assert.Equal(["run-a", "run-b"], rows.Select(r => r.GetProperty("run_id").GetString()));
        Assert.All(rows, r => Assert.Equal("completed", r.GetProperty("status").GetString()));
        Assert.Equal("oauth-token-via-gateway", Artifact(result, "driven-batch").GetProperty("credential_delivery").GetString());
        Assert.Equal(2000, result.Usage.Input + result.Usage.Output);
        AssertValid(result);
    }

    [Fact]
    public void A_batch_with_a_task_that_did_not_finish_is_failed_tasks_incomplete_and_names_them()
    {
        var outcome = new BatchOutcome([Done("t1", "run-a"), new TaskOutcome("t2", TaskState.Failed, "run-b", "the tests failed", ErrorCode.VerificationFailed), new TaskOutcome("t3", TaskState.NotStarted, null, "not started: the batch's cap leaves no room", ErrorCode.CostCapReached)], null);
        var result = DrivenResult.BuildBatch("run-batch", new string('0', 32), "Three changes", outcome, "env", priced: false, "2.1.283");
        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Equal(ErrorCode.TasksIncomplete, result.Error!.Code);
        Assert.False(result.Error.Retryable);
        Assert.Contains("2 of 3", result.Error.Message);
        Assert.Contains("t2", result.Error.Action);
        Assert.Contains("t3", result.Error.Action);
        Assert.Equal(2, result.OpenQuestions.Count);
        Assert.Contains(result.OpenQuestions, q => q.StartsWith("Task t2", StringComparison.Ordinal) && q.Contains("the tests failed"));
        var rows = Artifact(result, "driven-batch").GetProperty("tasks").EnumerateArray().ToList();
        Assert.Equal("verification_failed", rows[1].GetProperty("error_code").GetString());
        Assert.Equal("not_started", rows[2].GetProperty("status").GetString());
        AssertValid(result);
    }

    [Fact]
    public void A_rate_limited_batch_carries_the_switch_to_an_api_key_in_its_action()
    {
        var outcome = new BatchOutcome([new TaskOutcome("t1", TaskState.Failed, "run-a", "rate limited", ErrorCode.RateLimited), new TaskOutcome("t2", TaskState.NotStarted, null, "not started: the provider is rate limited or unavailable")], BatchScheduler.RateLimitedAction);
        var result = DrivenResult.BuildBatch("run-batch", new string('0', 32), "Two changes", outcome, "env", priced: false, "2.1.283");
        Assert.Contains("API key", result.Error!.Action);
        Assert.Contains("t1", result.Error.Action);
    }

    [Fact]
    public void Batch_dollars_are_summed_only_when_priced()
    {
        var outcome = new BatchOutcome([Done("t1", "a", usd: 0.5m), Done("t2", "b", usd: 0.25m)], null);
        Assert.Equal(0.75m, DrivenResult.BuildBatch("r", new string('0', 32), "d", outcome, "env", priced: true, "2.1.283").Usage.Usd);
        Assert.Null(DrivenResult.BuildBatch("r", new string('0', 32), "d", outcome, "env", priced: false, "2.1.283").Usage.Usd);
    }
}
