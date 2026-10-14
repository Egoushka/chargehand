using System.Text.Json;
using System.Text.Json.Serialization;
using Chargehand.Contracts;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Research.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Research.Tests;

/// <summary>The inline artifacts of a task's result moved from reflection JSON to source generation (ADR 0060). Each one is compared with what the reflection serializer wrote for the
/// anonymous type it replaced, byte for byte.</summary>
public class TaskArtifactsJsonTests
{
    private static readonly JsonSerializerOptions Reflection = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private const string Base = "1111111111111111111111111111111111111111";
    private const string Commit = "2222222222222222222222222222222222222222";

    private sealed class NoFailures : IEvidenceResolver
    {
        public Task<IReadOnlyList<EvidenceFailure>> ResolveAsync(ResultContract contract, EvidenceScope scope, CancellationToken ct) => Task.FromResult<IReadOnlyList<EvidenceFailure>>([]);
    }

    private static DrivenTaskInput Input(HandoverOutcome handover, string report, KeptWork? kept = null) =>
        new("t1", "run1", new string('0', 32), "model-a", "2.1.283",
            new SessionOutcome(SessionStatus.Completed, "", 12, 90_000, 4_000, 300_000, 0.42m, 0, [], true, StreamSha256: new string('a', 64)), report, handover,
            [new RunSummary("run-summary/v1", "run-child-1", RunState.Completed, "default", DateTimeOffset.UtcNow, "run1"),
             new RunSummary("run-summary/v1", "run-child-2", RunState.NeedsInput, "review", DateTimeOffset.UtcNow, "run1")],
            "/nowhere", Base, false, kept);

    private static string Old(object value) => JsonSerializer.Serialize(value, Reflection);

    private static string Content(ResultContract result, string kind) => result.Artifacts.Single(a => a.Kind == kind).Content!;

    [Fact]
    public async Task Every_inline_artifact_of_a_pushed_task_is_written_as_the_anonymous_types_wrote_it()
    {
        var verification = new VerificationRecord(["npm", "test"], "detected", 0, "ok \"quoted\" <tail>", false, TimeSpan.FromSeconds(41.26), true);
        var changes = new ChangeSummary([new FileChange("Retry.cs", 12, 3), new FileChange("logo.png", null, null)], "diff --git a/Retry.cs b/Retry.cs\n+retry é\n", false);
        var flag = new TestIntegrityFlag(["tests/RetryTests.cs"], ["scripts/check.sh"]);
        var handover = new HandoverOutcome(HandoverStatus.Pushed, "", null, "chargehand/run1", Commit, new PullRequestRef("https://example.test/o/r/pull/7", 7), verification,
            ["Retry.cs"], ["package.json"], [], changes, flag);
        var kept = new KeptWork("chargehand/run0", "run0", 2, Base);

        var result = await TaskResultBuilder.BuildAsync(Input(handover, """{"summary":"Added a retry.","claims":[],"tests":{"command":"npm test","exit_code":3}}""", kept), new NoFailures(), null, default);

        Assert.Equal(Old(new { runs = new[] { ("run-child-1", "default", "completed"), ("run-child-2", "review", "needs_input") }.Select(c => new { run_id = c.Item1, preset = c.Item2, status = c.Item3 }) }),
            Content(result, "review"));
        Assert.Equal(Old(kept), Content(result, KeptWork.ArtifactKind));
        Assert.Equal(Old(new { branch = handover.Branch, commit = handover.Commit, @base = Base, pushed = true }), Content(result, "branch"));
        Assert.Equal(Old(new
        {
            @base = Base,
            commit = Commit,
            files_total = 2,
            files = changes.Files.Select(f => new { path = f.Path, added = f.Added, removed = f.Removed, binary = f.Added is null && f.Removed is null }),
            diff = changes.Diff,
            diff_truncated = false,
        }), Content(result, "changes"));
        Assert.Equal(Old(new { url = "https://example.test/o/r/pull/7", number = 7, draft = true, head = handover.Branch }), Content(result, "pull-request"));
        Assert.Equal(Old(new
        {
            argv = verification.Argv,
            source = verification.Source,
            sandbox = "container",
            network = true,
            exit_code = 0,
            passed = true,
            timed_out = false,
            attempts = 1,
            duration_seconds = Math.Round(verification.Duration.TotalSeconds, 1),
            output_tail = verification.OutputTail,
            changed_verification_paths = handover.ChangedVerificationPaths,
            test_integrity = new { flagged = true, reason = flag.Reason, test_paths = flag.TestPaths, build_paths = flag.BuildPaths },
        }), Content(result, "verification"));
        Assert.Equal(Old(new { claimed_by = "session", command = "npm test", exit_code = (int?)3 }), Content(result, "session-tests"));
    }

    [Fact]
    public async Task The_artifacts_of_a_branch_that_was_not_pushed_keep_their_nulls_as_before()
    {
        var verification = new VerificationRecord(["make", "test"], "request", 2, "2 failed", true, TimeSpan.FromMilliseconds(1250), false);
        var changes = new ChangeSummary([new FileChange("a.cs", 1, 0)], null, true);
        var handover = new HandoverOutcome(HandoverStatus.NotPushed, "the tests failed", ErrorCode.VerificationFailed, null, null, null, verification, [], [], [], changes);

        var result = await TaskResultBuilder.BuildAsync(Input(handover, """{"summary":"Tried.","claims":[]}"""), new NoFailures(), null, default);

        Assert.Equal(Old(new { branch = (string?)null, commit = (string?)null, @base = Base, pushed = false }), Content(result, "branch"));
        Assert.Equal(Old(new
        {
            @base = Base,
            commit = (string?)null,
            files_total = 1,
            files = changes.Files.Select(f => new { path = f.Path, added = f.Added, removed = f.Removed, binary = f.Added is null && f.Removed is null }),
            diff = (string?)null,
            diff_truncated = true,
        }), Content(result, "changes"));
        Assert.Equal(Old(new
        {
            argv = verification.Argv,
            source = "request",
            sandbox = "container",
            network = false,
            exit_code = 2,
            passed = false,
            timed_out = true,
            attempts = 1,
            duration_seconds = Math.Round(verification.Duration.TotalSeconds, 1),
            output_tail = "2 failed",
            changed_verification_paths = handover.ChangedVerificationPaths,
            test_integrity = (object?)null,
        }), Content(result, "verification"));
        Assert.DoesNotContain(result.Artifacts, a => a.Kind is "pull-request" or "session-tests" or "kept-work");
    }
}
