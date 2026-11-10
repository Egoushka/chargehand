using Chargehand.Modules.Oversight.Api;
using Chargehand.Tests;

namespace Chargehand.Modules.Oversight.Tests;

/// <summary>The pass that reads pull request outcomes runs on a timer for the life of the host: a pass that fails, an HTTP timeout included, is reported and the next tick tries again.</summary>
public class PrOutcomeRefresherTests
{
    [Fact]
    public async Task A_timeout_in_one_pass_does_not_end_the_refresher()
    {
        var reads = new TaskCompletionSource[] { new(), new() };
        var read = 0;
        var clock = new ManualTimeProvider();
        using var refresher = new PrOutcomeRefresher(TimeSpan.FromMinutes(5), _ =>
        {
            var n = Interlocked.Increment(ref read);
            reads[Math.Min(n, reads.Length) - 1].TrySetResult();
            if (n == 1)
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException());
            return Task.FromResult(0);
        }, clock);

        await refresher.StartAsync(default);
        await reads[0].Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromMinutes(5));
        await reads[1].Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(refresher.ExecuteTask!.IsCompleted);
        await refresher.StopAsync(default);
        Assert.True(refresher.ExecuteTask.IsCompletedSuccessfully);
    }
}
