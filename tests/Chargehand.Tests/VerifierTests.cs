using Chargehand.Sandbox;
using Chargehand.Verification;

namespace Chargehand.Tests;

/// <summary>Goal 0.7 (ADR 0035): which command verifies a change, and running it in the sandbox.</summary>
public class VerifierTests
{
    [Theory]
    [InlineData("App.sln", "dotnet test")]
    [InlineData("App.slnx", "dotnet test")]
    [InlineData("App.csproj", "dotnet test")]
    [InlineData("Cargo.toml", "cargo test")]
    [InlineData("go.mod", "go test ./...")]
    [InlineData("pytest.ini", "python3 -m pytest")]
    [InlineData("tests/test_a.py", "python3 -m unittest")]
    public void The_test_command_is_found_from_the_files_present(string marker, string expected)
    {
        using var dir = new TempDir();
        dir.Write(marker, "");
        var plan = VerifyCommand.Resolve(null, dir.Path);
        Assert.Equal(expected, string.Join(' ', plan!.Argv));
        Assert.Equal("detected", plan.Source);
    }

    [Fact]
    public void Package_json_needs_a_test_script_and_pyproject_may_name_pytest()
    {
        using var withScript = new TempDir();
        withScript.Write("package.json", """{"scripts":{"test":"jest"}}""");
        Assert.Equal("npm test", string.Join(' ', VerifyCommand.Resolve(null, withScript.Path)!.Argv));

        using var noScript = new TempDir();
        noScript.Write("package.json", """{"scripts":{"build":"tsc"}}""");
        Assert.Null(VerifyCommand.Resolve(null, noScript.Path));

        using var broken = new TempDir();
        broken.Write("package.json", "{ not json");
        Assert.Null(VerifyCommand.Resolve(null, broken.Path));

        using var pytest = new TempDir();
        pytest.Write("pyproject.toml", "[tool.pytest.ini_options]\n");
        Assert.Equal("python3 -m pytest", string.Join(' ', VerifyCommand.Resolve(null, pytest.Path)!.Argv));
    }

    [Fact]
    public void An_empty_directory_has_no_command_and_the_requests_command_wins()
    {
        using var dir = new TempDir();
        Assert.Null(VerifyCommand.Resolve(null, dir.Path));
        Assert.Null(VerifyCommand.Resolve([], dir.Path));
        dir.Write("go.mod", "");
        var plan = VerifyCommand.Resolve(["make", "check"], dir.Path)!;
        Assert.Equal(["make", "check"], plan.Argv);
        Assert.Equal("request", plan.Source);
    }

    private static Verifier Sh() => new(new NoSandbox(), false, []);

    private static VerifyPlan Script(string body) => new(["/bin/sh", "-c", body], "request");

    [Fact]
    public async Task A_command_that_exits_zero_passes_and_reports_its_output()
    {
        using var dir = new TempDir();
        var outcome = await Sh().RunAsync(Script("echo green"), dir.Path, TimeSpan.FromSeconds(20), default);
        Assert.True(outcome.Passed);
        Assert.Contains("green", outcome.OutputTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_that_exits_non_zero_fails_with_its_code()
    {
        using var dir = new TempDir();
        var outcome = await Sh().RunAsync(Script("echo red >&2; exit 3"), dir.Path, TimeSpan.FromSeconds(20), default);
        Assert.False(outcome.Passed);
        Assert.Equal(3, outcome.ExitCode);
        Assert.Contains("red", outcome.OutputTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hanging_command_is_killed_at_the_timeout()
    {
        using var dir = new TempDir();
        var outcome = await Sh().RunAsync(Script("sleep 30"), dir.Path, TimeSpan.FromSeconds(1), default);
        Assert.True(outcome.TimedOut);
        Assert.False(outcome.Passed);
        Assert.True(outcome.Duration < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Output_is_tailed_to_8_KiB_and_keeps_the_end()
    {
        using var dir = new TempDir();
        var outcome = await Sh().RunAsync(Script("i=0; while [ $i -lt 3000 ]; do echo line-$i-xxxxxxxxxx; i=$((i+1)); done; echo THE-END"), dir.Path, TimeSpan.FromSeconds(30), default);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(outcome.OutputTail) <= 8192);
        Assert.EndsWith("THE-END", outcome.OutputTail.TrimEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain("line-0-", outcome.OutputTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_command_runs_in_the_given_directory_and_reports_the_sandbox()
    {
        using var dir = new TempDir();
        dir.Write("marker.txt", "here");
        var verifier = Sh();
        var outcome = await verifier.RunAsync(Script("cat marker.txt"), dir.Path, TimeSpan.FromSeconds(20), default);
        Assert.Contains("here", outcome.OutputTail, StringComparison.Ordinal);
        Assert.Equal("none", verifier.SandboxKind);
        Assert.False(verifier.Network);
    }
}
