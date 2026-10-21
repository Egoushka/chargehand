using System.Diagnostics;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Tasks.Tests;

/// <summary>ADR 0074: <c>chargehand why path:line</c> traces a line to the run that wrote it, or says that a person did.</summary>
public sealed class WhyCliTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("chargehand-why-");

    private string Repo => Path.Combine(_dir.FullName, "repo");

    private string Records => Path.Combine(_dir.FullName, "records");

    public void Dispose() => _dir.Delete(recursive: true);

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])["-c", "user.name=writer", "-c", "user.email=w@example.test", .. args])
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {text}");
    }

    private void Commit(string file, string content, string message)
    {
        Directory.CreateDirectory(Repo);
        if (!Directory.Exists(Path.Combine(Repo, ".git")))
            Git("init", "-q", "-b", "main");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Repo, file))!);
        File.WriteAllText(Path.Combine(Repo, file), content);
        Git("add", "-A");
        Git("commit", "-q", "-m", message);
    }

    private static string Stamped(string subject, string run, string? task = "t1") =>
        Provenance.Apply(subject, new Provenance(run, task, "claude-code 2.1.0", "sonnet", 0.5m, 1200, 40, "2 of 2 checks passed"));

    private async Task<(int Exit, string Out, string Err)> Why(string target, JsonlRunLog? log = null, params string[] more)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await WhyCli.RunAsync([target, .. more], log ?? new JsonlRunLog(Path.Combine(_dir.FullName, "run-log.jsonl")), Records, Repo, output, error, default);
        return (exit, output.ToString(), error.ToString());
    }

    private static StartRecord Start(string run, string goal) =>
        new(run, At, "trace", new RunRequest("request/v1", goal, new RequestContext(false, "code")), 1);

    [Fact]
    public async Task A_line_from_a_stamped_commit_names_the_run_its_trailers_and_the_goal_from_the_run_log()
    {
        Commit("a.txt", "one\ntwo\n", Stamped("feat: add two", "r-code"));
        var log = new JsonlRunLog(Path.Combine(_dir.FullName, "run-log.jsonl"));
        await log.AppendAsync(Start("r-code", "Add the second line"), default);

        var (exit, output, _) = await Why("a.txt:2", log);

        Assert.Equal(0, exit);
        Assert.Contains("run      r-code", output, StringComparison.Ordinal);
        Assert.Contains("spend    $0.50, 1200 tokens, 40% cache", output, StringComparison.Ordinal);
        Assert.Contains("checks   2 of 2 checks passed", output, StringComparison.Ordinal);
        Assert.Contains("goal     Add the second line", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_from_a_commit_without_trailers_is_said_to_be_a_persons()
    {
        Commit("a.txt", "one\n", "fix: by hand");

        var (exit, output, _) = await Why("a.txt:1");

        Assert.Equal(0, exit);
        Assert.Contains("no Chargehand-Run trailer", output, StringComparison.Ordinal);
        Assert.DoesNotContain("goal", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_the_log_does_not_know_is_said_so_and_the_trailers_still_print()
    {
        Commit("a.txt", "one\n", Stamped("feat: x", "r-elsewhere"));

        var (_, output, _) = await Why("a.txt:1");

        Assert.Contains("run      r-elsewhere", output, StringComparison.Ordinal);
        Assert.Contains("no record of run r-elsewhere", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_driven_session_is_found_through_the_batch_records_and_its_goal_through_the_batch_request()
    {
        Commit("a.txt", "one\n", Stamped("feat: x", "r-session"));
        var log = new JsonlRunLog(Path.Combine(_dir.FullName, "run-log.jsonl"));
        await log.AppendAsync(new StartRecord("batch-1", At, "trace", new RunRequest("request/v1", "batch", new RequestContext(false, "driven"), Driven: new RequestDriven([new DrivenTask("t1", Goal: "Retry twice on a timeout")])), 1), default);
        new TaskRecordStore(Records).Append("batch-1", new SessionRecord("t1", "r-session", "first", null, At, At.AddMinutes(5), "completed", null, 14, 1000, 200, 0, 0, 0.52m, null, 0, 1));

        var (_, output, _) = await Why("a.txt:1", log);

        Assert.Contains("session  first, completed, 14 turns, $0.52 (batch batch-1, task t1)", output, StringComparison.Ordinal);
        Assert.Contains("goal     Retry twice on a timeout", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_stream_lists_the_messages_that_edited_the_file()
    {
        Commit("src/a.txt", "one\n", Stamped("feat: x", "r1"));
        var stream = Path.Combine(_dir.FullName, "stream.jsonl");
        var edit = JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new
            {
                content = new object[]
        {
            new { type = "text", text = "I will make the retry count configurable." },
            new { type = "tool_use", name = "Edit", input = new { file_path = Path.Combine(Repo, "src/a.txt") } },
        }
            }
        });
        var other = JsonSerializer.Serialize(new { type = "assistant", message = new { content = new object[] { new { type = "tool_use", name = "Edit", input = new { file_path = "/work/src/b.txt" } } } } });
        await File.WriteAllLinesAsync(stream, ["not json", other, edit]);

        var (_, output, _) = await Why("src/a.txt:1", null, "--stream", stream);

        Assert.Contains("stream line 3, Edit: I will make the retry count configurable.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("b.txt", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("a.txt:0")]
    [InlineData("a.txt:x")]
    public async Task A_target_that_is_not_path_and_line_is_a_usage_error(string target)
    {
        Commit("a.txt", "one\n", "fix: x");

        Assert.Equal(2, (await Why(target)).Exit);
    }

    [Fact]
    public async Task A_line_git_cannot_blame_exits_1()
    {
        Commit("a.txt", "one\n", "fix: x");

        var (exit, _, error) = await Why("a.txt:9");

        Assert.Equal(1, exit);
        Assert.Contains("git blame failed", error, StringComparison.Ordinal);
    }
}
