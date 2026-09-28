using Chargehand.Config;

namespace Chargehand.Tests;

/// <summary>ADR 0027 follow-up 2: prompts/ and presets/ from a checkout when launched in one, else from the install.</summary>
public class InstallPathsTests
{
    [Fact]
    public void A_checkout_keeps_its_own_prompts_presets_and_relative_run_log()
    {
        using var cwd = new TempDir();
        Directory.CreateDirectory(Path.Combine(cwd.Path, "prompts"));
        Directory.CreateDirectory(Path.Combine(cwd.Path, "presets"));

        var (root, runLog) = InstallPaths.Resolve(null, cwd.Path, "/app", "/user-data");

        Assert.Equal(cwd.Path, root);
        Assert.Equal("runs/run-log.jsonl", runLog);
    }

    [Fact]
    public void Elsewhere_the_install_and_a_per_user_run_log()
    {
        using var cwd = new TempDir();
        Directory.CreateDirectory(Path.Combine(cwd.Path, "prompts")); // a project's own prompts/ alone is not a checkout

        var (root, runLog) = InstallPaths.Resolve(null, cwd.Path, "/app", "/user-data");

        Assert.Equal("/app", root);
        Assert.Equal(Path.Combine("/user-data", "chargehand", "run-log.jsonl"), runLog);
    }

    [Fact]
    public void The_profile_run_log_wins()
    {
        using var cwd = new TempDir();

        Assert.Equal("runs/eval-log.jsonl", InstallPaths.Resolve("runs/eval-log.jsonl", cwd.Path, "/app", "/user-data").RunLog);
    }
}
