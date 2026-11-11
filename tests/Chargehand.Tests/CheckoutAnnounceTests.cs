using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Events.Api;
using Chargehand.Modules.Warmer.Api;

namespace Chargehand.Tests;

/// <summary>ADR 0090: a new checkout is announced once, so the cache warmer restores it ahead of the agent; a reused one is not.</summary>
public class CheckoutAnnounceTests
{
    [Fact]
    public async Task A_new_checkout_is_announced_once_and_a_reused_one_not_again()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        using var log = SqliteEventLog.Open(Path.Combine(root.Path, "events.db"));
        var orchestrator = new Orchestrator(Runs.Profile(root.Path), new ScriptedRuntime(Runs.WorkerReply), "2.0.16", Repo.Root,
            new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), new Dictionary<string, int>(), events: log);

        await orchestrator.CheckoutForConversationAsync(repo, null, CancellationToken.None);
        await orchestrator.CheckoutForConversationAsync(repo, null, CancellationToken.None);

        var announced = await log.ReadAsync(new EventFilter(Type: CacheWarmer.CreatedType), CancellationToken.None);
        Assert.Single(announced);
    }
}
