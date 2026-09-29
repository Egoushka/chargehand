using System.Diagnostics;

namespace Chargehand.Tests;

/// <summary>scripts/opencode-serve.sh starts the server the launchd agents and docs/guide/quickstart.md use. It must not let
/// a worker's checkout configure that server (a checkout's opencode.json can register an MCP server whose command
/// OpenCode then starts).</summary>
public sealed class OpenCodeServeScriptTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Turns_project_config_off_even_when_the_caller_environment_says_otherwise()
    {
        var bin = _dir.Write("opencode", $$"""
            #!/bin/sh
            echo "args=$*" > "{{_dir.Path}}/seen.txt"
            echo "disable_project_config=$OPENCODE_DISABLE_PROJECT_CONFIG config_project_disable=$OPENCODE_CONFIG_PROJECT_DISABLE" >> "{{_dir.Path}}/seen.txt"
            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(bin, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var config = _dir.Write("opencode.json", "{}");
        var psi = new ProcessStartInfo("sh") { WorkingDirectory = _dir.Path };
        foreach (var a in new[] { Repo.Path("scripts", "opencode-serve.sh"), bin, config, "4999" })
            psi.ArgumentList.Add(a);
        psi.Environment["CHARGEHAND_STATE"] = Path.Combine(_dir.Path, "state");
        psi.Environment["OPENCODE_SERVER_PASSWORD"] = "test-password";
        psi.Environment["OPENCODE_CONFIG_PROJECT_DISABLE"] = "0";

        using var p = Process.Start(psi)!;
        p.WaitForExit();

        Assert.Equal(0, p.ExitCode);
        Assert.Equal(["args=serve --hostname 127.0.0.1 --port 4999", "disable_project_config=1 config_project_disable=1"],
            File.ReadAllLines(Path.Combine(_dir.Path, "seen.txt")));
    }
}
