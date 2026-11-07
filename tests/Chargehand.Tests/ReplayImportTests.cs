using System.Diagnostics;
using Chargehand.Bench;

namespace Chargehand.Tests;

/// <summary>The replay importer on a synthetic repository with real git: a merged pull request becomes a task whose goal has no test paths, whose oracle is the merged test files and whose parent is
/// the merge's first parent; the pull requests that cannot be tasks are named with a reason (cycle B spec, decision 2).</summary>
public sealed class ReplayImportTests : IDisposable
{
    private static readonly string[] Identity = ["-c", "user.name=t", "-c", "user.email=t@example.test", "-c", "commit.gpgsign=false"];
    private const string Url = "https://example.com/org/lib.git";

    private readonly TempDir _dir = new();
    private readonly string _repo;
    private readonly string _base;
    private readonly string _feature;
    private readonly string _testsOnly;
    private readonly string _noTests;

    public ReplayImportTests()
    {
        _repo = Path.Combine(_dir.Path, "repo");
        Directory.CreateDirectory(_repo);
        Git(_repo, "init", "-q", "-b", "main");
        Put("src/Lib/Calc.cs", "class Calc { int Add(int a, int b) => a; }\n");
        Put("tests/Lib.Tests/Lib.Tests.csproj", "<Project />\n");
        Put("tests/Lib.Tests/OldTests.cs", "class OldTests { }\n");
        Commit("initial");
        _base = Head();

        Put("src/Lib/Calc.cs", "class Calc { int Add(int a, int b) => a + b; }\n");
        Put("tests/Lib.Tests/CalcTests.cs", "class CalcTests { void Adds() { } }\n");
        File.Delete(Path.Combine(_repo, "tests/Lib.Tests/OldTests.cs"));
        Commit("feat: add numbers");
        _feature = Head();

        Put("tests/Lib.Tests/CalcTests.cs", "class CalcTests { void Adds() { } void Subtracts() { } }\n");
        Commit("test: more cases");
        _testsOnly = Head();

        Put("src/Lib/Calc.cs", "class Calc { int Add(int a, int b) => a + b; int Sub(int a, int b) => a - b; }\n");
        Commit("feat: subtract");
        _noTests = Head();
    }

    public void Dispose() => _dir.Dispose();

