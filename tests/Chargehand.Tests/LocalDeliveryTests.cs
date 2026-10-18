using System.Diagnostics;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Memory.Api;
using Chargehand.RunLog;
using Chargehand.Sandbox;

namespace Chargehand.Tests;

/// <summary>ADR 0069: a local run leaves its branch in the caller's repository and nothing else; the profile can force the mode.</summary>
public class LocalDeliveryTests
{
    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
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

    private static Setup Make(Func<Profile, Profile>? profile = null, MemoryStack? memory = null)
    {
        var root = new TempDir();
        var source = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply) { OnTurn = (dir, _) => File.WriteAllText(Path.Combine(dir, "feature.txt"), "feature\n") };
        var p = Runs.Profile(root.Path) with { RepositoryRoots = [root.Path], Sandbox = new SandboxSettings("none") };
        var orchestrator = new Orchestrator(profile?.Invoke(p) ?? p, runtime, "2.0.16", Repo.Root, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), new Dictionary<string, int>(), memory);
        return new Setup(root, source, runtime, orchestrator);
    }

    private static RunRequest Request(RepositoryRef repo, string? delivery) =>
        new("request/v1", "Add a feature file.", new RequestContext(false, "code", Repository: repo, Verify: ["/bin/sh", "-c", "true"], Delivery: delivery));

    private static JsonElement Branch(ResultContract r) => JsonDocument.Parse(r.Artifacts.Single(a => a.Kind == "branch").Content!).RootElement.Clone();

    /// <summary>Everything a landing must leave alone: HEAD, the current branch, the index and the working tree.</summary>
    private static string Untouched(string repo) => string.Join("|", Git(repo, "rev-parse", "HEAD"), Git(repo, "symbolic-ref", "HEAD"), Git(repo, "status", "--porcelain=v2", "--branch"),
        Git(repo, "ls-files", "-s"));

    private static string[] Refs(string repo) => Git(repo, "for-each-ref", "--format=%(refname)", "refs/heads", "refs/tags", "refs/remotes").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task A_local_request_leaves_the_branch_in_the_callers_repository_and_touches_nothing_else()
    {
        using var s = Make();
        var (before, refsBefore) = (Untouched(s.Source.Path), Refs(s.Source.Path));

        var r = await s.Orchestrator.RunAsync(Request(s.Source, "local"), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, r.Status);
        var branch = Branch(r);
        var name = branch.GetProperty("branch").GetString()!;
        Assert.Equal("local", branch.GetProperty("delivery").GetString());
        Assert.Equal(PathScope.RealPath(s.Source.Path), PathScope.RealPath(branch.GetProperty("repository").GetString()!));
        Assert.Equal(branch.GetProperty("commit").GetString(), Git(s.Source.Path, "rev-parse", name));
        Assert.Equal(s.Source.Commit, Git(s.Source.Path, "rev-parse", name + "~1"));
        Assert.Equal(before, Untouched(s.Source.Path));
        Assert.Equal(refsBefore.Append("refs/heads/" + name).Order(), Refs(s.Source.Path).Order());
        Assert.False(File.Exists(Path.Combine(s.Source.Path, "feature.txt")));
        Assert.Empty(Git(s.Source.Path, "status", "--porcelain"));
    }

    [Fact]
    public async Task A_request_without_the_field_keeps_the_branch_in_the_run_clone_as_before()
    {
        using var s = Make();

        var r = await s.Orchestrator.RunAsync(Request(s.Source, null), CancellationToken.None);

        var branch = Branch(r);
        Assert.Equal("push", branch.GetProperty("delivery").GetString());
        Assert.Contains(".runs", branch.GetProperty("repository").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("chargehand/", Git(s.Source.Path, "branch", "--list"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repository_under_a_local_only_root_is_local_without_the_field()
    {
        using var s = Make(p => p with { Delivery = new DeliverySettings([p.WorkerRoot]) });

        var r = await s.Orchestrator.RunAsync(Request(s.Source, null), CancellationToken.None);

        Assert.Equal("local", Branch(r).GetProperty("delivery").GetString());
        Assert.Contains("chargehand/" + r.TaskId, Git(s.Source.Path, "branch", "--list"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_push_request_for_a_local_only_repository_is_refused_before_anything_is_cloned()
    {
        using var s = Make(p => p with { Delivery = new DeliverySettings([p.WorkerRoot]) });

        var r = await s.Orchestrator.RunAsync(Request(s.Source, "push"), CancellationToken.None);

        Assert.Equal(ResultStatus.Failed, r.Status);
        Assert.Equal(ErrorCode.LocalOnly, r.Error!.Code);
        Assert.Empty(s.Runtime.Created);
        Assert.False(Directory.Exists(Path.Combine(s.Root.Path, ".runs")));
        Assert.DoesNotContain("chargehand/", Git(s.Source.Path, "branch", "--list"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_branch_that_cannot_land_fails_delivery_failed_and_stays_in_the_run_clone()
    {
        using var s = Make();
        // A divergent branch under the name the run will use makes the unforced fetch refuse to move it.
        Git(s.Source.Path, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "--allow-empty", "-m", "side");
        Git(s.Source.Path, "branch", "chargehand/run-fixed/writer");
        Git(s.Source.Path, "reset", "-q", "--hard", s.Source.Commit);

        var r = await s.Orchestrator.RunAsync(Request(s.Source, "local"), CancellationToken.None, runId: "run-fixed");

        Assert.Equal(ResultStatus.Failed, r.Status);
        Assert.Equal(ErrorCode.DeliveryFailed, r.Error!.Code);
        Assert.Contains("git fetch", r.Error.Action, StringComparison.Ordinal);
        Assert.Contains(".runs", Branch(r).GetProperty("repository").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_local_run_asks_only_the_memory_sources_the_profile_cleared()
    {
        var cleared = new MemoryStackTests.Fake(_ => [new RecalledMemory("1", "Cleared fact.")]);
        var uncleared = new MemoryStackTests.Fake(_ => [new RecalledMemory("2", "Shared fact.")]);
        var stack = new MemoryStack([
            new MemorySource("shared", uncleared, new MemoryScope("shared", "ns"), new MemoryLimits(), true),
            new MemorySource("work", cleared, new MemoryScope("work", "ns"), new MemoryLimits(), true, LocalOk: true)]);
        using var s = Make(memory: stack);

        await s.Orchestrator.RunAsync(Request(s.Source, "local"), CancellationToken.None);

        var prompt = string.Join("\n", s.Runtime.Prompts);
        Assert.Contains("Cleared fact.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Shared fact.", prompt, StringComparison.Ordinal);
        Assert.Empty(uncleared.Retained);
    }

    [Fact]
    public void A_stack_with_no_cleared_source_is_no_stack_for_a_local_run()
    {
        var stack = new MemoryStack([new MemorySource("shared", new MemoryStackTests.Fake(), new MemoryScope("shared", "ns"), new MemoryLimits(), true)]);

        Assert.Null(stack.LocalOnly());
    }

    [Fact]
    public void Intake_may_not_watch_a_repository_under_a_local_only_root()
    {
        using var dir = new TempDir();
        string Profile(string section) => dir.Write("local.json", "{\"schema\":\"profile/v1\",\"delivery\":{\"local_only\":[\"/srv/private\"]},\"driven\":{\"enabled\":true,\"integrator\":{\"app_id\":1,\"installation_id\":2,\"private_key_secret\":\"k\"}},\"automation\":{" + section + "}}");

        var github = Assert.Throws<ChargehandException>(() => Config.Profile.Load(Profile("\"github\":{\"repositories\":{\"o/r\":{\"path\":\"/srv/private/r\"}},\"allowed_authors\":[\"a\"]}")));
        Assert.Contains("automation.github.repositories.o/r.path", github.Message, StringComparison.Ordinal);
        Assert.Equal(ErrorCode.InvalidRequest, github.Code);
        var fine = Config.Profile.Load(Profile("\"github\":{\"repositories\":{\"o/r\":{\"path\":\"/srv/public/r\"}},\"allowed_authors\":[\"a\"]}"));
        Assert.Equal(["/srv/private"], fine.Delivery!.LocalOnly);
    }

    [Theory]
    [InlineData("relative/dir")]
    [InlineData("")]
    public void A_local_only_root_must_be_absolute_or_home_relative(string root)
    {
        using var dir = new TempDir();
        var path = dir.Write("local.json", "{\"schema\":\"profile/v1\",\"delivery\":{\"local_only\":[\"" + root + "\"]}}");

        Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ChargehandException>(() => Config.Profile.Load(path)).Code);
    }
}
