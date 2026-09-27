using Chargehand.Contracts;
using Chargehand.RunLog;

namespace Chargehand.Tests;

/// <summary>ADR 0006: OpenCode's grep reaches any file it is pointed at, so a checkout may hold no file its preset denies reading.</summary>
public class CheckoutTests
{
    [Fact]
    public async Task A_checkout_with_a_denied_file_fails_before_any_worker_session()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        File.WriteAllText(Path.Combine(repo.Path, ".gitignore"), ".env*\n");
        Directory.CreateDirectory(Path.Combine(repo.Path, "sub"));
        foreach (var name in new[] { "sub/.env", "x.env.local", ".env.example" })
            File.WriteAllText(Path.Combine(repo.Path, name), "TOKEN=placeholder\n");
        var runtime = new ScriptedRuntime(Runs.WorkerReply);

        var r = await Runs.Orchestrator(runtime, root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")))
            .RunAsync(Runs.CheapRequest(repo), CancellationToken.None);

        Assert.Equal(ResultStatus.Failed, r.Status);
        Assert.Contains("sub/.env", r.Summary, StringComparison.Ordinal);
        Assert.Contains("x.env.local", r.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(".env.example", r.Summary, StringComparison.Ordinal);
        Assert.Empty(runtime.Created);
    }

    [Fact]
    public async Task A_checkout_with_only_allowed_files_runs()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        File.WriteAllText(Path.Combine(repo.Path, ".env.example"), "TOKEN=\n");
        var runtime = new ScriptedRuntime(Runs.WorkerReply);

        var r = await Runs.Orchestrator(runtime, root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")))
            .RunAsync(Runs.CheapRequest(repo), CancellationToken.None);

        Assert.NotEqual(ResultStatus.Failed, r.Status);
        Assert.Single(runtime.Created);
    }
}
