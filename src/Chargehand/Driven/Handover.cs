using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Verification;

namespace Chargehand.Driven;

public enum PushCredentialKind { HttpsToken, SshKey }

/// <summary>What pushes a branch: a fine-grained token or a deploy key that cannot push the default branch. It is read from a secret source at push
/// time and is never inside a container.</summary>
public sealed record PushCredential(string Value, PushCredentialKind Kind = PushCredentialKind.HttpsToken);

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
public sealed record HandoverInput(string RunId, string OutDirectory, string SourceRepository, string BaseCommit, string Branch, string RemoteUrl, string BaseBranch,
    string Title, string Body, string ScratchRoot, PushCredential? Credential, IReadOnlyList<string>? VerifyCommand = null, IReadOnlyList<string>? Secrets = null,
    bool AllowCiChanges = false, bool AllowNoTests = false, bool AllowLocalRemote = false, TimeSpan? VerifyTimeout = null);

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
public sealed record HandoverOutcome(HandoverStatus Status, string Reason, ErrorCode? Error, string? Branch, string? Commit, PullRequestRef? PullRequest, VerificationRecord? Verification,
    IReadOnlyList<string> ChangedPaths, IReadOnlyList<string> ChangedVerificationPaths, IReadOnlyList<ScanFinding> Findings);

/// <summary>What happens to a session's branch once the session is over (ADR 0039), all of it outside any container. chargehand makes a scratch clone of its own checkout,
/// fetches the session's bundle into it (a bundle is objects and refs, no configuration, no hooks), and then checks the branch itself: it descends from the base, it changes
/// something, its diff carries no secret and no CI configuration, and the repository's tests pass in a fresh container. Only then does it push <c>chargehand/&lt;run&gt;</c>
/// (never anything else, never forced) with a credential that cannot push the default branch, and open a <b>draft</b> pull request. It never merges. A session that says
/// its tests pass is not believed: the verification here is chargehand's own.</summary>
public sealed partial class Handover(IBranchVerifier verifier, IPullRequests pullRequests) : IBranchHandover
{
    private const int MaxDiffBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(5);