    private void Put(string path, string text)
    {
        var full = Path.Combine(_repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private void Commit(string message)
    {
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", message);
    }

    private string Head() => Git(_repo, "rev-parse", "HEAD").Trim();

    private static string Git(string where, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = where, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in Identity.Concat(args))
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        psi.Environment["GIT_CONFIG_SYSTEM"] = "/dev/null";
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {error}");
        return output;
    }

    private Task<ReplayImport> Import(params ReplayPr[] prs) => ReplayImporter.ImportAsync(new ReplayRequest(_repo, Url, "MIT", "synthetic", prs), default);

    private ReplayPr Feature() => new(7, "feat: add numbers (#7)",
        "Add makes the sum of its arguments, not the first one.\n\n## Test plan\n- run everything\n\nCalcTests covers it.\nCloses #3\n\n🤖 Generated with [Claude Code](https://claude.com/claude-code)", _feature,
        [new ReplayIssue("Add ignores b", "Calc.Add(1, 2) returns 1.\nSee tests/Lib.Tests/CalcTests.cs.")]);

    [Fact]
    public async Task A_merged_pull_request_becomes_a_task_that_starts_at_the_merges_first_parent()
    {
        var import = await Import(Feature());
        var task = Assert.Single(import.Set.Tasks);
        var entry = Assert.Single(import.Manifest.Tasks);
        Assert.Equal(_base, entry.Parent);
        Assert.Equal(_feature, entry.Merge);
        Assert.Equal(_base, import.Set.Repositories.Single(r => r.Name == task.Repository).Commit);
        Assert.Equal("S", task.Size);
        Assert.Empty(import.Rejected);
    }

    [Fact]
    public async Task The_goal_carries_the_text_but_no_test_path_name_or_section()
    {
        var goal = Assert.Single((await Import(Feature())).Set.Tasks).Goal;
        Assert.Contains("feat: add numbers", goal);
        Assert.Contains("makes the sum of its arguments", goal);
        Assert.Contains("Calc.Add(1, 2) returns 1.", goal);
        foreach (var leak in new[] { "CalcTests", "OldTests", "tests/", "Test plan", "run everything", "(#7)", "Claude Code", "Closes" })
            Assert.DoesNotContain(leak, goal);
    }

    [Fact]
    public async Task The_oracle_is_the_merged_test_files_and_the_deleted_one_and_stays_out_of_the_goal()
    {
        var import = await Import(Feature());
        var oracle = Assert.Single(import.Manifest.Tasks).Oracle;
        Assert.Equal(["tests/Lib.Tests/CalcTests.cs", "tests/Lib.Tests/OldTests.cs"], oracle.Select(f => f.Path).Order());
        Assert.Equal(ReplayFile.Delete, oracle.Single(f => f.Path.EndsWith("OldTests.cs", StringComparison.Ordinal)).Change);
        Assert.Equal("class CalcTests { void Adds() { } }\n", System.Text.Encoding.UTF8.GetString(import.OracleFiles["pr-7"]["tests/Lib.Tests/CalcTests.cs"]));
        Assert.Equal(["tests/Lib.Tests/Lib.Tests.csproj"], Assert.Single(import.Manifest.Tasks).Projects);
    }

    [Fact]
    public async Task The_task_set_validates_and_its_acceptance_script_is_a_file_the_set_points_at()
    {
        var import = await Import(Feature());
        var dir = Path.Combine(_dir.Path, "out");
        var set = ReplayStore.Write(dir, import, out var problems);
        Assert.Empty(problems);
        Assert.True(File.Exists(Path.Combine(dir, "accept", "pr-7.sh")));
        Assert.True(File.Exists(Path.Combine(dir, "oracle", "pr-7", "tests/Lib.Tests/CalcTests.cs")));
        Assert.Equal("accept/pr-7.sh", set.Tasks[0].Acceptance.Script);
    }

    [Fact]
    public async Task Pull_requests_that_cannot_be_tasks_are_left_out_with_a_named_reason()
    {
        var import = await Import(
            Feature(),
            new ReplayPr(8, "test: more cases", "", _testsOnly),
            new ReplayPr(9, "feat: subtract", "", _noTests),
            new ReplayPr(10, "chore(main): release 1.2.3", "", _feature),
            new ReplayPr(11, "feat: lost", "", new string('0', 40)));
        Assert.Equal(["pr-7"], import.Set.Tasks.Select(t => t.Id));
        Assert.Equal(
            new[] { (8, ReplayRejection.NoSourceChange), (9, ReplayRejection.NoTestFiles), (10, ReplayRejection.ReleasePr), (11, ReplayRejection.NoMergeParent) }.Order(),
            import.Rejected.Select(r => (r.PullRequest, r.Reason)).Order());
    }

    [Fact]
    public async Task A_title_that_names_a_test_file_is_refused_not_edited()
    {
        var import = await Import(new ReplayPr(7, "fix: CalcTests adds", "Body.", _feature));
        Assert.Empty(import.Set.Tasks);
        Assert.Equal(ReplayRejection.GoalLeaksTests, Assert.Single(import.Rejected).Reason);
    }

    [Fact]
    public async Task The_acceptance_script_puts_the_oracle_on_the_branch_and_passes_only_when_tests_ran()
    {
        var import = await Import(Feature());
        var work = Path.Combine(_dir.Path, "work");
        Directory.CreateDirectory(Path.Combine(work, "tests/Lib.Tests"));
        File.WriteAllText(Path.Combine(work, "tests/Lib.Tests/CalcTests.cs"), "the session's own version\n");
        File.WriteAllText(Path.Combine(work, "tests/Lib.Tests/OldTests.cs"), "old\n");
        var bin = Path.Combine(_dir.Path, "bin");
        Directory.CreateDirectory(bin);
        var fake = Path.Combine(bin, "dotnet");
        File.WriteAllText(fake, "#!/bin/sh\necho \"$@\" >> \"$FAKE_LOG\"\n[ \"$FAKE_RESULT\" = pass ] && echo 'Passed!  - Failed: 0, Passed: 1'\n[ \"$FAKE_RESULT\" = pass ]\n");
        if (OperatingSystem.IsWindows())
            return; // the acceptance script is a POSIX shell script
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        int Run(string result)
        {
            var psi = new ProcessStartInfo("sh") { WorkingDirectory = work, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(import.Scripts["pr-7"]);
            psi.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            psi.Environment["FAKE_LOG"] = Path.Combine(_dir.Path, "dotnet.log");
            psi.Environment["FAKE_RESULT"] = result;
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode;
        }

        Assert.Equal(0, Run("pass"));
        Assert.Equal("class CalcTests { void Adds() { } }\n", File.ReadAllText(Path.Combine(work, "tests/Lib.Tests/CalcTests.cs")));
        Assert.False(File.Exists(Path.Combine(work, "tests/Lib.Tests/OldTests.cs")));
        Assert.Contains("test tests/Lib.Tests/Lib.Tests.csproj --nologo --filter FullyQualifiedName~CalcTests", File.ReadAllText(Path.Combine(_dir.Path, "dotnet.log")));
        Assert.NotEqual(0, Run("fail"));
    }

    private sealed class Scripted(Func<string, int> exit) : IReplayExecutor
    {
        public Task<(int ExitCode, string Tail)> RunAsync(string repositoryPath, string commit, IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult((exit(commit), "tail"));
    }

    [Theory]
    [InlineData(1, 0, true, ReplayVerdict.ValidReason)]
    [InlineData(0, 0, false, ReplayVerdict.PassesAtParent)]
    [InlineData(1, 1, false, ReplayVerdict.FailsAtMerge)]
    [InlineData(-2, 0, false, ReplayVerdict.Unreachable)]
    public async Task A_task_is_valid_only_when_its_oracle_fails_at_the_parent_and_passes_at_the_merge(int parent, int merge, bool valid, string reason)
    {
        var import = await Import(Feature());
        var dir = Path.Combine(_dir.Path, "out");
        var set = ReplayStore.Write(dir, import, out _);
        var verdict = Assert.Single(await ReplayBaseline.RunAsync(set, import.Manifest, _repo, new Scripted(c => c == _base ? parent : merge), null, 1, default));
        Assert.Equal(valid, verdict.Valid);
        Assert.Equal(reason, verdict.Reason);
    }
}
