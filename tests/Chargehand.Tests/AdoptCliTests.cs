using System.Text.Json;
using Chargehand.Modules.Gateway.Api;
using Chargehand.Onboarding;

namespace Chargehand.Tests;

/// <summary>chargehand adopt: the probes it picks from the detector, how it reads their results, the profile entry it proposes or writes, and what it reports missing. The container
/// engine and git are a fake that answers by command.</summary>
public class AdoptCliTests
{
    private sealed class FakeHost(Func<string, IReadOnlyList<string>, (int? Exit, string Output)?> answer) : IAdoptHost
    {
        public List<string> Calls { get; } = [];

        public Task<(int? ExitCode, string Output)> RunAsync(string program, IReadOnlyList<string> args, string? workingDirectory, TimeSpan limit, CancellationToken ct)
        {
            var line = $"{program} {string.Join(' ', args)}";
            Calls.Add(line);
            return Task.FromResult<(int?, string)>(answer(program, args) ?? (0, ""));
        }
    }

    private static (int? Exit, string Output)? Docker(IReadOnlyList<string> args, Func<string, (int? Exit, string Output)?> probe) => args switch
    {
        ["version", ..] => (0, "27.3.1"),
        ["exec", "-w", _, _, ..] => probe(string.Join(" ", args.Skip(4))),
        _ => null,
    };

    private static async Task<(int Code, string Output, FakeHost Host)> Run(TempDir dir, FakeHost host, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await AdoptCli.RunAsync(args, Path.Combine(dir.Path, "profile.json"), host, _ => (null, "no server"), output, error, CancellationToken.None);
        return (code, output + error.ToString(), host);
    }

    private static TempDir DotNetRepo(bool ci = true, string profile = """{"contract_version":"profile/v1","driven":{"images":["session@sha256:abc"]}}""")
    {
        var dir = new TempDir();
        dir.Write("repo/App.sln", "");
        dir.Write("repo/scripts/check.sh", "#!/bin/sh\n");
        if (ci)
            dir.Write("repo/.github/workflows/ci.yml", "on: push\n");
        dir.Write("profile.json", profile);
        return dir;
    }

