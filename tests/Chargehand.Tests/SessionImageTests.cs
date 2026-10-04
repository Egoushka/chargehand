using System.Diagnostics;

namespace Chargehand.Tests;

/// <summary>The session image (ADR 0039) on a real engine. Needs CHARGEHAND_TEST_SESSION_IMAGE: a locally built image
/// (<c>docker build -q -f images/session/Dockerfile .</c> prints its id).</summary>
public class SessionImageTests
{
    private static readonly string? Image = Environment.GetEnvironmentVariable("CHARGEHAND_TEST_SESSION_IMAGE");

    private sealed class SessionImageFactAttribute : FactAttribute
    {
        public SessionImageFactAttribute() => Skip = string.IsNullOrEmpty(Image)
            ? "set CHARGEHAND_TEST_SESSION_IMAGE to a locally built session image (docker build -q -f images/session/Dockerfile .)"
            : DockerFactAttribute.Reason(egress: false) is { } reason && reason.StartsWith("no Docker", StringComparison.Ordinal) ? reason : null;
    }

    private static string Run(params string[] shell)
    {
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "run", "--rm", "--entrypoint", "sh", Image!, "-c" }.Concat(shell))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return text;
    }

    [SessionImageFact]
    public void Image_has_the_toolchains_a_session_needs_and_no_docker_client()
    {
        var output = Run("dotnet --version; node --version; python3 --version; git --version; claude --version; id -u; command -v docker || echo no-docker; command -v pip3 >/dev/null && echo pip-ok");
        Assert.Matches(@"(?m)^10\.\d+\.\d+", output);           // dotnet
        Assert.Matches(@"(?m)^v(2[6-9]|[3-9]\d)\.\d+\.\d+", output); // node 26 or later: repositories that run .ts sources need its type stripping
        Assert.Contains("Python 3.", output);
        Assert.Contains("git version", output);
        Assert.Contains("2.1.283 (Claude Code)", output);         // the pinned version the adapter demands
        Assert.Contains("10001", output);                        // not root
        Assert.Contains("no-docker", output);
        Assert.Contains("pip-ok", output);
    }

    [SessionImageFact]
    public void The_plugin_and_the_driver_are_in_place()
    {
        var output = Run("test -f /opt/chargehand-plugin/skills/change/SKILL.md && echo skill-ok; test -f /opt/chargehand/Chargehand.Cli.dll && echo cli-ok; dotnet /opt/chargehand/Chargehand.Cli.dll session --out /nonexistent; echo exit=$?");
        Assert.Contains("skill-ok", output);
        Assert.Contains("cli-ok", output);
        Assert.Contains("task.json", output);                    // the session verb ran and looked for its task file (an unknown verb prints the usage instead)
        Assert.DoesNotContain("usage: chargehand [--profile", output);
        Assert.Contains("exit=2", output);                       // no task file: a usage-class failure, not a crash
    }
}
