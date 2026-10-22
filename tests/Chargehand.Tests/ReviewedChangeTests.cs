using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Ledger.Api;
using Chargehand.RunLog;

namespace Chargehand.Tests;

/// <summary>A review is pinned to the commit the caller changed from and gets the change as a diff input. The worker's checkout holds that diff applied, so the changed code is on disk and a file the change adds can be
/// cited. The shared checkout of the base commit is never written.</summary>
public class ReviewedChangeTests
{
    private const string AddsFile = "diff --git a/src/New.cs b/src/New.cs\nnew file mode 100644\nindex 0000000..e69de29\n--- /dev/null\n+++ b/src/New.cs\n@@ -0,0 +1,2 @@\n+class New { }\n+// second\n";

    private const string EditsReadme = "diff --git a/README.md b/README.md\nindex ce01362..94954ab 100644\n--- a/README.md\n+++ b/README.md\n@@ -1 +1 @@\n-hello\n+hello world\n";

    private static string Reply(string locator) => $$"""
        ```json
        {"status":"completed","summary":"One finding.",
         "claims":[{"text":"The new class is empty.","evidence":["e1"],"confidence":0.8}],
         "evidence":[{"id":"e1","kind":"file","locator":"{{locator}}"}],
         "artifacts":[],"open_questions":[],"confidence":0.8}
        ```
        """;

    private static RunRequest Review(RepositoryRef repo, string diff) => new("request/v1",
        "Review the change against its goal.", new RequestContext(false, "review", Repository: repo),
        [new CallerInput("goal", "goal", "Add a class."), new CallerInput("diff", "diff", diff), new CallerInput("tests", "test-output", "exit 0")], []);

    private static async Task<(ResultContract Result, ScriptedRuntime Runtime)> RunAsync(TempDir root, RepositoryRef repo, string diff, string locator)
    {
        var runtime = new ScriptedRuntime(Reply(locator));
        var result = await Runs.Orchestrator(runtime, root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"))).RunAsync(Review(repo, diff), CancellationToken.None);
        return (result, runtime);
    }

    [Fact]
    public async Task A_file_the_change_adds_can_be_cited()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);

        var (r, runtime) = await RunAsync(root, repo, AddsFile, "src/New.cs:1");

