using Chargehand.Modules.Gateway.Api;
using Chargehand.Tests;

namespace Chargehand.Modules.Gateway.Tests;

/// <summary>chargehand init: what a repository's marker files say about its ecosystems, test command and package hosts.</summary>
public class RepositoryDetectorTests
{
    private static RepositoryScan ScanOf(params (string Path, string Content)[] files)
    {
        using var dir = new TempDir();
        foreach (var (path, content) in files)
            dir.Write(path, content);
        return RepositoryDetector.Scan(dir.Path);
    }

    private static IReadOnlyList<string> Names(RepositoryScan scan) => [.. scan.Ecosystems.Select(e => e.Name)];

    [Theory]
    [InlineData("App.sln")]
    [InlineData("App.slnx")]
    [InlineData("App.csproj")]
    public void A_dotnet_repository_is_found_by_its_solution_or_project_file(string marker)
    {
        var scan = ScanOf((marker, ""));

        Assert.Equal(["dotnet"], Names(scan));
        Assert.Equal([marker], scan.Ecosystems[0].Markers);
        Assert.Equal(["api.nuget.org", "globalcdn.nuget.org"], scan.Hosts);
        Assert.Equal(["dotnet", "test", "--nologo"], scan.BestVerify!.Command);
    }

    [Theory]
    [InlineData("package-lock.json", "npm ci && npm test", "registry.npmjs.org")]
    [InlineData("pnpm-lock.yaml", "corepack enable && pnpm install --frozen-lockfile && pnpm test", "registry.npmjs.org")]
    [InlineData("yarn.lock", "corepack enable && yarn install --frozen-lockfile && yarn test", "registry.yarnpkg.com")]
    [InlineData("bun.lockb", "bun install --frozen-lockfile && bun run test", "registry.npmjs.org")]
    public void A_node_repository_installs_with_its_lock_file_kind_before_it_tests(string lockFile, string command, string host)
    {
        var scan = ScanOf(("package.json", """{"scripts":{"test":"vitest run"}}"""), (lockFile, ""));

        Assert.Equal(["node"], Names(scan));
        Assert.Equal(["package.json", lockFile], scan.Ecosystems[0].Markers);
        Assert.Contains(host, scan.Hosts);
        Assert.Equal(["sh", "-c", command], scan.BestVerify!.Command);
    }

    [Fact]
    public void A_node_repository_without_a_lock_file_installs_with_npm()
    {
        var scan = ScanOf(("package.json", """{"scripts":{"test":"jest"}}"""));

        Assert.Equal(["sh", "-c", "npm install && npm test"], scan.BestVerify!.Command);
    }

    [Theory]
    [InlineData("""{"name":"x"}""")]
    [InlineData("""{"scripts":{"test":"echo \"Error: no test specified\" && exit 1"}}""")]
    [InlineData("not json")]
    public void A_node_repository_with_no_real_test_script_proposes_no_command_but_still_needs_the_registry(string packageJson)
    {
        var scan = ScanOf(("package.json", packageJson));

        Assert.Equal(["node"], Names(scan));
        Assert.Null(scan.BestVerify);
        Assert.Equal(["registry.npmjs.org"], scan.Hosts);
    }

    [Fact]
    public void A_python_repository_with_requirements_installs_them_and_runs_pytest()
    {
        var scan = ScanOf(("requirements.txt", "pytest\n"), ("requirements-dev.txt", "ruff\n"));

        Assert.Equal(["python"], Names(scan));
        Assert.Equal(["requirements-dev.txt", "requirements.txt"], scan.Ecosystems[0].Markers);
        Assert.Equal(["pypi.org", "files.pythonhosted.org"], scan.Hosts);
        Assert.Equal(["sh", "-c", "pip install -r requirements-dev.txt pytest && python3 -m pytest -q"], scan.BestVerify!.Command);
    }

    [Fact]
    public void A_python_repository_with_only_pyproject_installs_the_project()
    {
        var scan = ScanOf(("pyproject.toml", "[project]\nname = \"x\"\n"));

        Assert.Equal(["python"], Names(scan));
        Assert.Equal(["sh", "-c", "pip install -e . pytest && python3 -m pytest -q"], scan.BestVerify!.Command);
    }

    [Fact]
    public void A_go_repository_is_found_by_go_mod()
    {
        var scan = ScanOf(("go.mod", "module x\n"));

        Assert.Equal(["go"], Names(scan));
        Assert.Equal(["proxy.golang.org", "sum.golang.org"], scan.Hosts);
        Assert.Equal(["go", "test", "./..."], scan.BestVerify!.Command);
    }

    [Fact]
    public void A_rust_repository_is_found_by_cargo_toml()
    {
        var scan = ScanOf(("Cargo.toml", "[package]\n"));

        Assert.Equal(["rust"], Names(scan));
        Assert.Equal(["crates.io", "index.crates.io", "static.crates.io"], scan.Hosts);
        Assert.Equal(["cargo", "test"], scan.BestVerify!.Command);
    }

