using Chargehand.Modules.Watch.Api;

namespace Chargehand.Tests;

/// <summary><c>chargehand watch</c> against a real server: the run's events end to end.</summary>
public class WatchCliServerTests
{
    /// <summary>Output that says when a given line was written.</summary>
    private sealed class Lines(string awaited) : StringWriter
    {
        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value == awaited)
                Written.TrySetResult();
        }
    }

    [Fact]
    public async Task Watch_follows_a_real_servers_run_from_accepted_to_its_result_and_reads_a_finished_one_again()
    {
        var runtime = new ScriptedRuntime(Runs.DraftReply) { Hold = new() };
        await using var s = await TestServer.StartAsync(runtime);
        var id = (await s.PostAsync(Runs.DraftRequest(), waitSeconds: 0)).Headers.Location!.OriginalString.Split('/')[^1];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // The run is released only once watch follows it: a run that finished first is read from the log as its result alone.
        var output = new Lines("accepted");
        var watching = WatchCli.WatchAsync(s.Http, id, output, TextWriter.Null, timeout.Token);
        await output.Written.Task.WaitAsync(timeout.Token);
        runtime.Hold.SetResult();
        Assert.Equal(0, await watching);
        var lines = output.ToString().TrimEnd().Split('\n');
        Assert.Equal("accepted", lines[0]);
        Assert.StartsWith("run_finished: completed: ", lines[^1]);

        var again = new StringWriter();
        Assert.Equal(0, await WatchCli.WatchAsync(s.Http, id, again, TextWriter.Null, timeout.Token));
        Assert.StartsWith("run_finished: completed: ", again.ToString());
    }
}
