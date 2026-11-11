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
