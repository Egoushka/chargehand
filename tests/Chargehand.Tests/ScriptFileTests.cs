using System.Diagnostics;

namespace Chargehand.Tests;

/// <summary>Scripts written and run while other threads fork: the race behind "Text file busy" in tests that exec a script they just wrote.</summary>
public class ScriptFileTests
{
    [Fact]
    public async Task A_script_written_by_the_helper_starts_while_other_threads_fork()
    {
        using var dir = new TempDir();
        using var stop = new CancellationTokenSource();
        var forkers = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                Run("true");
        })).ToArray();
        try
        {
            var failures = new List<string>();
            await Parallel.ForEachAsync(Enumerable.Range(0, 300), new ParallelOptions { MaxDegreeOfParallelism = 4 }, (i, _) =>
            {
                var script = dir.WriteExecutable($"s{i}.sh", "#!/bin/sh\necho ok\n");
                try
                {
                    Assert.Equal("ok", Run(script).Trim());
                }
                catch (System.ComponentModel.Win32Exception e)
                {
                    lock (failures)
                        failures.Add(e.Message);
                }
                return ValueTask.CompletedTask;
            });
            Assert.Empty(failures);
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(forkers);
        }
    }

    private static string Run(string file)
    {
        using var p = Process.Start(new ProcessStartInfo(file) { RedirectStandardOutput = true })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}
