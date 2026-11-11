using Chargehand.Modules.Warmer.Api;

namespace Chargehand.Modules.Warmer.Tests;

public sealed class StepRunnerTests
{
    private static readonly Dictionary<string, string> Daemon = new()
    {
        ["PATH"] = "/usr/bin",
        ["HOME"] = "/home/agent",
        ["GITHUB_TOKEN"] = "secret",
        ["CHARGEHAND_ADMIN_KEY"] = "secret",
        ["NUGET_PACKAGES"] = "/cache/nuget",
    };

    [Fact]
    public void The_child_gets_the_allowed_variables_and_the_extras_only()
    {
        var env = ProcessStepRunner.ChildEnvironment(new Dictionary<string, string> { ["pnpm_config_store_dir"] = "/s" }, k => Daemon.GetValueOrDefault(k));
        Assert.Equal(["HOME", "NUGET_PACKAGES", "PATH", "pnpm_config_store_dir"], env.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void An_extra_overrides_an_inherited_variable()
    {
        var env = ProcessStepRunner.ChildEnvironment(new Dictionary<string, string> { ["HOME"] = "/other" }, k => Daemon.GetValueOrDefault(k));
        Assert.Equal("/other", env["HOME"]);
    }

    [Fact]
    public async Task The_runner_passes_only_that_environment_to_the_program()
    {
        Environment.SetEnvironmentVariable("CHARGEHAND_WARMER_TEST_SECRET", "x");
        try
        {
            var dir = Directory.CreateTempSubdirectory("chargehand-runner-").FullName;
            try
            {
                var step = new WarmStep("env", "/bin/sh", ["-c", "test -z \"$CHARGEHAND_WARMER_TEST_SECRET\" && test \"$EXTRA\" = 1"], dir, () => false);
                Assert.Equal(0, await new ProcessStepRunner(new Dictionary<string, string> { ["EXTRA"] = "1" }).RunAsync(step, default));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHARGEHAND_WARMER_TEST_SECRET", null);
        }
    }

    [Fact]
    public async Task Cancelling_stops_the_program()
    {
        var dir = Directory.CreateTempSubdirectory("chargehand-runner-").FullName;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var step = new WarmStep("sleep", "/bin/sleep", ["30"], dir, () => false);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessStepRunner(new Dictionary<string, string>()).RunAsync(step, cts.Token));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
