using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Journal.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;

namespace Chargehand.Modules.Handover.Api;

/// <param name="OutDirectory">The session's output directory: <c>chargehand.bundle</c> is in it.</param>
/// <param name="SourceRepository">chargehand's own checkout of the pinned commit (ADR 0023): where the scratch clone comes from.</param>
/// <param name="BaseCommit">The commit the session started from.</param>
/// <param name="Branch">Under <c>chargehand/</c>; the only kind of ref this ever pushes.</param>
/// <param name="BaseBranch">The branch the pull request is opened against.</param>
/// <param name="Secrets">Literal values a diff must not contain (the credentials the session had); the push credential is added to them.</param>
/// <param name="VerifyCommand">The request's own verification command; null: detect the repository's.</param>
/// <param name="AllowCiChanges">Push a branch that changes CI configuration; off by default.</param>
/// <param name="AllowNoTests">Push a branch of a repository with no test command; off by default.</param>
/// <param name="AllowLocalRemote">Accept a <c>file://</c> remote (tests); a real deployment pushes to https or ssh only.</param>
/// <param name="Report">Told each step as it ends: <c>verify_finished</c>, <c>pushed</c>, <c>pr_opened</c>.</param>
/// <param name="Partial">The session did not finish (ADR 0044): the tests are run and recorded, but a failing run, a timeout or a repository with no test command does not stop the push. A secret in the diff, a CI change and every other check still do.</param>
/// <param name="PolicyNote">What the pull request body says chargehand does next (ADR 0046), for a repository that opted in to integration; null: the draft is not merged and chargehand will not merge it.</param>
/// <param name="UpdateReused">The title and body replace those of a pull request the host already had for the branch (a task continued on it), as a finished task's replace the <c>[partial]</c> ones. Off, a reused pull request is left as it is.</param>
/// <param name="Goal">The task's goal; with it, a passing branch that changes only tests and build files is flagged unless the goal asks for that work (<see cref="TestIntegrity"/>). Null: not assessed.</param>
/// <param name="LocalOnly">The repository is local-only (ADR 0069): the hand-over refuses before any git or forge call.</param>
/// <param name="Provenance">The run that wrote the branch (ADR 0074): its trailers go on every commit past the base that has none, and at the end of the pull request body, so a squash merge keeps them. Null: no trailers.</param>
/// <param name="Bare">The benchmark's control arm (ADR 0080): the scans and the CI-change refusal still run, then the branch is pushed with no test run and no pull request.</param>
/// <param name="CiVerifies">ADR 0078: the repository's CI verifies (<c>driven.environment: "mise"</c>). No fresh-container verification runs; the pull request says which checks decide and that the
/// pre-push check is a filter. A repository with no CI and no check is refused unless the change is documentation only.</param>
/// <param name="PrePushCheck">With <paramref name="CiVerifies"/>: the check the session driver ran before the push (<c>VerifyCommand.Declared</c>); null: none was declared.</param>
/// <param name="FallbackBaseBranch">For a pull request stacked on another task's branch (ADR 0049), <paramref name="BaseBranch"/> is that branch: if the host refuses it, the pull request is opened against this one instead and says so.</param>
public sealed record HandoverInput(string RunId, string OutDirectory, string SourceRepository, string BaseCommit, string Branch, string RemoteUrl, string BaseBranch,
    string Title, string Body, string ScratchRoot, PushCredential? Credential, IReadOnlyList<string>? VerifyCommand = null, IReadOnlyList<string>? Secrets = null,
    bool AllowCiChanges = false, bool AllowNoTests = false, bool AllowLocalRemote = false, TimeSpan? VerifyTimeout = null, Action<HandoverEvent>? Report = null, bool Partial = false, string? PolicyNote = null, string? Goal = null, bool UpdateReused = false, string? FallbackBaseBranch = null, bool LocalOnly = false, Provenance? Provenance = null, bool Bare = false,
    bool CiVerifies = false, IReadOnlyList<string>? PrePushCheck = null);

/// <summary>One step of a handover that has just ended; <paramref name="Detail"/> never carries a credential.</summary>
public sealed record HandoverEvent(RunEventKind Kind, string Detail, string? PrUrl = null);

/// <summary>A verification run in a fresh container on the branch as the bundle holds it (ADR 0035's verifier, now in a container).</summary>
public sealed record BranchVerification(string RunId, string BundlePath, string Branch, IReadOnlyList<string> Argv, TimeSpan Timeout);

