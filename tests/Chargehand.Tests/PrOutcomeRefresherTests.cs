using Chargehand.Config;
using Chargehand.Driven;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>The pass that reads pull request outcomes runs on a timer for the life of the server: a pass that fails, an HTTP timeout included, is reported and the next tick tries again.</summary>
public class PrOutcomeRefresherTests
{
    [Fact]
    public async Task A_timeout_in_one_pass_does_not_end_the_refresher()
    {
        var store = new TimingOutOnce();
        var clock = new ManualTimeProvider();
        using var rig = new DrivenRunTests.Rig(learning: store, learningSettings: new DrivenLearning(PrCheckMinutes: 5));
        using var refresher = new PrOutcomeRefresher(rig.Run, clock);

        await refresher.StartAsync(default);
        await store.Reads[0].Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromMinutes(5));
        await store.Reads[1].Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(refresher.ExecuteTask!.IsCompleted);
        await refresher.StopAsync(default);
        Assert.True(refresher.ExecuteTask.IsCompletedSuccessfully);
    }

    private sealed class TimingOutOnce : ILearningStore
    {
        private int _reads;

        public TaskCompletionSource[] Reads { get; } = [new(), new()];

        public Task AppendAsync(LearningRecord record, CancellationToken ct) => Task.CompletedTask;

        public Task AppendAsync(PrOutcomeRecord record, CancellationToken ct) => Task.CompletedTask;

        public Task AppendAsync(AutopsyRecord record, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<LearningEntry>> ReadAsync(CancellationToken ct, bool includeBench = false)
        {
            var read = Interlocked.Increment(ref _reads);
            Reads[Math.Min(read, Reads.Length) - 1].TrySetResult();
            if (read == 1)
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException());
            return Task.FromResult<IReadOnlyList<LearningEntry>>([]);
        }
    }
}
