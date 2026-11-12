using Chargehand.Modules.Warmer.Api;

namespace Chargehand.Modules.Warmer.Tests;

public sealed class WarmPlanTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "chargehand-warm-" + Guid.NewGuid().ToString("N"));

    public WarmPlanTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, true);

    private void Touch(string relative, string text = "")
    {
        var path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private IReadOnlyList<string> Names() => [.. WarmPlan.For(dir).Select(s => s.Name)];

    [Fact]
    public void An_empty_checkout_plans_nothing() => Assert.Empty(WarmPlan.For(dir));

    [Fact]
    public void A_solution_plans_one_dotnet_restore()
    {
        Touch("Chargehand.slnx");
        Touch("src/App/App.csproj");
        var step = Assert.Single(WarmPlan.For(dir));
        Assert.Equal("dotnet-restore", step.Name);
        Assert.Equal("dotnet", step.File);
        Assert.Equal(["restore", "./Chargehand.slnx", "--disable-build-servers"], step.Args);
        Assert.Equal(dir, step.WorkDir);
    }

    [Fact]
    public void A_project_without_a_solution_restores_the_project()
    {
        Touch("App.csproj");
        Assert.Equal(["restore", "./App.csproj", "--disable-build-servers"], Assert.Single(WarmPlan.For(dir)).Args);
    }

    [Fact]
    public void A_pnpm_lockfile_plans_the_cached_install()
    {
        Touch("pnpm-lock.yaml");
        var step = Assert.Single(WarmPlan.For(dir));
        Assert.Equal("pnpm-install", step.Name);
        Assert.Equal("pnpm", step.File);
        Assert.Equal(["install", "--prefer-offline", "--frozen-lockfile", "--ignore-scripts", "--ignore-pnpmfile"], step.Args);
    }

    [Fact]
    public void An_npm_lockfile_in_a_subdirectory_plans_npm_ci_there()
    {
        Touch("web/package-lock.json");
        var step = Assert.Single(WarmPlan.For(dir));
        Assert.Equal("npm-ci", step.Name);
        Assert.Equal(["ci", "--prefer-offline", "--ignore-scripts"], step.Args);
        Assert.Equal(Path.Combine(dir, "web"), step.WorkDir);
    }

    [Fact]
    public void Dependency_folders_are_not_searched()
    {
        Touch("node_modules/x/pnpm-lock.yaml");
        Touch(".git/pnpm-lock.yaml");
        Assert.Empty(WarmPlan.For(dir));
    }

    [Theory]
    [InlineData(".pnpmfile.mjs", "")]
    [InlineData(".npmrc", "registry=https://example.invalid\npnpmfile=./hooks.cjs\n")]
    [InlineData(".npmrc", "  global-pnpmfile = /tmp/x.cjs\n")]
    [InlineData("pnpm-workspace.yaml", "packages:\n  - a\npnpmfile: hooks.cjs\n")]
    [InlineData("pnpm-workspace.yaml", "globalPnpmfile: x.cjs\n")]
    [InlineData("pnpm-workspace.yaml", "configDependencies:\n  x: 1.0.0\n")]
    [InlineData("pnpm-workspace.yaml", "{packages: [a], pnpmfile: hooks.cjs}\n")]
    [InlineData("pnpm-workspace.yaml", "? configDependencies\n: {x: 1.0.0}\n")]
    [InlineData("pnpm-workspace.yaml", "config-dependencies:\n  x: 1.0.0\n")]
    [InlineData("pnpm-workspace.yaml", "\uFEFFconfigDependencies:\n  x: 1.0.0\n")]
    public void A_pnpmfile_named_anywhere_pnpm_reads_stops_the_folder_being_warmed(string file, string text)
    {
        Touch("pnpm-lock.yaml");
        Touch(file, text);
        Assert.Empty(WarmPlan.For(dir));
    }

    [Fact]
    public void An_ordinary_npmrc_and_workspace_still_warm()
    {
        Touch("pnpm-lock.yaml");
        Touch(".npmrc", "registry=https://example.invalid\nignore-pnpmfile=true\n");
        Touch("pnpm-workspace.yaml", "packages:\n  - a\n");
        Assert.Equal(["pnpm-install"], Names());
    }

    // pnpm walks up to the workspace root and loads its settings, so a hooked root reaches every folder below it; npm reads none of them.
    [Theory]
    [InlineData(".pnpmfile.cjs", "")]
    [InlineData(".npmrc", "pnpmfile=./hooks.cjs\n")]
    [InlineData("pnpm-workspace.yaml", "packages:\n  - web\nconfigDependencies:\n  x: 1.0.0\n")]
    public void A_hooked_checkout_root_stops_every_pnpm_folder_in_it(string file, string text)
    {
        Touch(file, text);
        Touch("web/pnpm-lock.yaml");
        Touch("api/package-lock.json");
        Assert.Equal(["npm-ci"], Names());
    }

    [Fact]
    public void A_pnpmfile_stops_the_folder_being_warmed()
    {
        Touch("pnpm-lock.yaml");
        Touch(".pnpmfile.cjs");
        Assert.Empty(WarmPlan.For(dir));
    }

    [Fact]
    public void A_symlinked_folder_is_not_searched()
    {
        var outside = Directory.CreateTempSubdirectory("chargehand-warmer-out-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "pnpm-lock.yaml"), "");
            Directory.CreateSymbolicLink(Path.Combine(dir, "web"), outside);
            Assert.Empty(WarmPlan.For(dir));
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void A_playwright_dependency_adds_the_browsers_check()
    {
        Touch("package-lock.json");
        Touch("package.json", """{"devDependencies":{"@playwright/test":"^1.50"}}""");
        Assert.Equal(["npm-ci", "playwright-browsers"], Names());
        Assert.Null(WarmPlan.For(dir)[^1].File);
    }

    [Fact]
    public void A_step_is_done_when_its_result_exists()
    {
        Touch("pnpm-lock.yaml");
        var step = Assert.Single(WarmPlan.For(dir));
        Assert.False(step.Done());
        Touch("node_modules/.modules.yaml");
        Assert.True(step.Done());
    }

    [Fact]
    public void Dotnet_restore_is_done_when_a_project_has_its_assets()
    {
        Touch("A.slnx");
        var step = Assert.Single(WarmPlan.For(dir));
        Assert.False(step.Done());
        Touch("src/Modules/X/obj/project.assets.json");
        Assert.True(step.Done());
    }
}