/// <param name="Source">"request" or "detected", as in <see cref="VerifyPlan"/>.</param>
public sealed record VerificationRecord(IReadOnlyList<string> Argv, string Source, int ExitCode, string OutputTail, bool TimedOut, TimeSpan Duration, bool Network);

public interface IBranchVerifier
{
    Task<VerificationRecord> VerifyAsync(BranchVerification verification, CancellationToken ct);
}

public interface IBranchHandover
{
    Task<HandoverOutcome> RunAsync(HandoverInput input, CancellationToken ct);
}

public enum HandoverStatus { Pushed, PrFailed, NotPushed }

/// <param name="Reason">Why it was not pushed, or why the pull request failed; a credential is never in it.</param>
/// <param name="Changes">The branch against its base, once the bundle is fetched and the branch changes something; null before that.</param>
public sealed record HandoverOutcome(HandoverStatus Status, string Reason, ErrorCode? Error, string? Branch, string? Commit, PullRequestRef? PullRequest, VerificationRecord? Verification,
    IReadOnlyList<string> ChangedPaths, IReadOnlyList<string> ChangedVerificationPaths, IReadOnlyList<ScanFinding> Findings, ChangeSummary? Changes = null, TestIntegrityFlag? TestIntegrity = null);

/// <summary>One path the branch changes, from <c>git diff --numstat --no-renames</c>: a rename is a delete and an add. Null counts: a binary file.</summary>
public sealed record FileChange(string Path, int? Added, int? Removed);

/// <param name="Diff">The unified diff, cut at a line within <see cref="Handover.MaxChangesDiffBytes"/>; null when the diff looked like it carried a secret.</param>
public sealed record ChangeSummary(IReadOnlyList<FileChange> Files, string? Diff, bool DiffTruncated);

