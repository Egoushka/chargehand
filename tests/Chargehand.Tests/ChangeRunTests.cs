using System.Diagnostics;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.RunLog;
using Chargehand.Sandbox;
using Chargehand.Verification;

namespace Chargehand.Tests;

/// <summary>Goal 0.7 (ADR 0035): a run under the code preset ends with a verified branch, or a failure that still carries it.</summary>
public class ChangeRunTests
{
    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return output.Trim();
    }

    private sealed record Setup(TempDir Root, RepositoryRef Source, ScriptedRuntime Runtime, Orchestrator Orchestrator) : IDisposable
    {
        public void Dispose() => Root.Dispose();
    }

    private static Setup Make(Action<string, int>? onTurn, Func<SandboxSettings?, ISandbox>? sandboxFor = null)
    {
        var root = new TempDir();
        var source = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply) { OnTurn = onTurn };
        var profile = Runs.Profile(root.Path) with { RepositoryRoots = [root.Path], Sandbox = new SandboxSettings("none") };
        var orchestrator = new Orchestrator(profile, runtime, "2.0.16", Repo.Root, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), new Dictionary<string, int>(),
            sandboxFor: sandboxFor);
        return new Setup(root, source, runtime, orchestrator);
    }

    private static RunRequest Request(RepositoryRef repo, params string[] verify) =>
        new("request/v1", "Add a feature file.", new RequestContext(false, "code", Repository: repo, Verify: verify.Length == 0 ? null : verify));

    private static string[] Sh(string script) => ["/bin/sh", "-c", script];

    private static JsonElement Artifact(ResultContract r, string kind) =>
        JsonDocument.Parse(r.Artifacts.Single(a => a.Kind == kind).Content!).RootElement.Clone();

    [Fact]
    public async Task A_change_that_passes_its_tests_completes_with_a_branch_a_diff_and_a_verification()
    {
        using var s = Make((dir, _) => File.WriteAllText(Path.Combine(dir, "feature.txt"), "feature\n"));
        var r = await s.Orchestrator.RunAsync(Request(s.Source, Sh("test -f feature.txt")), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, r.Status);
        Assert.Null(r.Error);
        Assert.Equal(["branch", "diff", "verification"], r.Artifacts.Select(a => a.Kind));
        var branch = Artifact(r, "branch");
        var clone = branch.GetProperty("repository").GetString()!;
        Assert.Equal("chargehand/" + r.TaskId + "/writer", branch.GetProperty("branch").GetString());
        Assert.Equal(branch.GetProperty("commit").GetString(), Git(clone, "rev-parse", branch.GetProperty("branch").GetString()!));
        Assert.Equal(s.Source.Commit, branch.GetProperty("base").GetString());
        Assert.Contains("+feature", r.Artifacts.Single(a => a.Kind == "diff").Content, StringComparison.Ordinal);
        var v = Artifact(r, "verification");
        Assert.True(v.GetProperty("passed").GetBoolean());
        Assert.Equal(0, v.GetProperty("exit_code").GetInt32());
        Assert.Equal(1, v.GetProperty("attempts").GetInt32());
        Assert.Equal("request", v.GetProperty("source").GetString());
        Assert.Equal("none", v.GetProperty("sandbox").GetString());
    }

    [Fact]
    public async Task The_worker_edits_its_own_clone_and_the_source_and_shared_checkout_stay_untouched()
    {
        using var s = Make((dir, _) => File.WriteAllText(Path.Combine(dir, "feature.txt"), "feature\n"));
        var r = await s.Orchestrator.RunAsync(Request(s.Source, Sh("true")), CancellationToken.None);

        var spec = Assert.Single(s.Runtime.Created);
        Assert.Contains(Path.Combine(".runs", r.TaskId, "writer"), spec.Directory, StringComparison.Ordinal);
        Assert.DoesNotContain(".checkouts", spec.Directory, StringComparison.Ordinal);
        Assert.Empty(Git(s.Source.Path, "status", "--porcelain"));
        Assert.DoesNotContain("chargehand/", Git(s.Source.Path, "branch", "--list"), StringComparison.Ordinal);
        var shared = Directory.GetDirectories(Path.Combine(s.Root.Path, ".checkouts")).Single();
        var checkout = Directory.GetDirectories(shared).Single();
        Assert.Empty(Git(checkout, "status", "--porcelain"));
        Assert.False(File.Exists(Path.Combine(checkout, "feature.txt")));
    }

    [Fact]
    public async Task A_red_run_is_sent_back_with_the_output_and_can_be_fixed()
    {
        using var s = Make((dir, turn) => File.WriteAllText(Path.Combine(dir, turn == 1 ? "attempt.txt" : "fixed.txt"), "x\n"));
        var r = await s.Orchestrator.RunAsync(Request(s.Source, Sh("echo missing-the-fix >&2; test -f fixed.txt")), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, r.Status);
        Assert.Equal(2, s.Runtime.Prompts.Count);
        var second = s.Runtime.Prompts.ToArray()[1];
        Assert.Contains("did not pass", second, StringComparison.Ordinal);
        Assert.Contains("missing-the-fix", second, StringComparison.Ordinal);
        Assert.Equal(2, Artifact(r, "verification").GetProperty("attempts").GetInt32());
    }

    [Fact]
    public async Task A_run_that_stays_red_fails_with_verification_failed_and_keeps_the_branch()
    {
        using var s = Make((dir, turn) => File.WriteAllText(Path.Combine(dir, $"try-{turn}.txt"), "x\n"));
        var r = await s.Orchestrator.RunAsync(Request(s.Source, Sh("echo nope; exit 1")), CancellationToken.None);

        Assert.Equal(ResultStatus.Failed, r.Status);
        Assert.Equal(ErrorCode.VerificationFailed, r.Error?.Code);
        Assert.False(r.Error!.Retryable);
        Assert.False(string.IsNullOrEmpty(r.Error.Action));
        Assert.Equal(3, s.Runtime.Prompts.Count);
        Assert.Equal(["branch", "diff", "verification"], r.Artifacts.Select(a => a.Kind));
        var v = Artifact(r, "verification");
        Assert.False(v.GetProperty("passed").GetBoolean());
        Assert.Equal(3, v.GetProperty("attempts").GetInt32());
        Assert.Contains("nope", v.GetProperty("output_tail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_the_tests_write_stays_out_of_the_branch()
    {
        using var s = Make((dir, _) => File.WriteAllText(Path.Combine(dir, "feature.txt"), "x\n"));
        var r = await s.Orchestrator.RunAsync(Request(s.Source, Sh("echo cache > build.out; test -f feature.txt")), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, r.Status);
        Assert.DoesNotContain("build.out", r.Artifacts.Single(a => a.Kind == "diff").Content, StringComparison.Ordinal);
        var clone = Artifact(r, "branch").GetProperty("repository").GetString()!;
        Assert.False(File.Exists(Path.Combine(clone, "build.out")));
        Assert.Empty(Git(clone, "status", "--porcelain"));
    }

    [Fact]
    public async Task A_repository_with_no_test_command_completes_untested_with_low_confidence()
    {
        using var s = Make((dir, _) => File.WriteAllText(Path.Combine(dir, "feature.txt"), "x\n"));
        var r = await s.Orchestrator.RunAsync(Request(s.Source), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, r.Status);
        Assert.Contains(r.OpenQuestions, q => q.Contains("No test command found", StringComparison.Ordinal));
        Assert.True(r.Confidence <= 0.5);
        var v = Artifact(r, "verification");
        Assert.Equal("none", v.GetProperty("source").GetString());
        Assert.Equal(0, v.GetProperty("attempts").GetInt32());
    }

    [Fact]
    public async Task A_worker_that_changes_nothing_fails_without_a_branch()
    {
        using var s = Make(null);
        var r = await s.Orchestrator.RunAsync(Request(s.Source, Sh("true")), CancellationToken.None);

        Assert.Equal(ResultStatus.Failed, r.Status);
        Assert.False(string.IsNullOrEmpty(r.Error?.Action));
        Assert.DoesNotContain(r.Artifacts, a => a.Kind == "branch");
    }

    [Fact]
    public async Task The_verification_records_that_the_network_was_off()
    {
        using var s = Make((dir, _) => File.WriteAllText(Path.Combine(dir, "feature.txt"), "x\n"));
        var r = await s.Orchestrator.RunAsync(Request(s.Source, Sh("true")), CancellationToken.None);
        Assert.False(Artifact(r, "verification").GetProperty("network").GetBoolean());
    }

    [Fact]
    public async Task Changed_build_and_test_files_are_listed_in_the_verification()
    {
        using var s = Make((dir, _) =>
        {
            Directory.CreateDirectory(Path.Combine(dir, "scripts"));
            File.WriteAllText(Path.Combine(dir, "scripts", "check.sh"), "exit 0\n");
            File.WriteAllText(Path.Combine(dir, "feature.txt"), "x\n");
        });
        var r = await s.Orchestrator.RunAsync(Request(s.Source, Sh("true")), CancellationToken.None);
        var paths = Artifact(r, "verification").GetProperty("changed_verification_paths").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["scripts/check.sh"], paths);
    }

    [Fact]
    public async Task Without_a_sandbox_the_run_is_refused_before_intake_and_nothing_is_cloned()
    {
        using var s = Make(null, _ => throw new ChargehandException(ErrorCode.SandboxUnavailable, "no sandbox", "install one"));
        var r = await s.Orchestrator.RunAsync(Request(s.Source, Sh("true")), CancellationToken.None);

        Assert.Equal(ErrorCode.SandboxUnavailable, r.Error?.Code);
        Assert.Empty(s.Runtime.IntakePrompts);
        Assert.Empty(s.Runtime.Created);
        Assert.False(Directory.Exists(Path.Combine(s.Root.Path, ".runs")));
    }

    [Fact]
    public void The_commit_message_is_one_short_line()
    {
        Assert.Equal("chargehand: Adds a greeting", ChangeRun.CommitMessage("Adds a greeting\nand more"));
        Assert.Equal("chargehand: change", ChangeRun.CommitMessage(""));
        Assert.True(ChangeRun.CommitMessage(new string('x', 200)).Length <= 72);
    }

    [Fact]
    public void A_verify_command_named_in_the_request_counts_as_a_verification_path_when_changed()
    {
        var plan = new VerifyPlan(["./check.sh"], "request");
        Assert.Equal(["check.sh", "tests/a.py", "app.csproj"], ChangeRun.VerificationPaths(["src/x.cs", "check.sh", "tests/a.py", "app.csproj"], plan));
    }
}
