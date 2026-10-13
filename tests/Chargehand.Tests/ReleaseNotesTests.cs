using System.Diagnostics;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.ReleaseNotes;
using Chargehand.RunLog;

namespace Chargehand.Tests;

/// <summary>CHARGEHAND-191: the read-only preset that writes release notes, and the command that turns a git range into its request.</summary>
public class ReleaseNotesTests
{
    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)} failed");
    }

    private static string Commit(string dir, string message, string? body = null, params string[] extra)
    {
        File.AppendAllText(Path.Combine(dir, "log.txt"), message + "\n");
        Git(dir, "add", "log.txt");
        string[] args = ["-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", message, .. body is null ? [] : new[] { "-m", body }, .. extra];
        Git(dir, args);
        return Git(dir, "rev-parse", "HEAD");
    }

    /// <summary>A repository with a tagged first commit and, after the tag, a feature, a fix, a merge of a branch named like a pull request, and a chore.</summary>
    private static (string Dir, string First, string Feature, string Fix, string Merge, string Chore) History(TempDir root)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root.Path, "repo")).FullName;
        Git(dir, "init", "-q", "-b", "main");
        var first = Commit(dir, "chore: start");
        Git(dir, "tag", "v1.0.0");
        var feature = Commit(dir, "feat(api): add the export route");
        var fix = Commit(dir, "fix: stop a double\tspace  in names");
        Git(dir, "checkout", "-q", "-b", "side");
        Commit(dir, "wip on the side branch");
        Git(dir, "checkout", "-q", "main");
        Git(dir, "-c", "user.name=t", "-c", "user.email=t@example.com", "merge", "-q", "--no-ff", "side", "-m", "Merge pull request #7 from x/side", "-m", "Speed up the export");
        var merge = Git(dir, "rev-parse", "HEAD");
        var chore = Commit(dir, "chore: bump");
        return (dir, first, feature, fix, merge, chore);
    }

    [Fact]
    public async Task The_range_becomes_one_input_per_first_parent_commit_newest_first_and_the_request_pins_the_target()
    {
        using var root = new TempDir();
        var h = History(root);

        var (request, problem) = await ReleaseNotesCli.BuildAsync(h.Dir, "v1.0.0", "HEAD", default);

        Assert.Null(problem);
        Assert.Equal("release-notes", request!.Context.Preset);
        Assert.Equal(new RepositoryRef(h.Dir, h.Chore), request.Context.Repository);
        Assert.Empty(ContractSchemasProblems(request));
        Assert.Equal(["range", h.Chore[..12], h.Merge[..12], h.Fix[..12], h.Feature[..12]], request.Inputs!.Select(i => i.Id));
        Assert.Equal("range", request.Inputs![0].Kind);
        Assert.Contains("4 of 4 first-parent commits", request.Inputs[0].Text, StringComparison.Ordinal);
        // The side branch's own commit is not listed; the merge is titled by the pull request's title, and whitespace is collapsed.
        Assert.Equal(["chore: bump", "Speed up the export", "fix: stop a double space in names", "feat(api): add the export route"], request.Inputs.Skip(1).Select(i => i.Text));
        Assert.All(request.Inputs.Skip(1), i => Assert.Equal("commit", i.Kind));
    }

    [Fact]
    public async Task A_long_range_lists_the_newest_commits_and_says_how_many_it_left_out()
    {
        using var root = new TempDir();
        var dir = Directory.CreateDirectory(Path.Combine(root.Path, "repo")).FullName;
        Git(dir, "init", "-q", "-b", "main");
        Commit(dir, "first");
        Git(dir, "tag", "v0");
        for (var i = 1; i <= ReleaseNotesCli.MaxCommits + 5; i++)
            Commit(dir, $"change {i}");

        var (request, _) = await ReleaseNotesCli.BuildAsync(dir, "v0", "HEAD", default);

        Assert.Equal(ReleaseNotesCli.MaxCommits + 1, request!.Inputs!.Count);
        Assert.Contains($"{ReleaseNotesCli.MaxCommits} of {ReleaseNotesCli.MaxCommits + 5}", request.Inputs[0].Text, StringComparison.Ordinal);
        Assert.Contains("the oldest 5 are not listed", request.Inputs[0].Text, StringComparison.Ordinal);
        Assert.Equal($"change {ReleaseNotesCli.MaxCommits + 5}", request.Inputs[1].Text);
    }

    [Theory]
    [InlineData("v9", "HEAD", "is not a commit or tag")]
    [InlineData("v1.0.0", "nope", "is not a commit or tag")]
    [InlineData("HEAD", "v1.0.0", "is not an ancestor")]
    [InlineData("v1.0.0", "v1.0.0", "no commits between")]
    [InlineData("--output=x", "HEAD", "cannot start with '-'")]
    public async Task An_unusable_range_is_refused_with_the_reason(string from, string to, string expected)
    {
        using var root = new TempDir();
        var h = History(root);

        var (request, problem) = await ReleaseNotesCli.BuildAsync(h.Dir, from, to, default);

        Assert.Null(request);
        Assert.Contains(expected, problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_command_prints_a_request_that_run_can_read_and_exits_2_on_a_usage_error()
    {
        using var root = new TempDir();
        var h = History(root);
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(0, await ReleaseNotesCli.RunAsync([h.Dir, "--from", "v1.0.0"], root.Path, output, error, default));
        var request = JsonSerializer.Deserialize<RunRequest>(output.ToString(), ContractJson.Options)!;
        Assert.Equal("release-notes", request.Context.Preset);
        Assert.Equal(h.Chore, request.Context.Repository!.Commit);
        Assert.Equal("", error.ToString());

        Assert.Equal(2, await ReleaseNotesCli.RunAsync([h.Dir], root.Path, new StringWriter(), error, default));
        Assert.Equal(2, await ReleaseNotesCli.RunAsync(["--from", "v1.0.0"], root.Path, new StringWriter(), error, default));
        Assert.Contains("usage: chargehand release-notes", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await ReleaseNotesCli.RunAsync([h.Dir, "--from", "v9"], root.Path, new StringWriter(), new StringWriter(), default));
    }

    [Fact]
    public void Is_read_only_and_answers_only()
    {
        var preset = Preset.Load(Repo.Path("presets"), "release-notes");
        Assert.Equal(["answer"], preset.AllowedActions);
        var worker = preset.NodeKinds["worker"];
        var rules = worker.Permissions.Select(p => $"{p.Action} {p.Resource} {p.Effect}").ToList();
        Assert.Contains("edit * deny", rules);
        Assert.Contains("shell * deny", rules);
        Assert.Null(worker.Services);
        Assert.False(worker.Writes);
    }

    [Fact]
    public void The_prompt_asks_for_Keep_a_Changelog_sections_and_a_commit_behind_every_line()
    {
        var prompt = File.ReadAllText(Repo.Path("prompts", "preset", "release-notes.md"));
        foreach (var section in new[] { "Added", "Changed", "Deprecated", "Removed", "Fixed", "Security" })
            Assert.Contains($"\"### {section}\"", prompt, StringComparison.Ordinal);
        Assert.Contains("kind \"commit\"", prompt, StringComparison.Ordinal);
        Assert.Contains("kind \"input\"", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_keeps_a_line_whose_commit_exists_and_moves_one_that_cites_a_missing_commit_to_the_open_questions()
    {
        using var root = new TempDir();
        var h = History(root);
        var (request, _) = await ReleaseNotesCli.BuildAsync(h.Dir, "v1.0.0", "HEAD", default);
        var reply = $$"""
            ```json
            {"status":"completed","summary":"## [VERSION] - YYYY-MM-DD\n\n### Added\n\n- Add the export route ({{h.Feature[..12]}})\n- Add a thing nobody committed (deadbeefdead)",
             "claims":[{"text":"Add the export route","evidence":["e1","e2"],"confidence":0.9},{"text":"Add a thing nobody committed","evidence":["e3"],"confidence":0.5}],
             "evidence":[{"id":"e1","kind":"commit","locator":"{{h.Feature[..12]}}"},{"id":"e2","kind":"input","locator":"{{h.Feature[..12]}}"},{"id":"e3","kind":"commit","locator":"deadbeefdead"}],
             "artifacts":[],"open_questions":[],"confidence":0.8}
            ```
            """;

        var result = await Runs.Orchestrator(new ScriptedRuntime(reply), root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")))
            .RunAsync(request!, CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        var claim = Assert.Single(result.Claims);
        Assert.Equal("Add the export route", claim.Text);
        Assert.Contains(result.Evidence, e => (e.Kind, e.Locator) == (EvidenceKind.Commit, h.Feature[..12]));
        Assert.Contains(result.OpenQuestions, q => q.Contains("Unverified:", StringComparison.Ordinal) && q.Contains("nobody committed", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> ContractSchemasProblems(RunRequest request) =>
        ContractSchemas.Validate(ContractSchemas.Request, JsonSerializer.SerializeToElement(request, ContractJson.Options));
}