        Assert.Equal(ResultStatus.Completed, r.Status);
        Assert.Single(r.Claims);
        Assert.DoesNotContain(r.OpenQuestions, q => q.StartsWith("Unverified:", StringComparison.Ordinal));
        Assert.Equal("class New { }\n// second\n", File.ReadAllText(Path.Combine(Assert.Single(runtime.Created).Directory, "src", "New.cs")));
    }

    [Fact]
    public async Task A_line_the_change_edits_is_read_as_changed()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);

        var (_, runtime) = await RunAsync(root, repo, EditsReadme, "README.md:1");

        Assert.Equal("hello world\n", File.ReadAllText(Path.Combine(Assert.Single(runtime.Created).Directory, "README.md")));
    }

    [Fact]
    public async Task The_shared_checkout_of_the_base_commit_is_not_written()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);

        var (_, first) = await RunAsync(root, repo, AddsFile, "src/New.cs:1");
        var (_, second) = await RunAsync(root, repo, "", "README.md:1");

        var changed = Assert.Single(first.Created).Directory;
        var plain = Assert.Single(second.Created).Directory;
        Assert.NotEqual(changed, plain);
        Assert.False(File.Exists(Path.Combine(plain, "src", "New.cs")));
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(plain, "README.md")));
    }

    [Fact]
    public async Task A_second_review_of_the_same_diff_reuses_the_changed_checkout()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);

        var (_, first) = await RunAsync(root, repo, AddsFile, "src/New.cs:1");
        var (_, second) = await RunAsync(root, repo, AddsFile, "src/New.cs:1");

        Assert.Equal(Assert.Single(first.Created).Directory, Assert.Single(second.Created).Directory);
    }

    [Fact]
    public async Task A_change_the_commit_already_holds_is_left_as_it_is()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        Directory.CreateDirectory(Path.Combine(repo.Path, "src"));
        File.WriteAllText(Path.Combine(repo.Path, "src", "New.cs"), "class New { }\n// second\n");
        repo = Runs.Commit(repo, "src/New.cs");

        var (r, runtime) = await RunAsync(root, repo, AddsFile, "src/New.cs:2");

        Assert.Equal(ResultStatus.Completed, r.Status);
        Assert.DoesNotContain(r.OpenQuestions, q => q.StartsWith("Unverified:", StringComparison.Ordinal));
        Assert.EndsWith(repo.Commit[..12], Assert.Single(runtime.Created).Directory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_files_of_a_cut_diff_that_still_apply_are_applied()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var cut = AddsFile + "diff --git a/src/Other.cs b/src/Other.cs\nnew file mode 100644\n--- /dev/null\n+++ b/src/Other.cs\n@@ -0,0 +1,40 @@\n+class Other { }\n\n[diff truncated at 60000 characters]\n";

        var (r, runtime) = await RunAsync(root, repo, cut, "src/New.cs:1");

        Assert.Equal(ResultStatus.Completed, r.Status);
        var directory = Assert.Single(runtime.Created).Directory;
        Assert.True(File.Exists(Path.Combine(directory, "src", "New.cs")));
        Assert.False(File.Exists(Path.Combine(directory, "src", "Other.cs")));
    }

    [Fact]
    public async Task A_diff_that_does_not_apply_leaves_the_worker_on_the_base_commit()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);

        var (r, runtime) = await RunAsync(root, repo, string.Concat(Enumerable.Repeat("+x\n", 100)) + "\n[diff truncated at 60000 characters]\n", "README.md:1");

        Assert.Equal(ResultStatus.Completed, r.Status);
        Assert.EndsWith(repo.Commit[..12], Assert.Single(runtime.Created).Directory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_header_with_no_hunk_is_not_applied()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);

        var (_, runtime) = await RunAsync(root, repo, "diff --git a/src/Stub.cs b/src/Stub.cs\nnew file mode 100644\n--- /dev/null\n+++ b/src/Stub.cs\n(the full diff is large; see the worktree)\n", "README.md:1");

        Assert.EndsWith(repo.Commit[..12], Assert.Single(runtime.Created).Directory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_the_change_adds_and_the_preset_denies_reading_is_left_out_of_the_checkout()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        const string diff = "diff --git a/.env b/.env\nnew file mode 100644\n--- /dev/null\n+++ b/.env\n@@ -0,0 +1 @@\n+TOKEN=[masked:assignment]\n" + AddsFile;

        var (r, runtime) = await RunAsync(root, repo, diff, "src/New.cs:1");

        Assert.Equal(ResultStatus.Completed, r.Status);
        var directory = Assert.Single(runtime.Created).Directory;
        Assert.False(File.Exists(Path.Combine(directory, ".env")));
        Assert.True(File.Exists(Path.Combine(directory, "src", "New.cs")));
        Assert.Contains(".env", Assert.Single(runtime.Prompts)[Assert.Single(runtime.Prompts).IndexOf("left out of your checkout", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_task_text_says_how_much_of_the_change_is_in_the_checkout()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var cut = AddsFile + "diff --git a/src/Other.cs b/src/Other.cs\nnew file mode 100644\n--- /dev/null\n+++ b/src/Other.cs\n@@ -0,0 +1,40 @@\n+class Other { }\n";

        var (_, runtime) = await RunAsync(root, repo, cut, "src/New.cs:1");

        var prompt = Assert.Single(runtime.Prompts);
        Assert.Contains("1 of 2 files of the diff input are in it", prompt, StringComparison.Ordinal);
        Assert.Contains("src/Other.cs", prompt, StringComparison.Ordinal);
    }
}
