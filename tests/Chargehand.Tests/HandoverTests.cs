using System.Diagnostics;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary>ADR 0039: what happens to a session's branch after the session. chargehand checks it itself, and pushes and opens a draft pull request only if
/// every check passes.</summary>
public class HandoverTests
{
    private const string Canary = "canary-push-credential-42";
    private static readonly string AwsKey = "AKIA" + "IOSFODNN7EXAMPLE";

    private static readonly string[] EvilHooks = ["post-checkout", "post-merge", "pre-push", "reference-transaction", "post-commit"];

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static (int Exit, string Out) Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    private static string G(string dir, params string[] args)
    {
        var (exit, output) = Git(dir, args);
        Assert.True(exit == 0, $"git {string.Join(' ', args)}: {output}");
        return output;
    }

    /// <summary>A source checkout, a session workspace with a branch and a bundle in an output directory, and a bare remote that refuses the default branch.</summary>
    private sealed class Fixture : IDisposable
    {
        public TempDir Dir { get; } = new();
        public string Source => Path.Combine(Dir.Path, "source");
        public string Work => Path.Combine(Dir.Path, "work");
        public string Out => Path.Combine(Dir.Path, "out");
        public string Remote => Path.Combine(Dir.Path, "remote.git");
        public string Scratch => Path.Combine(Dir.Path, "scratch");
        public string BaseCommit { get; }

        public Fixture(Action<string>? edit = null, bool commit = true, bool makeBundle = true, bool rejectAll = false, bool orphan = false, bool revert = false, Action<string>? afterBundle = null)
        {
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Out);
            Directory.CreateDirectory(Scratch);
            G(Source, "init", "-q", "-b", "main");
            File.WriteAllText(Path.Combine(Source, "README.md"), "hello\n");
            File.WriteAllText(Path.Combine(Source, "package.json"), """{"scripts":{"test":"echo ok"}}""");
            G(Source, "add", "-A");
            G(Source, "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-q", "-m", "base");
            BaseCommit = G(Source, "rev-parse", "HEAD").Trim();

            G(Dir.Path, "clone", "-q", "--template=", Source, Work);
            G(Work, "config", "user.name", "chargehand");
            G(Work, "config", "user.email", "chargehand@localhost");
            if (orphan)
                G(Work, "checkout", "-q", "--orphan", "chargehand/run1");
            else
                G(Work, "checkout", "-q", "-b", "chargehand/run1", BaseCommit);
            if (commit)
            {
                File.WriteAllText(Path.Combine(Work, "retry.txt"), "retry twice\n");
                edit?.Invoke(Work);
                G(Work, "add", "-A");
                G(Work, "commit", "-q", "-m", "feat: retry");
                if (revert)
                {
                    G(Work, "revert", "--no-edit", "HEAD");
                }
            }
            if (makeBundle)
                G(Work, "bundle", "create", Path.Combine(Out, "chargehand.bundle"), "chargehand/run1", "--not", "--remotes");
            afterBundle?.Invoke(Work);

            G(Dir.Path, "init", "-q", "--bare", "-b", "main", Remote);
            var hook = Path.Combine(Remote, "hooks", "pre-receive");
            File.WriteAllText(hook, rejectAll
                ? "#!/bin/sh\necho 'remote: denied by test' >&2\nexit 1\n"
                : "#!/bin/sh\nwhile read old new ref; do [ \"$ref\" = refs/heads/main ] && { echo 'remote: default branch is protected' >&2; exit 1; }; done\nexit 0\n");
            MakeExecutable(hook);
        }

        public string RemoteRef(string name) => Git(Remote, "rev-parse", "--verify", "--quiet", name).Exit == 0 ? G(Remote, "rev-parse", name).Trim() : "";

        public HandoverInput Input(Action<HandoverInput>? _ = null, Func<HandoverInput, HandoverInput>? with = null)
        {
            var input = new HandoverInput("run1", Out, Source, BaseCommit, "chargehand/run1", $"file://{Remote}", "main", "Add a retry", "A retry.", Scratch,
                new PushCredential(Canary), Secrets: [Canary], AllowLocalRemote: true);
            return with?.Invoke(input) ?? input;
        }