/// <summary>What happens to a session's branch once the session is over (ADR 0039), all of it outside any container. chargehand makes a scratch clone of its own checkout,
/// fetches the session's bundle into it (a bundle is objects and refs, no configuration, no hooks), and then checks the branch itself: it descends from the base, it changes
/// something, its diff carries no secret and no CI configuration, and the repository's tests pass in a fresh container. Only then does it push <c>chargehand/&lt;run&gt;</c>
/// (never anything else, never forced) with a credential that cannot push the default branch, and open a <b>draft</b> pull request. It never merges. A session that says
/// its tests pass is not believed: the verification here is chargehand's own. With <paramref name="steps"/>, the verification, the push and the pull request are recorded steps of batch <paramref name="batchId"/>
/// (ADR 0061), keyed by the handed-over run: a finished one is not done again, and one a restart cut short first looks for what it may have left (the branch at the commit, an open pull request
/// for the branch).</summary>
/// <param name="pause">Waits between push attempts (<see cref="PushPauses"/>); tests pass one that does not.</param>
public sealed partial class Handover(IBranchVerifier verifier, IPullRequests pullRequests, DrivenForge? forge = null, DrivenJournal? steps = null, string? batchId = null,
    Func<TimeSpan, CancellationToken, Task>? pause = null) : IBranchHandover
{
    private readonly Func<TimeSpan, CancellationToken, Task> _pause = pause ?? ((by, ct) => Task.Delay(by, ct));

    private const int MaxDiffBytes = 8 * 1024 * 1024;

    /// <summary>The most of the unified diff a <see cref="ChangeSummary"/> keeps, so it fits an inline artifact (64 KiB) beside the paths.</summary>
    public const int MaxChangesDiffBytes = 32 * 1024;
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(5);

    public async Task<HandoverOutcome> RunAsync(HandoverInput input, CancellationToken ct)
    {
        if (!WellFormed(input))
            throw new ArgumentException("the run id, branch (chargehand/<name>) or base commit is not well formed");
        List<string> secrets = [.. input.Secrets ?? []];
        if (input.Credential is { } credential)
            secrets.Add(credential.Value);
        var run = new HandoverRun(input, secrets);
        if (input.LocalOnly)
            return run.Refuse("the repository is local-only: nothing is pushed and no pull request is opened", ErrorCode.LocalOnly);
        if (await FetchAsync(run, ct) is { } notFetched)
            return notFetched;
        if (await ScanDiffAsync(run, ct) is { } diffRefused)
            return diffRefused;
        if (await ScanHistoryAsync(run, ct) is { } historyRefused)
            return historyRefused;
        if (await SummarizeAsync(run, ct) is { } ciRefused)
            return ciRefused;
        if (await VerifyBranchAsync(run, ct) is { } notVerified)
            return notVerified;
        if (await StampAsync(run, ct) is { } notStamped)
            return notStamped;
        if (await PushBranchAsync(run, ct) is { } notPushed)
            return notPushed;
        return await OpenAsync(run, ct);
    }

    private static bool WellFormed(HandoverInput input) =>
        RunIdPattern().IsMatch(input.RunId) && BranchPattern().IsMatch(input.Branch) && !input.Branch.Contains("..", StringComparison.Ordinal) && CommitPattern().IsMatch(input.BaseCommit);

    /// <summary>The scratch clone of chargehand's checkout with the session's branch fetched from the bundle; it must descend from the base.</summary>
    private static async Task<HandoverOutcome?> FetchAsync(HandoverRun run, CancellationToken ct)
    {
        var input = run.Input;
        if (!File.Exists(run.Bundle))
            return run.Refuse("no bundle: the session made no branch", ErrorCode.SessionFailed);
        if (!RemoteAllowed(input.RemoteUrl, input.AllowLocalRemote))
            return run.Refuse("the remote is not an https or ssh remote", ErrorCode.PushRejected);

        var scratch = run.Scratch;
        if (Directory.Exists(scratch))
            Directory.Delete(scratch, recursive: true);
        var env = run.Env;
        var clone = await GitProcess.RunAsync(input.ScratchRoot, env, ["-c", "core.hooksPath=/dev/null", "clone", "--quiet", "--no-hardlinks", "--template=", "--", input.SourceRepository, scratch], GitTimeout, ct);
        if (clone.ExitCode != 0)
            return run.Refuse($"chargehand could not clone its checkout: {clone.Tail}", ErrorCode.SessionFailed);
        var verify = await GitProcess.RunAsync(scratch, env, ["bundle", "verify", run.Bundle], GitTimeout, ct);
        if (verify.ExitCode != 0)
            return run.Refuse($"the bundle does not verify against the base: {verify.Tail}", ErrorCode.SessionFailed);
        var fetch = await GitProcess.RunAsync(scratch, env, ["-c", "fetch.fsckObjects=true", "-c", "core.hooksPath=/dev/null", "fetch", "--quiet", run.Bundle, $"refs/heads/{input.Branch}:refs/heads/{input.Branch}"], GitTimeout, ct);
        if (fetch.ExitCode != 0)
            return run.Refuse($"the bundle has no usable {input.Branch}: {fetch.Tail}", ErrorCode.SessionFailed);
        var commit = run.Commit = (await GitProcess.RunAsync(scratch, env, ["rev-parse", $"refs/heads/{input.Branch}"], GitTimeout, ct)).Stdout.Trim();
        if ((await GitProcess.RunAsync(scratch, env, ["merge-base", "--is-ancestor", input.BaseCommit, commit], GitTimeout, ct)).ExitCode != 0)
            return run.Refuse("the branch does not descend from the commit the session started at", ErrorCode.SessionFailed, commit: commit);
        return null;
    }

    /// <summary>The branch against its base: it changes something, and its diff carries no secret.</summary>
    private static async Task<HandoverOutcome?> ScanDiffAsync(HandoverRun run, CancellationToken ct)
    {
        var (input, scratch, env, commit) = (run.Input, run.Scratch, run.Env, run.Commit);
        var names = await GitProcess.RunAsync(scratch, env, ["diff", "--name-only", "-z", input.BaseCommit, commit], GitTimeout, ct);
        run.Changed = [.. names.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        var changed = run.Changed;
        if (changed.Count == 0)
            return run.Refuse("the branch has no change against its base", ErrorCode.SessionFailed, commit: commit);
        var numstat = await GitProcess.RunAsync(scratch, env, ["diff", "--numstat", "--no-renames", "-z", input.BaseCommit, commit], GitTimeout, ct, MaxDiffBytes);
        run.Changes = new ChangeSummary(ParseNumstat(numstat.Stdout), null, false);
        var diff = await GitProcess.RunAsync(scratch, env, ["diff", "--no-color", "-U0", input.BaseCommit, commit], GitTimeout, ct, MaxDiffBytes);
        if (diff.Truncated)
            return run.Refuse("the diff is too large to scan", ErrorCode.SessionFailed, changed: changed, commit: commit);
        var findings = DiffScan.Scan(diff.Stdout, run.Secrets, changed);
        if (findings.Count > 0)
            return run.Refuse($"the diff looks like it carries a secret ({string.Join(", ", findings.Select(f => $"{f.Kind} in {f.Path}"))}); nothing was pushed", ErrorCode.SessionFailed, changed: changed, findings: findings, commit: commit);
        return null;
    }

    /// <summary>The branch is pushed with its history: a secret a commit added and a later commit removed is on the remote all the same.</summary>
    private static async Task<HandoverOutcome?> ScanHistoryAsync(HandoverRun run, CancellationToken ct)
    {
        var (input, scratch, env, commit, changed) = (run.Input, run.Scratch, run.Env, run.Commit, run.Changed);
        var history = await GitProcess.RunAsync(scratch, env, ["log", "-m", "-p", "-U0", "--no-color", "--no-ext-diff", "--no-textconv", "--no-renames", "--format=", $"{input.BaseCommit}..{commit}"], GitTimeout, ct, MaxDiffBytes);
        if (history.Truncated)
            return run.Refuse("the branch's history is too large to scan", ErrorCode.SessionFailed, changed: changed, commit: commit);
        var touched = (await GitProcess.RunAsync(scratch, env, ["log", "-m", "--name-only", "-z", "--no-renames", "--format=", $"{input.BaseCommit}..{commit}"], GitTimeout, ct, MaxDiffBytes))
            .Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim('\n')).Where(n => n.Length > 0).Distinct().ToList();
        var historyFindings = DiffScan.Scan(history.Stdout, run.Secrets, touched);
        if (historyFindings.Count > 0)
            return run.Refuse($"a commit of the branch looks like it carries a secret ({string.Join(", ", historyFindings.Select(f => $"{f.Kind} in {f.Path}"))}); nothing was pushed", ErrorCode.SessionFailed, changed: changed, findings: historyFindings, commit: commit);
        return null;
    }

    /// <summary>The redacted unified diff for the <see cref="ChangeSummary"/>, and the refusal of a change to CI configuration.</summary>
    private static async Task<HandoverOutcome?> SummarizeAsync(HandoverRun run, CancellationToken ct)
    {
        var (input, commit, changed) = (run.Input, run.Commit, run.Changed);
        var unified = await GitProcess.RunAsync(run.Scratch, run.Env, ["diff", "--no-color", "--no-ext-diff", "--no-textconv", input.BaseCommit, commit], GitTimeout, ct, MaxChangesDiffBytes);
        run.Changes = run.Changes! with { Diff = CutAtLine(Redact(unified.Stdout, run.Secrets), unified.Truncated, out var cut), DiffTruncated = cut };
        var ci = DiffScan.CiPaths(changed);
        if (ci.Count > 0 && !input.AllowCiChanges)
            return run.Refuse($"the change edits CI configuration ({string.Join(", ", ci)}); chargehand does not push that, because pushing can run it", ErrorCode.SessionFailed, changed: changed, commit: commit);
        return null;
    }

    /// <summary>The branch checked out and its tests run by chargehand itself; a session that says its tests pass is not believed.</summary>
    private async Task<HandoverOutcome?> VerifyBranchAsync(HandoverRun run, CancellationToken ct)
    {
        var (input, commit, changed) = (run.Input, run.Commit, run.Changed);
        var checkout = await GitProcess.RunAsync(run.Scratch, run.Env, ["-c", "core.hooksPath=/dev/null", "checkout", "--quiet", input.Branch], GitTimeout, ct);
        if (checkout.ExitCode != 0)
            return run.Refuse($"chargehand could not check the branch out: {checkout.Tail}", ErrorCode.SessionFailed, changed: changed, commit: commit);
        if (input.Bare)
            return null;
        if (input.CiVerifies)
            return await CiVerdictAsync(run, ct);
        var plan = VerifyCommand.Resolve(input.VerifyCommand, run.Scratch);
        run.VerificationPaths = VerifyCommand.VerificationPaths(changed, plan);
        if (plan is null)
        {
            if (!input.AllowNoTests && !input.Partial && (run.NoTestsRule = await NoTestsRuleAsync(run, ct)) is null)
                return run.Refuse(NoTestCommandMessage(run.Scratch), ErrorCode.VerificationFailed, changed: changed, commit: commit);
            return null;
        }
        return await RunVerificationAsync(run, plan, ct);
    }

    private async Task<HandoverOutcome?> RunVerificationAsync(HandoverRun run, VerifyPlan plan, CancellationToken ct)
    {
        var input = run.Input;
        VerificationRecord verification;
        try
        {
            verification = await VerifyAsync(new BranchVerification(input.RunId, run.Bundle, input.Branch, plan.Argv, input.VerifyTimeout ?? TimeSpan.FromMinutes(10)), plan.Source, run.Secrets, ct);
        }
        catch (Exception e) when (input.Partial && e is not OperationCanceledException)
        {
            // The work is kept whether or not the tests could run; say that they did not.
            verification = new VerificationRecord(plan.Argv, plan.Source, -1, Redact($"the verification could not run: {e.Message}", run.Secrets), false, TimeSpan.Zero, false);
        }
        run.Verification = verification;
        input.Report?.Invoke(new HandoverEvent(RunEventKind.VerifyFinished, VerifyDetail(verification)));
        if ((verification.ExitCode != 0 || verification.TimedOut) && !input.Partial)
            return run.Refuse(verification.TimedOut ? "the tests timed out in chargehand's own verification" : $"the tests failed in chargehand's own verification (exit {verification.ExitCode})", ErrorCode.VerificationFailed,
                verification, run.Changed, verificationPaths: run.VerificationPaths, commit: run.Commit);
        return null;
    }

    private static string VerifyDetail(VerificationRecord verification)
    {
        var passed = verification.ExitCode == 0 && !verification.TimedOut;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"tests {(passed ? "passed" : verification.TimedOut ? "timed out" : $"failed (exit {verification.ExitCode})")} in {verification.Duration.TotalSeconds:0}s: {string.Join(' ', verification.Argv)}");
    }

    private async Task<HandoverOutcome?> PushBranchAsync(HandoverRun run, CancellationToken ct)
    {
        var input = run.Input;
        PushedBranch pushed;
        try
        {
            pushed = await PushAsync(input, run.Scratch, run.Commit, run.Env, run.Secrets, ct);
        }
        catch (PushRejectedException e)
        {
            return run.Refuse($"the push was rejected: {e.Message}", ErrorCode.PushRejected, run.Verification, run.Changed, verificationPaths: run.VerificationPaths, commit: run.Commit);
        }
        catch (GitHubAppException e)
        {
            return run.Refuse($"no push credential: {e.Message}", ErrorCode.CredentialUnavailable, run.Verification, run.Changed, verificationPaths: run.VerificationPaths, commit: run.Commit);
        }
        input.Report?.Invoke(new HandoverEvent(RunEventKind.Pushed, pushed.Adopted ? $"{input.Branch} was already on the remote" : $"pushed {input.Branch}"));
        return null;
    }

    /// <summary>The draft pull request for the pushed branch; a failure here leaves the push in place.</summary>
    private async Task<HandoverOutcome> OpenAsync(HandoverRun run, CancellationToken ct)
    {
        var (input, commit, changed, verification, verificationPaths, changes) = (run.Input, run.Commit, run.Changed, run.Verification, run.VerificationPaths, run.Changes);
        if (input.Bare)
            return new(HandoverStatus.Pushed, "", null, input.Branch, commit, null, null, changed, [], [], changes);
        var integrity = input.Goal is { } goal && verification is { ExitCode: 0, TimedOut: false } ? TestIntegrity.Assess(changed, goal) : null;
        var repository = RemoteRepository.Parse(input.RemoteUrl, forge) ?? (input.AllowLocalRemote ? new RemoteRepository("local", "local") : null);
        if (repository is null)
            return new(HandoverStatus.PrFailed, "the remote's owner and repository could not be read from its URL", ErrorCode.PrFailed, input.Branch, commit, null, verification, changed, verificationPaths, [], changes, integrity);
        try
        {
            var prBody = DescribeBody(input.Body, changed, verification, verificationPaths, input.PolicyNote, integrity, run.NoTestsRule, run.CiVerdict);
            var (pr, bodyUsed) = await OpenPullRequestAsync(input, repository, prBody, ct);
            var detail = pr.Reused && input.UpdateReused ? await UpdateReusedAsync(run, repository, pr, bodyUsed, ct) : "draft pull request opened";
            input.Report?.Invoke(new HandoverEvent(RunEventKind.PrOpened, detail, pr.Url));
            return new(HandoverStatus.Pushed, "", null, input.Branch, commit, pr, verification, changed, verificationPaths, [], changes, integrity);
        }
        catch (PullRequestException e)
        {
            return new(HandoverStatus.PrFailed, Redact(e.Message, run.Secrets), ErrorCode.PrFailed, input.Branch, commit, null, verification, changed, verificationPaths, [], changes, integrity);
        }
    }

    /// <summary>The pull request and the push are there either way; a title that stays stale is said, not an error.</summary>
    private async Task<string> UpdateReusedAsync(HandoverRun run, RemoteRepository repository, PullRequestRef pr, string body, CancellationToken ct)
    {
        try
        {
            await pullRequests.EditAsync(repository, pr.Number, run.Input.Title, body, ct);
            return "draft pull request reused and its title and body updated";
        }
        catch (PullRequestException e)
        {
            return Redact($"draft pull request reused; its title and body could not be updated: {e.Message}", run.Secrets);
        }
    }

    /// <summary>What one handover has found so far, so that every phase of <see cref="RunAsync"/> refuses with the same fields.</summary>
    private sealed class HandoverRun(HandoverInput input, List<string> secrets)
    {
        public HandoverInput Input { get; } = input;

        /// <summary>The request's secrets and the push credential: a diff must not carry them, and no reason or detail shows them.</summary>
        public List<string> Secrets { get; } = secrets;

        public string Bundle { get; } = Path.Combine(input.OutDirectory, "chargehand.bundle");

        public string Scratch { get; } = Path.Combine(input.ScratchRoot, input.RunId);

        public Dictionary<string, string> Env { get; } = GitProcess.Environment(input.ScratchRoot);

        public string Commit { get; set; } = "";

        public List<string> Changed { get; set; } = [];

        public ChangeSummary? Changes { get; set; }

        public VerificationRecord? Verification { get; set; }

        public IReadOnlyList<string> VerificationPaths { get; set; } = [];

        /// <summary>Why a branch with no test command was pushed all the same (<see cref="NoTestsRuleAsync"/>); null otherwise.</summary>
        public string? NoTestsRule { get; set; }

        /// <summary>With <see cref="HandoverInput.CiVerifies"/>: what the pull request says verifies it (<see cref="CiVerdictAsync"/>); null otherwise.</summary>
        public string? CiVerdict { get; set; }

        public HandoverOutcome Refuse(string reason, ErrorCode error, VerificationRecord? verification = null, IReadOnlyList<string>? changed = null, IReadOnlyList<ScanFinding>? findings = null,
            IReadOnlyList<string>? verificationPaths = null, string? commit = null) =>
            new(HandoverStatus.NotPushed, Redact(reason, Secrets), error, Input.Branch, commit, null, verification, changed ?? [], verificationPaths ?? [], findings ?? [], Changes);
    }

    /// <summary><c>added\tremoved\tpath\0</c> per path; a binary file has <c>-</c> for both counts.</summary>
    private static List<FileChange> ParseNumstat(string output)
    {
        List<FileChange> files = [];
        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = record.Split('\t', 3);
            if (parts.Length == 3)
                files.Add(new FileChange(parts[2], Count(parts[0]), Count(parts[1])));
        }
        return files;

        static int? Count(string s) => int.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>At most <see cref="MaxChangesDiffBytes"/> bytes of <paramref name="diff"/>, ending at a line; <paramref name="cut"/> says whether anything was left out.</summary>
    private static string CutAtLine(string diff, bool truncated, out bool cut)
    {
        cut = truncated || Encoding.UTF8.GetByteCount(diff) > MaxChangesDiffBytes;
        if (!cut)
            return diff;
        var bytes = Encoding.UTF8.GetBytes(diff);
        var end = Array.LastIndexOf(bytes, (byte)'\n', Math.Min(bytes.Length, MaxChangesDiffBytes) - 1);
        return end < 0 ? "" : Encoding.UTF8.GetString(bytes, 0, end + 1);
    }

    private static string Redact(string text, IReadOnlyList<string> secrets)
    {
        foreach (var secret in secrets)
        {
            if (secret.Length < 8)
                continue;
            text = text.Replace(secret, "[redacted]", StringComparison.Ordinal)
                .Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{secret}")), "[redacted]", StringComparison.Ordinal);
        }
        return text;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_.-]{0,62}$")]
    private static partial Regex RunIdPattern();

    [GeneratedRegex(@"^chargehand/[a-z0-9][a-z0-9_-]*(/[a-z0-9][a-z0-9_-]*)*$")]
    private static partial Regex BranchPattern();

    [GeneratedRegex(@"^[0-9a-f]{7,40}$")]
    private static partial Regex CommitPattern();
}