    public async Task<HandoverOutcome> RunAsync(HandoverInput input, CancellationToken ct)
    {
        if (!RunIdPattern().IsMatch(input.RunId) || !BranchPattern().IsMatch(input.Branch) || input.Branch.Contains("..", StringComparison.Ordinal)
            || !CommitPattern().IsMatch(input.BaseCommit))
            throw new ArgumentException("the run id, branch (chargehand/<name>) or base commit is not well formed");
        List<string> secrets = [.. input.Secrets ?? []];
        if (input.Credential is { } credential)
            secrets.Add(credential.Value);
        HandoverOutcome Refuse(string reason, ErrorCode error, VerificationRecord? verification = null, IReadOnlyList<string>? changed = null, IReadOnlyList<ScanFinding>? findings = null,
            IReadOnlyList<string>? verificationPaths = null, string? commit = null) =>
            new(HandoverStatus.NotPushed, Redact(reason, secrets), error, input.Branch, commit, null, verification, changed ?? [], verificationPaths ?? [], findings ?? []);

        var bundle = Path.Combine(input.OutDirectory, "chargehand.bundle");
        if (!File.Exists(bundle))
            return Refuse("no bundle: the session made no branch", ErrorCode.SessionFailed);
        if (!RemoteAllowed(input.RemoteUrl, input.AllowLocalRemote))
            return Refuse("the remote is not an https or ssh remote", ErrorCode.PushRejected);

        var scratch = Path.Combine(input.ScratchRoot, input.RunId);
        if (Directory.Exists(scratch))
            Directory.Delete(scratch, recursive: true);
        var env = GitEnvironment(input.ScratchRoot);
        var clone = await Git.RunAsync(input.ScratchRoot, env, ["-c", "core.hooksPath=/dev/null", "clone", "--quiet", "--no-hardlinks", "--template=", "--", input.SourceRepository, scratch], GitTimeout, ct);
        if (clone.ExitCode != 0)
            return Refuse($"chargehand could not clone its checkout: {clone.Tail}", ErrorCode.SessionFailed);
        var verify = await Git.RunAsync(scratch, env, ["bundle", "verify", bundle], GitTimeout, ct);
        if (verify.ExitCode != 0)
            return Refuse($"the bundle does not verify against the base: {verify.Tail}", ErrorCode.SessionFailed);
        var fetch = await Git.RunAsync(scratch, env, ["-c", "fetch.fsckObjects=true", "-c", "core.hooksPath=/dev/null", "fetch", "--quiet", bundle, $"refs/heads/{input.Branch}:refs/heads/{input.Branch}"], GitTimeout, ct);
        if (fetch.ExitCode != 0)
            return Refuse($"the bundle has no usable {input.Branch}: {fetch.Tail}", ErrorCode.SessionFailed);
        var commit = (await Git.RunAsync(scratch, env, ["rev-parse", $"refs/heads/{input.Branch}"], GitTimeout, ct)).Stdout.Trim();
        if ((await Git.RunAsync(scratch, env, ["merge-base", "--is-ancestor", input.BaseCommit, commit], GitTimeout, ct)).ExitCode != 0)
            return Refuse("the branch does not descend from the commit the session started at", ErrorCode.SessionFailed, commit: commit);

        var names = await Git.RunAsync(scratch, env, ["diff", "--name-only", "-z", input.BaseCommit, commit], GitTimeout, ct);
        List<string> changed = [.. names.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        if (changed.Count == 0)
            return Refuse("the branch has no change against its base", ErrorCode.SessionFailed, commit: commit);
        var diff = await Git.RunAsync(scratch, env, ["diff", "--no-color", "-U0", input.BaseCommit, commit], GitTimeout, ct, MaxDiffBytes);
        if (diff.Truncated)
            return Refuse("the diff is too large to scan", ErrorCode.SessionFailed, changed: changed, commit: commit);
        var findings = DiffScan.Scan(diff.Stdout, secrets, changed);
        if (findings.Count > 0)
            return Refuse($"the diff looks like it carries a secret ({string.Join(", ", findings.Select(f => $"{f.Kind} in {f.Path}"))}); nothing was pushed", ErrorCode.SessionFailed, changed: changed, findings: findings, commit: commit);
        var ci = DiffScan.CiPaths(changed);
        if (ci.Count > 0 && !input.AllowCiChanges)
            return Refuse($"the change edits CI configuration ({string.Join(", ", ci)}); chargehand does not push that, because pushing can run it", ErrorCode.SessionFailed, changed: changed, commit: commit);

        var checkout = await Git.RunAsync(scratch, env, ["-c", "core.hooksPath=/dev/null", "checkout", "--quiet", input.Branch], GitTimeout, ct);
        if (checkout.ExitCode != 0)
            return Refuse($"chargehand could not check the branch out: {checkout.Tail}", ErrorCode.SessionFailed, changed: changed, commit: commit);
        var plan = VerifyCommand.Resolve(input.VerifyCommand, scratch);
        VerificationRecord? verification = null;
        IReadOnlyList<string> verificationPaths = ChangeRun.VerificationPaths(changed, plan);
        if (plan is null)
        {
            if (!input.AllowNoTests)
                return Refuse("no test command found for this repository; chargehand does not push a change it cannot test (name one in the request's context.verify)", ErrorCode.VerificationFailed, changed: changed, commit: commit);
        }
        else
        {
            var raw = await verifier.VerifyAsync(new BranchVerification(input.RunId, bundle, input.Branch, plan.Argv, input.VerifyTimeout ?? TimeSpan.FromMinutes(10)), ct);
            verification = raw with { Source = plan.Source, OutputTail = Redact(raw.OutputTail, secrets) };
            if (verification.ExitCode != 0 || verification.TimedOut)
                return Refuse(verification.TimedOut ? "the tests timed out in chargehand's own verification" : $"the tests failed in chargehand's own verification (exit {verification.ExitCode})", ErrorCode.VerificationFailed,
                    verification, changed, verificationPaths: verificationPaths, commit: commit);
        }

        var push = await Git.RunAsync(scratch, PushEnvironment(input.Credential, input.RemoteUrl, input.ScratchRoot, env), PushArgs(input.RemoteUrl, input.Branch), GitTimeout, ct);
        if (push.ExitCode != 0)
            return Refuse($"the push was rejected: {push.Tail}", ErrorCode.PushRejected, verification, changed, verificationPaths: verificationPaths, commit: commit);

        var repository = RemoteRepository.Parse(input.RemoteUrl) ?? (input.AllowLocalRemote ? new RemoteRepository("local", "local") : null);
        if (repository is null)
            return new(HandoverStatus.PrFailed, "the remote's owner and repository could not be read from its URL", ErrorCode.PrFailed, input.Branch, commit, null, verification, changed, verificationPaths, []);
        try
        {
            var pr = await pullRequests.CreateDraftAsync(repository, input.Branch, input.BaseBranch, input.Title, DescribeBody(input.Body, changed, verification, verificationPaths), ct);
            return new(HandoverStatus.Pushed, "", null, input.Branch, commit, pr, verification, changed, verificationPaths, []);
        }
        catch (PullRequestException e)
        {
            return new(HandoverStatus.PrFailed, Redact(e.Message, secrets), ErrorCode.PrFailed, input.Branch, commit, null, verification, changed, verificationPaths, []);
        }
    }

    /// <summary>The whole <c>git push</c>: one ref, never forced (no <c>+</c>), never a mirror or a delete. The credential is not here.</summary>
    public static IReadOnlyList<string> PushArgs(string remoteUrl, string branch) =>
        ["push", "--porcelain", remoteUrl, $"refs/heads/{branch}:refs/heads/{branch}"];

    /// <summary>The environment of a push: nothing is inherited from the user's git configuration or askpass, and the credential is an HTTP header through git's
    /// environment configuration (or an ssh key file), so it appears on no command line.</summary>
    public static IReadOnlyDictionary<string, string> PushEnvironment(PushCredential? credential, string remoteUrl, string scratchRoot, IReadOnlyDictionary<string, string>? baseEnvironment = null)
    {
        var env = new Dictionary<string, string>(baseEnvironment ?? GitEnvironment(scratchRoot), StringComparer.Ordinal);
        if (credential is null)
            return env;
        if (credential.Kind == PushCredentialKind.HttpsToken)
        {
            env["GIT_CONFIG_COUNT"] = "1";
            env["GIT_CONFIG_KEY_0"] = "http.extraheader";
            env["GIT_CONFIG_VALUE_0"] = $"Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{credential.Value}"))}";
        }
        else
        {
            var keyFile = Path.Combine(scratchRoot, $"push-key-{Guid.NewGuid():N}");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var writer = new StreamWriter(new FileStream(keyFile, options)))
                writer.Write(credential.Value.TrimEnd() + "\n");
            env["GIT_SSH_COMMAND"] = $"ssh -i {keyFile} -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes -o BatchMode=yes";
        }
        return env;
    }

    /// <summary>A clean environment: no user git configuration, no prompts, no askpass, no inherited credentials; PATH only, so git and ssh are found.</summary>
    private static Dictionary<string, string> GitEnvironment(string home) => new(StringComparer.Ordinal)
    {
        ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
        ["HOME"] = home,
        ["GIT_CONFIG_GLOBAL"] = "/dev/null",
        ["GIT_CONFIG_SYSTEM"] = "/dev/null",
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_ASKPASS"] = "/bin/true",
        ["LC_ALL"] = "C",
    };

    private static bool RemoteAllowed(string url, bool allowLocal) =>
        url.Length > 0 && !url.StartsWith('-')
        && (url.StartsWith("https://", StringComparison.Ordinal) || url.StartsWith("ssh://", StringComparison.Ordinal) || ScpForm().IsMatch(url)
            || (allowLocal && url.StartsWith("file://", StringComparison.Ordinal)));

    private static string DescribeBody(string body, List<string> changed, VerificationRecord? verification, IReadOnlyList<string> verificationPaths)
    {
        var text = new StringBuilder(body.TrimEnd()).Append("\n\n---\n\n");
        text.Append("Opened by chargehand as a draft; it is not merged and chargehand will not merge it.\n\n");
        text.Append(verification is null
            ? "Verification: no test command was found, so nothing was run.\n"
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Verification (chargehand's own run in a fresh container): `{string.Join(' ', verification.Argv)}` exited {verification.ExitCode} in {verification.Duration.TotalSeconds:0}s.\n"));
        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"Changed files: {changed.Count}.\n");
        if (verificationPaths.Count > 0)
            text.Append("\nThe change touches files that decide what the tests mean; read them first: ").Append(string.Join(", ", verificationPaths.Take(20))).Append(".\n");
        return text.ToString();
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

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+@[A-Za-z0-9_.-]+:[A-Za-z0-9_./-]+$")]
    private static partial Regex ScpForm();

    private sealed record GitResult(int ExitCode, string Stdout, string Stderr, bool Truncated)
    {
        public string Tail => ((Stderr.Length > 0 ? Stderr : Stdout).Trim() is var t && t.Length > 400 ? t[^400..] : t);
    }

    private static class Git
    {
        public static async Task<GitResult> RunAsync(string directory, IReadOnlyDictionary<string, string> env, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, int maxBytes = 1024 * 1024)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            psi.Environment.Clear();
            foreach (var (k, v) in env)
                psi.Environment[k] = v;
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start git");
            process.StandardInput.Close();
            var truncated = false;
            async Task<string> Read(StreamReader r)
            {
                var text = new StringBuilder();
                var buffer = new char[8192];
                int n;
                while ((n = await r.ReadAsync(buffer)) > 0)
                    if (text.Length + n <= maxBytes)
                        text.Append(buffer, 0, n);
                    else
                        truncated = true;
                return text.ToString();
            }
            var stdout = Read(process.StandardOutput);
            var stderr = Read(process.StandardError);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                if (ct.IsCancellationRequested)
                    throw;
                return new GitResult(-1, "", "git timed out", false);
            }
            return new GitResult(process.ExitCode, await stdout, await stderr, truncated);
        }
    }
}
