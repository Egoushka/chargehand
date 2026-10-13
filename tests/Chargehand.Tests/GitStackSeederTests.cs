using System.Diagnostics;
using Chargehand.Driven;

namespace Chargehand.Tests;

/// <summary>ADR 0049 with real git: the dependencies' bundles become one branch under the new task's name, merged in the order listed, and a conflict names its paths.</summary>
public sealed class GitStackSeederTests : IDisposable
{
    private static readonly string[] Identity = ["-c", "user.name=t", "-c", "user.email=t@example.test", "-c", "commit.gpgsign=false"];

    private readonly TempDir _dir = new();
    private readonly string _source;
    private readonly string _scratch;
    private readonly string _commit;

    public GitStackSeederTests()
    {
        _source = Path.Combine(_dir.Path, "src");
        _scratch = Path.Combine(_dir.Path, "scratch");
        Directory.CreateDirectory(_source);
        Git(_source, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(_source, "a.txt"), "one\ntwo\nthree\n");
        File.WriteAllText(Path.Combine(_source, "b.txt"), "alpha\nbeta\n");
        Git(_source, "add", "-A");
        Git(_source, "commit", "-q", "-m", "base");
        _commit = Git(_source, "rev-parse", "HEAD").Trim();
    }

    public void Dispose() => _dir.Dispose();

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

    /// <summary>A task's session: a branch off the base in its own clone, changing one file, bundled the way the driver does.</summary>
    private StackInput Session(string id, string file, string content)
    {
        var work = Path.Combine(_dir.Path, $"work-{id}");
        Git(_dir.Path, "clone", "-q", _source, work);
        var branch = $"chargehand/run-{id}";
        Git(work, "checkout", "-q", "-b", branch);
        File.WriteAllText(Path.Combine(work, file), content);
        Git(work, "add", "-A");
        Git(work, "commit", "-q", "-m", $"task {id}");
        var bundle = Path.Combine(_dir.Path, $"{id}.bundle");
        Git(work, "bundle", "create", bundle, branch, "--not", "--remotes");
        return new StackInput(id, branch, bundle);
    }

    private Task<StackResult> Build(params StackInput[] inputs) =>
        new GitStackSeeder().BuildAsync(new StackRequest("chargehand/run-new", _source, _commit, _scratch, Path.Combine(_scratch, "run-new-stack"), Path.Combine(_dir.Path, "seed.bundle"), inputs), default);

    /// <summary>What the session container does with the seed: a workspace at the base commit fetches the branch from it.</summary>
    private string FetchedInto(string file)
    {
        var workspace = Path.Combine(_dir.Path, "workspace");
        Git(_dir.Path, "clone", "-q", _source, workspace);
        Git(workspace, "fetch", "-q", Path.Combine(_dir.Path, "seed.bundle"), "refs/heads/chargehand/run-new");
        return Git(workspace, "show", $"FETCH_HEAD:{file}");
    }

    [Fact]
    public async Task One_dependency_becomes_the_new_tasks_branch_under_the_new_tasks_name()
    {
        var made = await Build(Session("a", "b.txt", "alpha\nbeta\ngamma\n"));

        Assert.True(made.Made);
        Assert.Equal(1, made.Commits);
        Assert.Contains("refs/heads/chargehand/run-new", Git(_dir.Path, "bundle", "list-heads", Path.Combine(_dir.Path, "seed.bundle")));
        Assert.Equal("alpha\nbeta\ngamma\n", FetchedInto("b.txt"));
    }

    [Fact]
    public async Task Several_dependencies_are_merged_in_the_order_listed()
    {
        var first = Session("a", "a.txt", "ONE\ntwo\nthree\n");
        var second = Session("b", "b.txt", "alpha\nBETA\n");

        var made = await Build(first, second);

        Assert.True(made.Made);
        Assert.Equal(3, made.Commits);                                     // both tasks' commits and the merge
        Assert.Equal("ONE\ntwo\nthree\n", FetchedInto("a.txt"));
        Assert.Equal("alpha\nBETA\n", Git(Path.Combine(_dir.Path, "workspace"), "show", "FETCH_HEAD:b.txt"));
    }

    [Fact]
    public async Task A_conflict_names_the_paths_and_the_task_that_could_not_be_merged_and_leaves_no_bundle()
    {
        var made = await Build(Session("a", "a.txt", "ONE\ntwo\nthree\n"), Session("b", "a.txt", "uno\ntwo\nthree\n"));

        Assert.False(made.Made);
        Assert.Equal(["a.txt"], made.Conflicts);
        Assert.Equal("b", made.ConflictWith);
        Assert.Null(made.Failure);
        Assert.False(File.Exists(Path.Combine(_dir.Path, "seed.bundle")));
    }

    [Fact]
    public async Task A_bundle_that_is_not_one_is_a_failure_not_a_conflict()
    {
        var broken = Path.Combine(_dir.Path, "broken.bundle");
        File.WriteAllText(broken, "not a bundle");

        var made = await Build(new StackInput("a", "chargehand/run-a", broken));

        Assert.False(made.Made);
        Assert.Empty(made.Conflicts);
        Assert.Contains("task 'a' does not verify against the base", made.Failure);
    }
}