    [Fact]
    public async Task It_runs_build_test_and_lint_in_the_session_image_and_proposes_the_test_command_that_passed()
    {
        using var dir = DotNetRepo();
        var host = new FakeHost((program, args) => program == "docker" ? Docker(args, command => command == "sh scripts/check.sh" ? (1, "error CS1002\nFailed: 2") : null) : null);

        var (code, output, _) = await Run(dir, host, "o/r", "--path", Path.Combine(dir.Path, "repo"));

        Assert.Equal(0, code);
        Assert.Contains("ok   build: dotnet build", output, StringComparison.Ordinal);
        Assert.Contains("fail test: sh scripts/check.sh: exit 1", output, StringComparison.Ordinal);
        Assert.Contains("Failed: 2", output, StringComparison.Ordinal);
        Assert.Contains("ok   test: dotnet test --nologo", output, StringComparison.Ordinal);
        Assert.Contains("ok   lint: dotnet format --verify-no-changes", output, StringComparison.Ordinal);
        Assert.Contains("""profile: add "driven": { "services": { "verify": { "o/r": ["dotnet","test","--nologo"] } } } (or pass --write)""", output, StringComparison.Ordinal);
        Assert.Contains("missing: nothing", output, StringComparison.Ordinal);
        Assert.Contains(host.Calls, c => c.StartsWith("docker run -d --rm --name chargehand-adopt-", StringComparison.Ordinal) && c.Contains(":/src:ro --entrypoint sleep session@sha256:abc 3600", StringComparison.Ordinal));
        Assert.StartsWith("docker rm -f chargehand-adopt-", host.Calls[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_write_it_sets_the_verify_entry_and_keeps_the_rest_of_the_profile()
    {
        using var dir = DotNetRepo();
        var host = new FakeHost((program, args) => program == "docker" ? Docker(args, _ => null) : null);

        var (code, output, _) = await Run(dir, host, "o/r", "--path", Path.Combine(dir.Path, "repo"), "--write");

        Assert.Equal(0, code);
        Assert.Contains("ok   profile: driven.services.verify[\"o/r\"] = sh scripts/check.sh", output, StringComparison.Ordinal);
        var profile = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.Path, "profile.json"))).RootElement;
        Assert.Equal("session@sha256:abc", profile.GetProperty("driven").GetProperty("images")[0].GetString());
        Assert.Equal(["sh", "scripts/check.sh"], profile.GetProperty("driven").GetProperty("services").GetProperty("verify").GetProperty("o/r").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task It_reports_a_missing_toolchain_ci_workflow_and_no_passing_test_and_exits_1()
    {
        using var dir = DotNetRepo(ci: false);
        var host = new FakeHost((program, args) => program == "docker" ? Docker(args, _ => (127, "sh: dotnet: not found")) : null);

        var (code, output, _) = await Run(dir, host, "o/r", "--path", Path.Combine(dir.Path, "repo"));

        Assert.Equal(1, code);
        Assert.Contains("fail ci: no CI configuration", output, StringComparison.Ordinal);
        Assert.Contains("fail build: dotnet build: dotnet is not in the session image", output, StringComparison.Ordinal);
        Assert.Contains("skip profile: no test command passed", output, StringComparison.Ordinal);
        Assert.Contains("missing: a CI workflow that builds and tests every pull request; toolchain: dotnet in the session image", output, StringComparison.Ordinal);
        Assert.Contains("a test command that passes in the session image", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_container_engine_or_an_image_it_says_so_and_runs_nothing()
    {
        using var dir = DotNetRepo(profile: """{"contract_version":"profile/v1"}""");
        var host = new FakeHost((_, _) => (1, "Cannot connect to the Docker daemon"));

        var (code, output, _) = await Run(dir, host, "o/r", "--path", Path.Combine(dir.Path, "repo"));
        Assert.Equal(1, code);
        Assert.Contains("fail image: driven.images is empty", output, StringComparison.Ordinal);

        (code, output, var used) = await Run(dir, new FakeHost((_, _) => (1, "Cannot connect to the Docker daemon")), "o/r", "--path", Path.Combine(dir.Path, "repo"), "--image", "img");
        Assert.Equal(1, code);
        Assert.Contains("fail docker: the container engine does not answer", output, StringComparison.Ordinal);
        Assert.DoesNotContain(used.Calls, c => c.StartsWith("docker run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_repository_with_no_checkout_under_the_roots_gets_the_clone_command()
    {
        using var dir = new TempDir();
        dir.Write("profile.json", $$"""{"contract_version":"profile/v1","repository_roots":[{{JsonSerializer.Serialize(dir.Path)}}]}""");

        var (code, output, _) = await Run(dir, new FakeHost((_, _) => null), "o/r");

        Assert.Equal(1, code);
        Assert.Contains("fail checkout: none under repository_roots has origin o/r", output, StringComparison.Ordinal);
        Assert.Contains("git clone https://github.com/o/r.git", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("owner-only")]
    [InlineData("o/r", "--timeout", "0")]
    [InlineData("o/r", "--bogus")]
    [InlineData("o/r", "--path")]
    public async Task A_bad_argument_is_a_usage_error(params string[] args)
    {
        using var dir = new TempDir();
        Assert.Equal(2, (await Run(dir, new FakeHost((_, _) => null), args)).Code);
    }

    [Fact]
    public void Probes_follow_the_ecosystems_and_the_node_scripts()
    {
        using var dir = new TempDir();
        dir.Write("package.json", """{"scripts":{"build":"vite build","test":"vitest"}}""");
        dir.Write("package-lock.json", "{}");
        dir.Write("go.mod", "module x\n");

        var probes = AdoptProbes.For(RepositoryDetector.Scan(dir.Path), dir.Path);

        Assert.Equal(["setup npm ci", "build npm run build", "build go build ./...", "lint go vet ./..."], probes.Where(p => p.Kind != "test").Select(p => $"{p.Kind} {p.Line}"));
        Assert.Contains(probes, p => p.Kind == "test");
    }

    [Theory]
    [InlineData(0, "", AdoptProbes.Passed)]
    [InlineData(127, "", AdoptProbes.MissingTool)]
    [InlineData(1, "sh: 1: ruff: not found", AdoptProbes.MissingTool)]
    [InlineData(2, "scripts/check.sh: line 4: dotnet: command not found", AdoptProbes.MissingTool)]
    [InlineData(1, "2 tests failed", AdoptProbes.Failed)]
    [InlineData(null, "", AdoptProbes.TimedOut)]
    public void A_result_is_read_from_the_exit_code_and_a_shells_not_found(int? exit, string output, string expected) =>
        Assert.Equal(expected, AdoptProbes.Classify(new AdoptProbe("lint", ["ruff", "check", "."], "ruff"), exit, output, TimeSpan.Zero).Outcome);

    [Fact]
    public void The_missing_tool_is_the_one_the_shell_names()
    {
        var result = AdoptProbes.Classify(new AdoptProbe("test", ["sh", "scripts/check.sh"], "scripts/check.sh"), 127, "scripts/check.sh: 3: dotnet: not found", TimeSpan.Zero);
        Assert.Equal((AdoptProbes.MissingTool, "dotnet"), (result.Outcome, result.Tool));
    }

    [Fact]
    public void A_profile_that_is_not_plain_json_is_not_rewritten() =>
        Assert.Null(AdoptProbes.WithVerify("// a comment\n{}", "o/r", ["make", "test"]));
}
