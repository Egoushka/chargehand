namespace Chargehand.Kernel.Tests;

/// <summary>ADR 0027 follow-up 2: prompts/ and presets/ from a checkout when launched in one, else from the install.</summary>
public class InstallPathsTests : IDisposable
{
    private readonly string _cwd = Directory.CreateTempSubdirectory("install-").FullName;

    public void Dispose()
    {
        Directory.Delete(_cwd, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_checkout_keeps_its_own_prompts_presets_and_relative_run_log()
    {
        Directory.CreateDirectory(Path.Combine(_cwd, "prompts"));
        Directory.CreateDirectory(Path.Combine(_cwd, "presets"));

        var (root, runLog) = InstallPaths.Resolve(null, _cwd, "/app", "/user-data");

        Assert.Equal(_cwd, root);
        Assert.Equal("runs/run-log.jsonl", runLog);
    }

    [Fact]
    public void Elsewhere_the_install_and_a_per_user_run_log()
    {
        Directory.CreateDirectory(Path.Combine(_cwd, "prompts")); // a project's own prompts/ alone is not a checkout

        var (root, runLog) = InstallPaths.Resolve(null, _cwd, "/app", "/user-data");

        Assert.Equal("/app", root);
        Assert.Equal(Path.Combine("/user-data", "chargehand", "run-log.jsonl"), runLog);
    }

    [Fact]
    public void The_profile_run_log_wins() =>
        Assert.Equal("runs/eval-log.jsonl", InstallPaths.Resolve("runs/eval-log.jsonl", _cwd, "/app", "/user-data").RunLog);
}
