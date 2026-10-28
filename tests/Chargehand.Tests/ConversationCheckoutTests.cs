using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Ledger.Api;
using Chargehand.RunLog;

namespace Chargehand.Tests;

/// <summary>The checkout a conversation's agent reads (ADR 0086): the worker's clone at the pinned commit, with the extra refusals an agent that has a shell needs.</summary>
public class ConversationCheckoutTests
{
    private static Orchestrator Orchestrator(string root) => Runs.Orchestrator(new ScriptedRuntime(Runs.WorkerReply), root, new JsonlRunLog(Path.Combine(root, "log.jsonl")));

    [Fact]
    public async Task A_conversation_reads_a_clone_of_the_commit_not_the_callers_working_tree()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        File.WriteAllText(Path.Combine(repo.Path, "README.md"), "edited, not committed\n");

        var directory = await Orchestrator(root.Path).CheckoutForConversationAsync(repo, null, CancellationToken.None);

        Assert.NotEqual(Path.GetFullPath(repo.Path), directory);
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(directory, "README.md")));
        Assert.Equal(repo.Commit, Runs.Git(directory, "rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task A_tracked_file_the_preset_denies_reading_refuses_the_conversation()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        File.WriteAllText(Path.Combine(repo.Path, "x.env"), "TOKEN=[masked:assignment]");
        repo = Runs.Commit(repo, "x.env");

        var e = await Assert.ThrowsAsync<ChargehandException>(() => Orchestrator(root.Path).CheckoutForConversationAsync(repo, null, CancellationToken.None));

        Assert.Equal(ErrorCode.CheckoutHasSecrets, e.Code);
        Assert.Contains("x.env", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_preset_that_writes_is_refused_because_a_conversation_only_reads()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);

        var e = await Assert.ThrowsAsync<ChargehandException>(() => Orchestrator(root.Path).CheckoutForConversationAsync(repo, "code", CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.Contains("writes", e.Message, StringComparison.Ordinal);
    }
}
