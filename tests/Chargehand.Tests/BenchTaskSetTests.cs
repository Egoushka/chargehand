using Chargehand.Bench;

namespace Chargehand.Tests;

/// <summary>ADR 0052: a task set is checked as a whole before anything starts, and every rule names the repository or task it is about.</summary>
public class BenchTaskSetTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static readonly string[] OpenSourceLicenses = ["MIT", "Apache-2.0", "BSD-3-Clause", "BSD-2-Clause", "ISC"];

    private static BenchRepository Repo(string name = "lib", string commit = Sha, string url = "https://example.com/org/lib.git") =>
        new(name, "python", url, commit, "MIT", ["python3", "-m", "unittest"]);

    private static BenchTask Task(string id = "t1", string repository = "lib", string kind = "change", string size = "S", string? script = null, IReadOnlyList<string>? argv = null,
        IReadOnlyList<string>? answers = null, string goal = "Add a function.") =>
        new(id, repository, kind, size, goal, new BenchAcceptance(script, script is null ? argv ?? ["true"] : null), answers);

    private static TaskSet Set(IReadOnlyList<BenchRepository>? repositories = null, IReadOnlyList<BenchTask>? tasks = null) =>
        new(TaskSet.SchemaId, "set", null, repositories ?? [Repo()], tasks ?? [Task()]);

    [Fact]
    public void A_small_valid_set_has_no_problems() => Assert.Empty(TaskSetLoader.Validate(Set()));

    [Theory]
    [InlineData("main")]
    [InlineData("v1.2.3")]
    [InlineData("0123456")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567")]
    public void A_pin_must_be_a_full_lower_case_commit_id(string commit)
    {
        var problems = TaskSetLoader.Validate(Set([Repo(commit: commit)]));
        Assert.Contains(problems, p => p.Contains("repository 'lib'") && p.Contains("40-character"));
    }

    [Theory]
    [InlineData("http://example.com/org/lib.git", "https")]
    [InlineData("https://user:pw@example.com/org/lib.git", "credentials")]
    [InlineData("https://example.com/org/lib.git?token=x", "credentials")]
    [InlineData("https://localhost/lib.git", "public host")]
    [InlineData("https://10.0.0.5/lib.git", "public host")]
    [InlineData("https://build.internal/lib.git", "public host")]
    [InlineData("/home/me/lib", "https")]
    [InlineData("git@github.com:org/lib.git", "https")]
    public void A_repository_must_be_a_public_https_url(string url, string mentions) =>
        Assert.Contains(TaskSetLoader.Validate(Set([Repo(url: url)])), p => p.Contains("url") && p.Contains(mentions));

    [Fact]
    public void Ids_and_names_must_be_unique_and_well_formed()
    {
        var problems = TaskSetLoader.Validate(Set([Repo(), Repo()], [Task("a"), Task("a"), Task("Bad Id")]));
        Assert.Contains(problems, p => p.Contains("repository name 'lib' is used more than once"));
        Assert.Contains(problems, p => p.Contains("task id 'a' is used more than once"));
        Assert.Contains(problems, p => p.Contains("'Bad Id'") && p.Contains("id must be"));
    }

    [Fact]
    public void A_task_names_a_repository_of_the_set_and_a_repository_has_a_task()
    {
        var problems = TaskSetLoader.Validate(Set([Repo(), Repo("unused")], [Task(repository: "elsewhere")]));
        Assert.Contains(problems, p => p.Contains("repository 'elsewhere' is not in the set"));
        Assert.Contains(problems, p => p.Contains("repository 'unused'") && p.Contains("no task uses it"));
    }

    [Fact]
    public void Kind_and_size_are_closed_sets()
    {
        var problems = TaskSetLoader.Validate(Set(tasks: [Task(kind: "hard"), Task("b", size: "XL")]));
        Assert.Contains(problems, p => p.Contains("kind must be change, impossible or question"));
        Assert.Contains(problems, p => p.Contains("size must be S, M or L"));
    }

    [Fact]
    public void A_question_task_needs_answers_and_no_other_kind_has_them()
    {
        Assert.Contains(TaskSetLoader.Validate(Set(tasks: [Task(kind: "question")])), p => p.Contains("needs answers"));
        Assert.Contains(TaskSetLoader.Validate(Set(tasks: [Task(kind: "question", answers: [" "])])), p => p.Contains("needs answers"));
        Assert.Contains(TaskSetLoader.Validate(Set(tasks: [Task(answers: ["yes"])])), p => p.Contains("answers belong to a question task"));
        Assert.Empty(TaskSetLoader.Validate(Set(tasks: [Task(kind: "question", answers: ["yes"])])));
    }

    [Fact]
    public void A_goal_is_present_and_not_longer_than_a_task()
    {
        Assert.Contains(TaskSetLoader.Validate(Set(tasks: [Task(goal: " ")])), p => p.Contains("goal is empty"));
        Assert.Contains(TaskSetLoader.Validate(Set(tasks: [Task(goal: new string('x', TaskSetLoader.MaxGoalLength + 1))])), p => p.Contains("longer than"));
    }

    [Fact]
    public void An_acceptance_check_is_a_script_or_a_command_and_the_script_must_exist()
    {
        using var dir = new TempDir();
        dir.Write("acceptance/ok.sh", "exit 0\n");
        dir.Write("acceptance/blank.sh", "  \n");
        var set = Set(tasks: [Task("a", script: "acceptance/ok.sh"), Task("b", script: "acceptance/missing.sh"), Task("c", script: "acceptance/blank.sh"), Task("d", script: "../outside.sh")]) with { Directory = dir.Path };
        var problems = TaskSetLoader.Validate(set);
        Assert.DoesNotContain(problems, p => p.Contains("task 'a'"));
        Assert.Contains(problems, p => p.Contains("task 'b'") && p.Contains("does not exist"));
        Assert.Contains(problems, p => p.Contains("task 'c'") && p.Contains("is empty"));
        Assert.Contains(problems, p => p.Contains("task 'd'") && p.Contains("relative path inside"));

        var neither = Set(tasks: [Task() with { Acceptance = new BenchAcceptance() }, Task("e") with { Acceptance = new BenchAcceptance("a.sh", ["true"]) }]);
        Assert.Equal(2, TaskSetLoader.Validate(neither).Count(p => p.Contains("exactly one of script and argv")));
    }

    [Fact]
    public void The_pinned_commit_replaces_the_placeholder_in_the_acceptance_command()
    {
        using var dir = new TempDir();
        dir.Write("acceptance/guard.sh", "git diff --quiet {base} HEAD -- tests\n");
        var set = Set(tasks: [Task("a", script: "acceptance/guard.sh"), Task("b", argv: ["git", "diff", "--quiet", "{base}", "HEAD"])]) with { Directory = dir.Path };
        var script = TaskSetLoader.AcceptanceArgv(set, set.Tasks[0], set.Repositories[0]);
        Assert.Equal(["sh", "-c", $"git diff --quiet {Sha} HEAD -- tests\n"], script);
        Assert.Equal(["git", "diff", "--quiet", Sha, "HEAD"], TaskSetLoader.AcceptanceArgv(set, set.Tasks[1], set.Repositories[0]));
    }

    [Fact]
    public void Load_reads_a_file_and_reports_what_is_wrong_with_it()
    {
        using var dir = new TempDir();
        var good = dir.Write("good.json", """
            { "schema": "bench-taskset/v1", "name": "set", // comments are fine
              "repositories": [ { "name": "lib", "ecosystem": "python", "url": "https://example.com/org/lib.git", "commit": "0123456789abcdef0123456789abcdef01234567", "license": "MIT", "test": ["true"] } ],
              "tasks": [ { "id": "t1", "repository": "lib", "kind": "change", "size": "S", "goal": "Do it.", "acceptance": { "argv": ["true"] } } ] }
            """);
        var set = TaskSetLoader.Load(good, out var problems);
        Assert.NotNull(set);
        Assert.Empty(problems);
        Assert.Equal(64, set.Sha256.Length);
        Assert.Equal(dir.Path, set.Directory);

        Assert.Null(TaskSetLoader.Load(dir.Write("broken.json", "{ not json"), out var broken));
        Assert.Contains("not a task set", Assert.Single(broken));
        Assert.Null(TaskSetLoader.Load(Path.Combine(dir.Path, "missing.json"), out var missing));
        Assert.Single(missing);
        var wrong = TaskSetLoader.Load(dir.Write("wrong.json", """{ "schema": "other/v9", "name": "x" }"""), out var wrongProblems);
        Assert.NotNull(wrong);
        Assert.Contains(wrongProblems, p => p.Contains("schema must be"));
    }

    [Fact]
    public void The_shipped_set_is_valid_and_keeps_to_what_ADR_0052_promises()
    {
        var set = TaskSetLoader.Load(Chargehand.Tests.Repo.Path("benchmarks", "core.json"), out var problems);
        Assert.NotNull(set);
        Assert.Empty(problems);
        Assert.InRange(set.Tasks.Count, 12, 20);
        Assert.InRange(set.Repositories.Count, 4, 6);
        Assert.InRange(set.Tasks.Count(t => t.Kind == BenchTask.Impossible), 2, 3);
        Assert.Equal(2, set.Tasks.Count(t => t.Kind == BenchTask.Question));
        Assert.True(set.Repositories.Select(r => r.Ecosystem).Distinct().Count() >= 4);
        Assert.All(set.Tasks.GroupBy(t => t.Size), g => Assert.NotEmpty(g));
        Assert.Equal(["L", "M", "S"], set.Tasks.Select(t => t.Size).Distinct().Order());
        // Public, open source, pinned: no repository is anything else, and every pin is one commit.
        Assert.All(set.Repositories, r =>
        {
            Assert.StartsWith("https://github.com/", r.Url);
            Assert.Matches("^[0-9a-f]{40}$", r.Commit);
            Assert.Contains(r.License, OpenSourceLicenses);
        });
        // Every check is a file in the repository, and none of its text is in a goal.
        foreach (var task in set.Tasks)
        {
            var argv = TaskSetLoader.AcceptanceArgv(set, task, set.Repositories.First(r => r.Name == task.Repository));
            Assert.Equal("sh", argv[0]);
            Assert.DoesNotContain("ChargehandBench", task.Goal);
            Assert.DoesNotContain("acceptance", task.Goal, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(set.Tasks, t => t.Kind == BenchTask.Impossible && t.Acceptance.Script is null);
    }
}