        public void Dispose() => Dir.Dispose();
    }

    private sealed class FakeVerifier(int exit = 0) : IBranchVerifier
    {
        public List<BranchVerification> Calls { get; } = [];

        public Task<VerificationRecord> VerifyAsync(BranchVerification v, CancellationToken ct)
        {
            Calls.Add(v);
            return Task.FromResult(new VerificationRecord(v.Argv, "detected", exit, exit == 0 ? "ok" : "1 failed", false, TimeSpan.FromSeconds(3), false));
        }
    }

    private sealed class FakePullRequests(Exception? failure = null) : IPullRequests
    {
        public List<(string Head, string Base, string Title, string Body)> Created { get; } = [];

        public Task<PullRequestRef> CreateDraftAsync(RemoteRepository repo, string head, string @base, string title, string body, CancellationToken ct)
        {
            Created.Add((head, @base, title, body));
            return failure is null ? Task.FromResult(new PullRequestRef("https://example.test/o/r/pull/9", 9)) : throw failure;
        }
    }

    [Fact]
    public async Task A_clean_green_branch_is_pushed_and_a_draft_pull_request_opened()
    {
        using var f = new Fixture();
        var verifier = new FakeVerifier();
        var prs = new FakePullRequests();
        var outcome = await new Handover(verifier, prs).RunAsync(f.Input(), default);
        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        Assert.Equal(new PullRequestRef("https://example.test/o/r/pull/9", 9), outcome.PullRequest);
        Assert.Equal(outcome.Commit, f.RemoteRef("refs/heads/chargehand/run1"));
        Assert.Equal("", f.RemoteRef("refs/heads/main"));                       // the default branch was never touched
        Assert.Equal(["retry.txt"], outcome.ChangedPaths);
        Assert.Equal(0, outcome.Verification!.ExitCode);
        Assert.Equal(["npm", "test"], outcome.Verification.Argv);              // detected in chargehand's own checkout of the branch, not the session's word
        var (head, @base, title, body) = Assert.Single(prs.Created);
        Assert.Equal(("chargehand/run1", "main", "Add a retry"), (head, @base, title));
        Assert.Contains("not merged", body, StringComparison.OrdinalIgnoreCase);
        Assert.Single(verifier.Calls);
        Assert.EndsWith("chargehand.bundle", verifier.Calls[0].BundlePath);
    }

    [Fact]
    public async Task Each_step_is_reported_as_it_finishes()
    {
        using var f = new Fixture();
        List<HandoverEvent> seen = [];
        var outcome = await new Handover(new FakeVerifier(), new FakePullRequests()).RunAsync(f.Input(with: i => i with { Report = seen.Add }), default);
        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        Assert.Equal([RunEventKind.VerifyFinished, RunEventKind.Pushed, RunEventKind.PrOpened], seen.Select(e => e.Kind));
        Assert.Contains("passed", seen[0].Detail);
        Assert.Equal("https://example.test/o/r/pull/9", seen[2].PrUrl);

        using var red = new Fixture();
        seen.Clear();
        await new Handover(new FakeVerifier(exit: 1), new FakePullRequests()).RunAsync(red.Input(with: i => i with { Report = seen.Add }), default);
        var only = Assert.Single(seen);
        Assert.Equal(RunEventKind.VerifyFinished, only.Kind);
        Assert.Contains("failed", only.Detail);
    }

    [Fact]
    public async Task Session_claim_does_not_override_red_verification()
    {
        using var f = new Fixture();
        var prs = new FakePullRequests();
        var outcome = await new Handover(new FakeVerifier(exit: 1), prs).RunAsync(f.Input(), default);
        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Equal(ErrorCode.VerificationFailed, outcome.Error);
        Assert.Equal(1, outcome.Verification!.ExitCode);
        Assert.Equal("", f.RemoteRef("refs/heads/chargehand/run1"));
        Assert.Empty(prs.Created);
        Assert.True(File.Exists(Path.Combine(f.Out, "chargehand.bundle")), "the bundle is kept for a person to look at");
    }

    [Fact]
    public async Task Empty_diff_is_not_pushed()
    {
        using var f = new Fixture(revert: true);
        var verifier = new FakeVerifier();
        var outcome = await new Handover(verifier, new FakePullRequests()).RunAsync(f.Input(), default);
        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Contains("no change", outcome.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(verifier.Calls);                                           // nothing to verify
        Assert.Equal("", f.RemoteRef("refs/heads/chargehand/run1"));
    }

    [Fact]
    public async Task Secret_in_diff_blocks_push()
    {
        using var f = new Fixture(edit: w => File.WriteAllText(Path.Combine(w, "config.txt"), $"key = {AwsKey}\n"));
        var verifier = new FakeVerifier();
        var outcome = await new Handover(verifier, new FakePullRequests()).RunAsync(f.Input(), default);
        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Equal(ErrorCode.SessionFailed, outcome.Error);
        Assert.Equal("aws", Assert.Single(outcome.Findings).Kind);
        Assert.DoesNotContain(AwsKey, JsonSerializer.Serialize(outcome));
        Assert.Empty(verifier.Calls);                                           // the scan comes before anything else runs
        Assert.Equal("", f.RemoteRef("refs/heads/chargehand/run1"));
    }

    [Fact]
    public async Task The_push_credential_literal_in_a_diff_blocks_push()
    {
        using var f = new Fixture(edit: w => File.WriteAllText(Path.Combine(w, "notes.txt"), $"token {Canary}\n"));
        var outcome = await new Handover(new FakeVerifier(), new FakePullRequests()).RunAsync(f.Input(), default);
        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Equal("credential-literal", Assert.Single(outcome.Findings).Kind);
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(outcome));
    }

    [Fact]
    public async Task A_change_to_ci_configuration_is_refused_unless_allowed()
    {
        static void Edit(string w)
        {
            Directory.CreateDirectory(Path.Combine(w, ".github", "workflows"));
            File.WriteAllText(Path.Combine(w, ".github", "workflows", "ci.yml"), "on: push\n");
        }
        using var refused = new Fixture(edit: Edit);
        var outcome = await new Handover(new FakeVerifier(), new FakePullRequests()).RunAsync(refused.Input(), default);
        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Contains("CI", outcome.Reason);
        Assert.Contains(".github/workflows/ci.yml", outcome.Reason);
        Assert.Equal("", refused.RemoteRef("refs/heads/chargehand/run1"));

        using var allowed = new Fixture(edit: Edit);
        Assert.Equal(HandoverStatus.Pushed, (await new Handover(new FakeVerifier(), new FakePullRequests()).RunAsync(allowed.Input(with: i => i with { AllowCiChanges = true }), default)).Status);
    }

    [Fact]
    public async Task No_bundle_means_nothing_to_hand_over()
    {
        using var f = new Fixture(makeBundle: false);
        var outcome = await new Handover(new FakeVerifier(), new FakePullRequests()).RunAsync(f.Input(), default);
        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Equal(ErrorCode.SessionFailed, outcome.Error);
        Assert.Contains("bundle", outcome.Reason);
    }

    [Fact]
    public async Task A_branch_that_does_not_descend_from_the_base_is_refused()
    {
        using var f = new Fixture(orphan: true);
        var outcome = await new Handover(new FakeVerifier(), new FakePullRequests()).RunAsync(f.Input(), default);
        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Contains("descend", outcome.Reason);
    }

    [Fact]
    public async Task A_repository_with_no_test_command_is_not_pushed_unless_allowed()
    {
        using var f = new Fixture(edit: w => File.Delete(Path.Combine(w, "package.json")));
        var refused = await new Handover(new FakeVerifier(), new FakePullRequests()).RunAsync(f.Input(), default);
        Assert.Equal(HandoverStatus.NotPushed, refused.Status);
        Assert.Equal(ErrorCode.VerificationFailed, refused.Error);
        Assert.Contains("no test command", refused.Reason, StringComparison.OrdinalIgnoreCase);
        var allowed = await new Handover(new FakeVerifier(), new FakePullRequests()).RunAsync(f.Input(with: i => i with { AllowNoTests = true }), default);
        Assert.Equal(HandoverStatus.Pushed, allowed.Status);
        Assert.Null(allowed.Verification);
    }

    [Fact]
    public async Task A_request_can_name_the_verification_command_and_it_is_the_one_run()
    {
        using var f = new Fixture();
        var verifier = new FakeVerifier();
        await new Handover(verifier, new FakePullRequests()).RunAsync(f.Input(with: i => i with { VerifyCommand = ["make", "check"] }), default);
        Assert.Equal(["make", "check"], verifier.Calls.Single().Argv);
    }

    [Fact]
    public async Task Push_rejected_keeps_the_bundle_and_never_shows_the_credential()
    {
        using var f = new Fixture(rejectAll: true);
        var prs = new FakePullRequests();
        var outcome = await new Handover(new FakeVerifier(), prs).RunAsync(f.Input(), default);
        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Equal(ErrorCode.PushRejected, outcome.Error);
        Assert.Contains("denied by test", outcome.Reason);
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(outcome));
        Assert.Empty(prs.Created);
        Assert.True(File.Exists(Path.Combine(f.Out, "chargehand.bundle")));
    }

    [Fact]
    public async Task A_pull_request_that_cannot_be_opened_still_names_the_pushed_branch()
    {
        using var f = new Fixture();
        var outcome = await new Handover(new FakeVerifier(), new FakePullRequests(new PullRequestException("GitHub answered 403"))).RunAsync(f.Input(), default);
        Assert.Equal(HandoverStatus.PrFailed, outcome.Status);
        Assert.Equal(ErrorCode.PrFailed, outcome.Error);
        Assert.Equal("chargehand/run1", outcome.Branch);
        Assert.Equal(outcome.Commit, f.RemoteRef("refs/heads/chargehand/run1"));
        Assert.Null(outcome.PullRequest);
    }

    [Fact]
    public async Task Hostile_workspace_config_does_not_run_on_handover()
    {
        using var markerDir = new TempDir();
        var marker = Path.Combine(markerDir.Path, "PWNED");
        using var f = new Fixture(edit: w => File.WriteAllText(Path.Combine(w, ".gitattributes"), "* filter=evil\n"), afterBundle: w =>
        {
            // Everything a hostile session could configure in its own repository, set after the bundle exists so the fixture's own git calls do not trip it.
            G(w, "config", "core.fsmonitor", $"touch {marker}; echo");
            G(w, "config", "core.hooksPath", Path.Combine(w, "evil-hooks"));
            Directory.CreateDirectory(Path.Combine(w, "evil-hooks"));
            foreach (var hook in EvilHooks)
            {
                var path = Path.Combine(w, "evil-hooks", hook);
                File.WriteAllText(path, $"#!/bin/sh\ntouch {marker}\n");
                MakeExecutable(path);
            }
            G(w, "config", "filter.evil.clean", $"touch {marker}; cat");
            G(w, "config", "filter.evil.smudge", $"touch {marker}; cat");
        });
        var outcome = await new Handover(new FakeVerifier(), new FakePullRequests()).RunAsync(f.Input(), default);
        Assert.Equal(HandoverStatus.Pushed, outcome.Status);
        Assert.False(File.Exists(marker), "something the session's repository configured ran on the host");
    }

    [Fact]
    public void The_push_never_forces_and_carries_the_credential_only_in_the_environment()
    {
        var args = Handover.PushArgs("file:///r.git", "chargehand/run1");
        Assert.Equal(["push", "--porcelain", "file:///r.git", "refs/heads/chargehand/run1:refs/heads/chargehand/run1"], args);
        Assert.DoesNotContain(args, a => a.StartsWith('+') || a.Contains("force", StringComparison.OrdinalIgnoreCase) || a.Contains("--mirror") || a.Contains("--delete"));
        var env = Handover.PushEnvironment(new PushCredential("s3cret-token-value"), "https://github.com/o/r.git", "/scratch");
        Assert.Contains(env, kv => kv.Value.Contains(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("x-access-token:s3cret-token-value")), StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.Contains("s3cret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://github.com/o/r.git")]                 // no plain http
    [InlineData("ftp://example.test/r.git")]
    [InlineData("git://github.com/o/r.git")]
    [InlineData("ext::sh -c evil")]
    [InlineData("--upload-pack=x")]
    [InlineData("")]
    public async Task Only_https_and_ssh_remotes_are_pushed_to(string url)
    {
        using var f = new Fixture();
        var outcome = await new Handover(new FakeVerifier(), new FakePullRequests()).RunAsync(f.Input(with: i => i with { RemoteUrl = url, AllowLocalRemote = false }), default);
        Assert.Equal(HandoverStatus.NotPushed, outcome.Status);
        Assert.Contains("remote", outcome.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("chargehand/")]
    [InlineData("chargehand/--force")]
    [InlineData("chargehand/a b")]
    public async Task The_branch_must_be_under_chargehand(string branch)
    {
        using var f = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => new Handover(new FakeVerifier(), new FakePullRequests()).RunAsync(f.Input(with: i => i with { Branch = branch }), default));
    }
}
