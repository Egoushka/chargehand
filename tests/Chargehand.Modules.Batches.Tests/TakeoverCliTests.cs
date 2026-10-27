using System.Diagnostics;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Ledger.Api;

namespace Chargehand.Modules.Batches.Tests;

/// <summary><c>chargehand takeover &lt;batch&gt;/&lt;task&gt;</c> (issue 443): a stalled driven task's pushed branch becomes a local worktree with its handoff, for a Claude Code session to continue.</summary>
public sealed class TakeoverCliTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("chargehand-takeover-");

    private string Remote => Path.Combine(_dir.FullName, "remote.git");

    private string Clone => Path.Combine(_dir.FullName, "clone");

    private JsonlRunLog Log => new(Path.Combine(_dir.FullName, "run-log.jsonl"));

    public void Dispose()
    {
        foreach (var file in _dir.EnumerateFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal;
        _dir.Delete(recursive: true);
    }

    private static string Git(string directory, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])["-c", "user.name=writer", "-c", "user.email=w@example.test", "-c", "commit.gpgsign=false", .. args])
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var p = Process.Start(psi)!;
        var (stdout, stderr) = (p.StandardOutput.ReadToEnd(), p.StandardError.ReadToEnd());
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {stdout}{stderr}");
        return stdout.Trim();
    }

    /// <summary>A remote with <c>main</c> and a task branch two commits ahead of it, and a clone that has only <c>main</c>. Returns the base commit.</summary>
    private string Fixture(string branch)
    {
        Directory.CreateDirectory(Remote);
        Git(Remote, "init", "-q", "--bare", "-b", "main");
        var work = Path.Combine(_dir.FullName, "work");
        Directory.CreateDirectory(work);
        Git(work, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(work, "a.txt"), "one\n");
        Git(work, "add", "-A");
        Git(work, "commit", "-q", "-m", "chore: start");
        var baseCommit = Git(work, "rev-parse", "HEAD");
        Git(work, "remote", "add", "origin", Remote);
        Git(work, "push", "-q", "origin", "main");
        Git(_dir.FullName, "clone", "-q", Remote, Clone);
        Git(work, "checkout", "-q", "-b", branch);
        File.WriteAllText(Path.Combine(work, "a.txt"), "one\ntwo\n");
        Git(work, "commit", "-q", "-am", "feat: second line");
        File.WriteAllText(Path.Combine(work, "b.txt"), "b\n");
        Git(work, "add", "-A");
        Git(work, "commit", "-q", "-m", "feat: add b");
        Git(work, "push", "-q", "origin", branch);
        return baseCommit;
    }

    private async Task Record(string batch, string task, string run, string status, string? branch, string baseCommit)
    {
        var rows = JsonSerializer.Serialize(new { tasks = new[] { new { id = task, status, run_id = run } } });
        await Log.AppendAsync(Result(batch, ResultStatus.Failed, [new Artifact("driven-batch", "application/json", "x", Content: rows)], []), default);
        await Log.AppendAsync(Start(run), default);
        Artifact[] artifacts = branch is null ? [] : [new Artifact("branch", "application/json", "x", Content: JsonSerializer.Serialize(new { branch, commit = "c", @base = baseCommit, pushed = true }))];
        await Log.AppendAsync(Result(run, ResultStatus.NeedsInput, artifacts, ["Should the second line be a constant?"]), default);
    }

    private static StartRecord Start(string run) =>
        new(run, At, "trace", new RunRequest("request/v1", "Add a second line to a.txt", new RequestContext(false, "code")), 1);

    private static RunRecord Result(string run, ResultStatus status, IReadOnlyList<Artifact> artifacts, IReadOnlyList<string> questions) =>
        new(run, At, At.AddMinutes(5), "code", null, null,
            new ResultContract("result/v1", run, "n", "trace", new PromptChain([], new AsSent("", "", "", "")), status, "s", [], [], artifacts, questions, 0.5, new Usage(1, 1, 0, 0, 0.1m)));

    private async Task<(int Exit, string Out, string Err)> Takeover(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await TakeoverCli.RunAsync(args, Log, Clone, output, error, default);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task A_stalled_tasks_branch_is_checked_out_with_its_handoff_and_the_command_to_open_a_session_is_printed()
    {
        const string branch = "chargehand/r-task";
        var baseCommit = Fixture(branch);
        await Record("b1", "t1", "r-task", "needs_input", branch, baseCommit);

        var (exit, output, error) = await Takeover("b1/t1");

        Assert.True(exit == 0, error);
        var worktree = Path.Combine(Clone, ".claude", "worktrees", "takeover-t1");
        Assert.Equal(branch, Git(worktree, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal(Git(Remote, "rev-parse", branch), Git(worktree, "rev-parse", "HEAD"));
        using var handoff = JsonDocument.Parse(File.ReadAllText(Path.Combine(worktree, ".chargehand", "handoff.json")));
        Assert.Equal(baseCommit, handoff.RootElement.GetProperty("base_commit").GetString());
        Assert.Equal(2, handoff.RootElement.GetProperty("commits").GetArrayLength());
        var progress = File.ReadAllText(Path.Combine(worktree, ".chargehand", "progress.md"));
        Assert.Contains("Add a second line to a.txt", progress, StringComparison.Ordinal);
        Assert.Contains("Should the second line be a constant?", progress, StringComparison.Ordinal);
        Assert.Contains("feat: add b", progress, StringComparison.Ordinal);
        Assert.Contains("b.txt", progress, StringComparison.Ordinal);
        Assert.Contains($"cd {worktree} && claude", output, StringComparison.Ordinal);
        Assert.Contains(".chargehand/progress.md", output, StringComparison.Ordinal);
        Assert.DoesNotContain(".chargehand", Git(worktree, "status", "--porcelain"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_takeover_of_the_same_task_reuses_the_worktree()
    {
        const string branch = "chargehand/r-task";
        var baseCommit = Fixture(branch);
        await Record("b1", "t1", "r-task", "needs_input", branch, baseCommit);

        Assert.Equal(0, (await Takeover("b1/t1")).Exit);
        var (exit, output, _) = await Takeover("b1/t1");

        Assert.Equal(0, exit);
        Assert.Contains("claude", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_task_with_no_pushed_branch_is_refused_and_a_bad_target_is_a_usage_error()
    {
        var baseCommit = Fixture("chargehand/r-task");
        await Record("b1", "t1", "r-task", "failed", null, baseCommit);

        var (exit, _, error) = await Takeover("b1/t1");
        Assert.Equal(1, exit);
        Assert.Contains("no pushed branch", error, StringComparison.Ordinal);

        Assert.Equal(1, (await Takeover("b1/nope")).Exit);
        Assert.Equal(2, (await Takeover("nothing-to-split")).Exit);
    }

    [Fact]
    public void The_console_shows_the_same_command_the_cli_accepts() =>
        Assert.Equal("chargehand takeover b1/t1", TakeoverCli.Command("b1", "t1"));
}