    [Fact]
    public void A_gradle_repository_with_a_wrapper_runs_the_wrapper()
    {
        var scan = ScanOf(("build.gradle.kts", ""), ("gradlew", ""));

        Assert.Equal(["gradle"], Names(scan));
        Assert.Equal(["build.gradle.kts", "gradlew"], scan.Ecosystems[0].Markers);
        Assert.Contains("services.gradle.org", scan.Hosts);
        Assert.Contains("repo.maven.apache.org", scan.Hosts);
        Assert.Equal(["./gradlew", "check", "--no-daemon"], scan.BestVerify!.Command);
    }

    [Fact]
    public void A_gradle_repository_without_a_wrapper_runs_gradle()
    {
        var scan = ScanOf(("build.gradle", ""));

        Assert.Equal(["gradle", "check", "--no-daemon"], scan.BestVerify!.Command);
    }

    [Fact]
    public void A_maven_repository_is_found_by_pom_xml()
    {
        var scan = ScanOf(("pom.xml", "<project/>"));

        Assert.Equal(["maven"], Names(scan));
        Assert.Equal(["repo.maven.apache.org", "repo1.maven.org"], scan.Hosts);
        Assert.Equal(["mvn", "-B", "test"], scan.BestVerify!.Command);
    }

    [Fact]
    public void A_makefile_with_a_test_target_is_a_test_command_and_needs_no_package_host()
    {
        var scan = ScanOf(("Makefile", "build:\n\tcc x.c\n\ntest: build\n\t./a.out\n"));

        Assert.Empty(scan.Ecosystems);
        Assert.Empty(scan.Hosts);
        Assert.Equal(["make", "test"], scan.BestVerify!.Command);
    }

    [Fact]
    public void A_makefile_without_a_test_target_is_ignored()
    {
        var scan = ScanOf(("Makefile", "build:\n\tcc x.c\n# test: not a target\n"));

        Assert.Null(scan.BestVerify);
    }

    [Fact]
    public void The_test_command_follows_the_fixed_priority_order()
    {
        var scan = ScanOf(("scripts/check.sh", ""), ("scripts/test-all.sh", ""), ("scripts/test.py", ""), ("Makefile", "test:\n\ttrue\n"), ("App.sln", ""), ("go.mod", "module x\n"));

        Assert.Equal(
            ["sh scripts/check.sh", "sh scripts/test-all.sh", "./scripts/test.py", "make test", "dotnet test --nologo", "go test ./..."],
            scan.Verify.Select(v => string.Join(' ', v.Command)));
        Assert.Equal("scripts/check.sh", scan.BestVerify!.Source);
    }

    [Fact]
    public void Ecosystems_come_in_a_fixed_order_and_shared_hosts_are_listed_once()
    {
        var scan = ScanOf(("pom.xml", ""), ("build.gradle", ""), ("Cargo.toml", ""), ("package.json", "{}"), ("App.sln", ""));

        Assert.Equal(["dotnet", "node", "rust", "gradle", "maven"], Names(scan));
        Assert.Equal(scan.Hosts.Distinct(), scan.Hosts);
        Assert.Equal(1, scan.Hosts.Count(h => h == "repo.maven.apache.org"));
    }

    [Fact]
    public void A_directory_with_no_marker_has_nothing_to_report_and_a_missing_one_does_not_throw()
    {
        using var dir = new TempDir();
        dir.Write("README.md", "hi");

        Assert.Empty(RepositoryDetector.Scan(dir.Path).Ecosystems);
        Assert.Empty(RepositoryDetector.Scan(Path.Combine(dir.Path, "missing")).Ecosystems);
    }

    [Fact]
    public void Only_the_top_level_is_read()
    {
        var scan = ScanOf(("sub/go.mod", "module x\n"), ("sub/App.sln", ""));

        Assert.Empty(scan.Ecosystems);
    }

    [Fact]
    public void Hosts_the_allowlist_already_covers_are_left_out()
    {
        var needed = new[] { "api.nuget.org", "globalcdn.nuget.org", "registry.npmjs.org", "static.crates.io" };

        Assert.Equal(["globalcdn.nuget.org", "static.crates.io"], RepositoryDetector.Uncovered(needed, ["api.nuget.org", "registry.npmjs.org", "api.anthropic.com"]));
        Assert.Equal(["registry.npmjs.org"], RepositoryDetector.Uncovered(needed, ["*.nuget.org", "*.crates.io"]));
    }

    [Fact]
    public void An_empty_or_invalid_allowlist_covers_nothing()
    {
        Assert.Equal(["pypi.org"], RepositoryDetector.Uncovered(["pypi.org"], []));
        Assert.Equal(["pypi.org"], RepositoryDetector.Uncovered(["pypi.org"], ["10.0.0.1", "not a host"]));
    }
}
