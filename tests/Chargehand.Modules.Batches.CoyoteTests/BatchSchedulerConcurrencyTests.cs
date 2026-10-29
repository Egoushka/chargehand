using Chargehand.Contracts;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Modules.Tasks.Api;
using Microsoft.Coyote;
using Microsoft.Coyote.SystematicTesting;
using Xunit.Abstractions;

namespace Chargehand.Modules.Batches.CoyoteTests;

/// <summary>Systematic tests (Coyote) of <see cref="BatchScheduler"/>: each scenario runs over many task schedules, so a usage report, a cap cancel and a task's end meet in orders a plain test hits one run in a thousand.</summary>
public class BatchSchedulerConcurrencyTests(ITestOutputHelper output)
{
    private const int Iterations = 1000;

    private static ResolvedTask[] Tasks(int n) => [.. Enumerable.Range(1, n).Select(i => new ResolvedTask($"t{i}", null, $"goal {i}"))];

    private void Explore(Func<Task> scenario)
    {
        var config = Configuration.Create().WithTestingIterations(Iterations).WithMaxSchedulingSteps(2000).WithRandomGeneratorSeed(1).WithTelemetryEnabled(false);
        var engine = TestingEngine.Create(config, scenario);
        engine.Run();
        output.WriteLine(engine.TestReport.GetText(config, "Coyote"));
        Assert.True(engine.TestReport.NumOfFoundBugs == 0, engine.GetReport());
    }

    /// <summary>Tasks report usage from their own threads while the scheduler decides on caps and starts: at most MaxParallel run at once, every task ends exactly once, and the batch's tokens are the sum of its tasks'.</summary>
    [Fact]
    public void Usage_reports_racing_task_ends_keep_the_parallel_limit_and_the_token_sum() => Explore(ParallelLimitAndTokenSum);

    internal static async Task ParallelLimitAndTokenSum()
    {
        {
            var running = 0;
            var maxRunning = 0;
            var ended = new List<string>();
            var batch = new BatchScheduler(async (task, progress, ct) =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref maxRunning, now);
                await Task.Run(() => progress.Report(new TaskUsage(100, 0)), CancellationToken.None);
                await Task.Yield();
                Interlocked.Decrement(ref running);
                lock (ended)
                    ended.Add(task.Id);
                return new TaskOutcome(task.Id, TaskState.Completed, $"run-{task.Id}", null, Tokens: 100);
            });
            var outcome = await batch.RunAsync(Tasks(4), new BatchLimits(2, null, null, new TaskLimits(1000, null)), CancellationToken.None);

            Assert.True(maxRunning <= 2, $"{maxRunning} tasks ran at once");
            Assert.Equal(4, outcome.Tasks.Count);
            Assert.Equal(["t1", "t2", "t3", "t4"], outcome.Tasks.Select(t => t.Id));
            Assert.Equal(4, ended.Distinct().Count());
            Assert.Equal(400, outcome.Tokens);
        }
    }

    /// <summary>A task over its own cap is stopped while it runs, whether it then ends on the cancel or on its own: it never counts as completed, every task ends once and the batch's tokens are what the tasks used.</summary>
    [Fact]
    public void A_task_over_its_cap_is_never_completed_whether_it_ends_on_the_cancel_or_by_itself() => Explore(TaskOverItsCap);

    internal static async Task TaskOverItsCap()
    {
        {
            var batch = new BatchScheduler(async (task, progress, ct) =>
            {
                await Task.Run(() => progress.Report(new TaskUsage(1200, 0)), CancellationToken.None);
                await Task.Yield();
                return ct.IsCancellationRequested
                    ? new TaskOutcome(task.Id, TaskState.Cancelled, $"run-{task.Id}", "stopped", Tokens: 1200)
                    : new TaskOutcome(task.Id, TaskState.Completed, $"run-{task.Id}", null, Tokens: 1200);
            });
            var outcome = await batch.RunAsync(Tasks(3), new BatchLimits(3, null, null, new TaskLimits(1000, null)), CancellationToken.None);

            Assert.Equal(["t1", "t2", "t3"], outcome.Tasks.Select(t => t.Id));
            Assert.All(outcome.Tasks, t => Assert.Equal((TaskState.Failed, ErrorCode.CostCapReached), (t.State, t.Error)));
            Assert.Equal(3600, outcome.Tokens);
        }
    }

    [Fact]
    public void A_batch_over_its_cap_cancels_what_runs_while_a_task_ends() => Explore(BatchOverItsCap);

    /// <summary>The batch goes over its cap by more than one task's cap on one task's report while another has just returned: the cancel meets the one that ended and the ones still running, and every task still has exactly one outcome.</summary>
    internal static async Task BatchOverItsCap()
    {
        var batch = new BatchScheduler(async (task, progress, ct) =>
        {
            if (task.Id != "t1")
                await Task.Yield();
            await Task.Run(() => progress.Report(new TaskUsage(task.Id == "t3" ? 3600 : 500, 0)), CancellationToken.None);
            if (task.Id == "t1")
                return new TaskOutcome(task.Id, TaskState.Completed, $"run-{task.Id}", null, Tokens: 500);
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                // agent-checks: allow empty-catch -- a task stopped by the batch ends here
            }
            return new TaskOutcome(task.Id, TaskState.Cancelled, $"run-{task.Id}", "stopped", Tokens: task.Id == "t3" ? 3600 : 500);
        });
        var outcome = await batch.RunAsync(Tasks(3), new BatchLimits(3, 3000, null, new TaskLimits(1000, null)), CancellationToken.None);

        Assert.Equal(["t1", "t2", "t3"], outcome.Tasks.Select(t => t.Id));
        Assert.All(outcome.Tasks, t => Assert.NotEqual(TaskState.NotStarted, t.State));
        Assert.All(outcome.Tasks, t => Assert.True(t.Error is null or ErrorCode.CostCapReached or ErrorCode.Cancelled, $"{t.Id}: {t.Error} {t.Detail}"));
        Assert.All(outcome.Tasks.Skip(1), t => Assert.NotEqual(TaskState.Completed, t.State));
        Assert.Equal(4600, outcome.Tokens);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}
