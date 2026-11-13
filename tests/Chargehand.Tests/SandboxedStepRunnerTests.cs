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

        Assert.Equal(0, (await runner.RunAsync(step, default)).Code);

        var spec = sandbox.Seen!;
        Assert.Equal(["pnpm", "install", "--ignore-pnpmfile"], spec.Argv);
        Assert.Equal(root.Path, spec.WorkDirectory);
        Assert.True(spec.Network);
        Assert.Equal(["HTTPS_PROXY"], spec.PassEnv);
        Assert.Equal(cache, spec.Env!["pnpm_config_store_dir"]);
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
    public async Task A_failure_returns_its_code_a_timeout_and_the_output_tail()
    {
        var step = new WarmStep("s", "dotnet", ["restore"], "/w", () => false);
        Assert.Equal(new StepExit(3, false, "boom"), await new SandboxedStepRunner(new FakeSandbox(new SandboxResult(3, "boom", false, TimeSpan.Zero)), false, [], new Dictionary<string, string>(), []).RunAsync(step, default));
        Assert.True((await new SandboxedStepRunner(new FakeSandbox(Ok(0, timedOut: true)), false, [], new Dictionary<string, string>(), []).RunAsync(step, default)).TimedOut);
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
            Assert.Equal(0, (await runner.RunAsync(step, default)).Code);
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

    [Fact]
    public async Task A_scoped_step_gets_its_own_repository_pnpm_store_and_no_other()
    {
        using var root = new TempDir();
        var stores = Path.Combine(root.Path, "pnpm-store");
        var sandbox = new FakeSandbox(Ok());
        var runner = new SandboxedStepRunner(sandbox, true, [], new Dictionary<string, string>(), [], stores);

        await runner.RunAsync(new WarmStep("pnpm-install", "pnpm", ["install"], root.Path, () => false, Scope: "repo-1"), default);

        var own = Path.Combine(stores, "repo-1");
        Assert.Equal(own, sandbox.Seen!.Env!["pnpm_config_store_dir"]);
        Assert.Equal([own], sandbox.Seen.WritablePaths);
        Assert.True(Directory.Exists(own));
    }

    [Fact]
    public async Task Every_step_runs_with_the_pnpm_package_manager_switch_off_whatever_the_caller_passes()
    {
        using var root = new TempDir();
        var sandbox = new FakeSandbox(Ok());
        var env = new Dictionary<string, string> { ["pnpm_config_manage_package_manager_versions"] = "true" };
        var runner = new SandboxedStepRunner(sandbox, true, [], env, []);

        await runner.RunAsync(new WarmStep("pnpm-install", "pnpm", ["install"], root.Path, () => false), default);

        Assert.Equal("false", sandbox.Seen!.Env!["pnpm_config_manage_package_manager_versions"]);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task A_scope_that_is_not_one_plain_name_is_refused(string scope)
    {
        using var root = new TempDir();
        var sandbox = new FakeSandbox(Ok());
        var runner = new SandboxedStepRunner(sandbox, true, [], new Dictionary<string, string>(), [], Path.Combine(root.Path, "pnpm-store"));
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(new WarmStep("pnpm-install", "pnpm", ["install"], root.Path, () => false, Scope: scope), default));
        Assert.Null(sandbox.Seen);
    }

    /// <summary>A hostile checkout's own <c>Directory.Build.props</c> hooks a target into <c>Restore</c> and shells out to write a file
    /// outside the checkout: exactly what <c>dotnet restore</c> running repository code can do, and no <c>dotnet</c> flag turns it off
    /// (docs/specs/2026-11-11-cache-warmer-design.md, decision 9). The <c>Exec</c> command never names the real "outside" path.</summary>
    private static (string Checkout, string Outside) DecoyRestoreTarget(TempDir root)
    {
        var checkout = Directory.CreateDirectory(Path.Combine(root.Path, "checkout")).FullName;
        var outside = Path.Combine(root.Path, "escaped.txt");
        root.Write("checkout/decoy.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        root.Write("checkout/Directory.Build.props", $"""
            <Project>
              <Target Name="Escape" BeforeTargets="Restore">
                <Exec Command="echo escaped &gt; &quot;{outside}&quot;" />
              </Target>
            </Project>
            """);
        return (checkout, outside);
    }

    private static WarmStep DecoyRestoreStep(string checkout) =>
        new("dotnet-restore", "dotnet", ["restore", "./decoy.csproj", "--disable-build-servers"], checkout, () => false);

    [Fact]
    public async Task A_decoy_restore_target_writes_outside_the_checkout_when_nothing_confines_it()
    {
        using var root = new TempDir();
        var (checkout, outside) = DecoyRestoreTarget(root);
        var runner = new SandboxedStepRunner(new NoSandbox(), network: false, [], new Dictionary<string, string>(), []);

        await runner.RunAsync(DecoyRestoreStep(checkout), default);

        Assert.True(File.Exists(outside), "the decoy target should have proven it can write outside an unconfined checkout");
    }

    [BwrapFact]
    public async Task A_decoy_restore_target_cannot_write_outside_the_checkout_under_bubblewrap()
    {
        using var root = new TempDir();
        var (checkout, outside) = DecoyRestoreTarget(root);
        var runner = new SandboxedStepRunner(new BubblewrapSandbox(), network: false, [], new Dictionary<string, string>(), []);

        var exit = await runner.RunAsync(DecoyRestoreStep(checkout), default);

        Assert.NotEqual(0, exit.Code);
        Assert.False(File.Exists(outside));
    }
}

/// <summary>Runs a test only where bubblewrap (<c>bwrap</c>) is actually on PATH: the real confinement a Linux warmer uses, not a fake.</summary>
public sealed class BwrapFactAttribute : FactAttribute
{
    public BwrapFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || !OnPath("bwrap"))
            Skip = "requires bubblewrap (bwrap) on PATH";
    }

    private static bool OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Any(d => File.Exists(Path.Combine(d, name)));
}
