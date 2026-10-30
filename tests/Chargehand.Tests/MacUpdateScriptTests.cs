using System.Diagnostics;

namespace Chargehand.Tests;

/// <summary>deploy/macos/update.sh (CHARGEHAND-328): updates the chargehand build under <c>~/.chargehand/app</c>. Runs here with a throwaway HOME and a throwaway
/// git checkout, only in <c>--dry-run</c>, the mode that starts nothing (as the other deploy/macos scripts are tested, MacRunnerScriptTests); it needs bash and git.</summary>
public sealed class MacUpdateScriptTests : IDisposable
{
    private readonly TempDir _home = new();
    private readonly TempDir _checkout = new();

    public void Dispose()
    {
        _home.Dispose();
        _checkout.Dispose();
    }

    private static void G(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {text}");
    }

    private void Commit(string message = "base")
    {
        File.WriteAllText(Path.Combine(_checkout.Path, "file.txt"), message);
        G(_checkout.Path, "add", "-A");
        G(_checkout.Path, "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-q", "-m", message);
    }

    private (int Exit, string Output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("bash") { WorkingDirectory = _home.Path, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(Repo.Path("deploy", "macos", "update.sh"));
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["HOME"] = _home.Path;
        psi.Environment["CHARGEHAND_UPDATE_REPO"] = _checkout.Path;
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    [Fact]
    public void A_dry_run_on_a_clean_main_checkout_prints_the_publish_swap_restart_and_probe_in_order_and_changes_nothing()
    {
        G(_checkout.Path, "init", "-q", "-b", "main");
        Commit();

        var (exit, output) = Run("--dry-run");

        Assert.Equal(0, exit);
        var publish = output.IndexOf("+ dotnet publish", StringComparison.Ordinal);
        var swap = output.IndexOf("+ ln -sfn", StringComparison.Ordinal);
        var restart = output.IndexOf("+ launchctl kickstart -k gui/", StringComparison.Ordinal);
        var probe = output.IndexOf("+ curl -fsS http://127.0.0.1:4300/v1/health", StringComparison.Ordinal);
        Assert.True(publish >= 0 && swap > publish && restart > swap && probe > restart, output);
        Assert.Contains("dry run: nothing was changed", output);
        Assert.False(Directory.Exists(Path.Combine(_home.Path, ".chargehand")));
    }

    [Fact]
    public void A_dirty_checkout_is_refused_before_anything_is_built()
    {
        G(_checkout.Path, "init", "-q", "-b", "main");
        Commit();
        File.AppendAllText(Path.Combine(_checkout.Path, "file.txt"), "\nmore");

        var (exit, output) = Run("--dry-run");

        Assert.NotEqual(0, exit);
        Assert.Contains("dirty", output);
        Assert.DoesNotContain("+ dotnet publish", output);
    }

    [Fact]
    public void A_checkout_not_on_main_is_refused_before_anything_is_built()
    {
        G(_checkout.Path, "init", "-q", "-b", "feature");
        Commit();

        var (exit, output) = Run("--dry-run");

        Assert.NotEqual(0, exit);
        Assert.Contains("'feature'", output);
        Assert.DoesNotContain("+ dotnet publish", output);
    }
}
