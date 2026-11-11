using Chargehand.Modules.Research.Api;
using Chargehand.Modules.Warmer.Api;
using Chargehand.Warming;

namespace Chargehand.Tests;

public sealed class SandboxedStepRunnerTests
{
    private sealed class FakeSandbox(SandboxResult result) : ISandbox
    {
        public SandboxSpec? Seen { get; private set; }

        public string Kind => "fake";

        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken ct)
        {
            Seen = spec;
            return Task.FromResult(result);
        }
    }

    private static SandboxResult Ok(int code = 0, bool timedOut = false) => new(code, "", timedOut, TimeSpan.Zero);

    [Fact]
    public async Task A_step_goes_through_the_sandbox_with_the_checkout_the_caches_and_the_profile_network()
    {
        using var root = new TempDir();
        var cache = Path.Combine(root.Path, "store");
        var sandbox = new FakeSandbox(Ok());
        var env = new Dictionary<string, string> { ["pnpm_config_store_dir"] = cache };
        var runner = new SandboxedStepRunner(sandbox, network: true, ["HTTPS_PROXY"], env, [cache]);
        var step = new WarmStep("pnpm-install", "pnpm", ["install", "--ignore-pnpmfile"], root.Path, () => false);

        Assert.Equal(0, await runner.RunAsync(step, default));

        var spec = sandbox.Seen!;
        Assert.Equal(["pnpm", "install", "--ignore-pnpmfile"], spec.Argv);
        Assert.Equal(root.Path, spec.WorkDirectory);
        Assert.True(spec.Network);
        Assert.Equal(["HTTPS_PROXY"], spec.PassEnv);
        Assert.Equal(env, spec.Env);
        Assert.Equal([cache], spec.WritablePaths);
        Assert.Equal(SandboxedStepRunner.Timeout, spec.Timeout);
        Assert.True(Directory.Exists(cache));
    }

    [Fact]
    public async Task The_network_stays_off_when_the_profile_does_not_allow_it()
    {
        var sandbox = new FakeSandbox(Ok());
        await new SandboxedStepRunner(sandbox, false, [], new Dictionary<string, string>(), []).RunAsync(new WarmStep("s", "dotnet", ["restore"], "/w", () => false), default);
        Assert.False(sandbox.Seen!.Network);
    }

    [Fact]
    public async Task A_failure_returns_its_code_and_a_timeout_is_minus_one()
    {
        var step = new WarmStep("s", "dotnet", ["restore"], "/w", () => false);
        Assert.Equal(3, await new SandboxedStepRunner(new FakeSandbox(Ok(3)), false, [], new Dictionary<string, string>(), []).RunAsync(step, default));
        Assert.Equal(-1, await new SandboxedStepRunner(new FakeSandbox(Ok(0, timedOut: true)), false, [], new Dictionary<string, string>(), []).RunAsync(step, default));
    }

    [Fact]
    public async Task The_environment_reaching_the_child_is_the_sandbox_allowlist_plus_the_named_ones()
    {
        Environment.SetEnvironmentVariable("CHARGEHAND_WARMER_TEST_SECRET", "x");
        try
        {
            using var root = new TempDir();
            var runner = new SandboxedStepRunner(new NoSandbox(), false, [], new Dictionary<string, string> { ["EXTRA"] = "1" }, []);
            var step = new WarmStep("env", "/bin/sh", ["-c", "test -z \"$CHARGEHAND_WARMER_TEST_SECRET\" && test \"$EXTRA\" = 1"], root.Path, () => false);
            Assert.Equal(0, await runner.RunAsync(step, default));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHARGEHAND_WARMER_TEST_SECRET", null);
        }
    }

    [Fact]
    public async Task A_staging_step_writes_nuget_packages_to_its_staging_folder_and_never_to_the_shared_cache()
    {
        using var root = new TempDir();
        var store = Path.Combine(root.Path, "store");
        var sharedNuget = Path.Combine(root.Path, "nuget-packages");
        var staging = Path.Combine(root.Path, "staging", "run1");
        var sandbox = new FakeSandbox(Ok());
        var env = new Dictionary<string, string> { ["pnpm_config_store_dir"] = store };
        var runner = new SandboxedStepRunner(sandbox, true, [], env, [store]);
        var step = new WarmStep("dotnet-restore", "dotnet", ["restore"], root.Path, () => false, Stages: true, Staging: staging);

        await runner.RunAsync(step, default);

        var spec = sandbox.Seen!;
        Assert.Equal(staging, spec.Env!["NUGET_PACKAGES"]);
        Assert.Contains(staging, spec.WritablePaths!);
        Assert.DoesNotContain(sharedNuget, spec.WritablePaths!);
        Assert.DoesNotContain(spec.WritablePaths!, p => p.Contains("nuget-packages", StringComparison.Ordinal));
        Assert.False(env.ContainsKey("NUGET_PACKAGES"));
    }
}
