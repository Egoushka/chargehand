using Chargehand.Contracts;
using Chargehand.Sandbox;

namespace Chargehand.Tests;

/// <summary>Goal 0.7 (ADR 0035): the sandbox a writing run's tests execute in.</summary>
public class SandboxTests
{
    private static SandboxSpec Spec(string work, bool network, params string[] argv) => new(argv, work, TimeSpan.FromSeconds(30), network, []);

    [Fact]
    public void Auto_on_a_machine_with_no_sandbox_refuses_with_an_action()
    {
        var e = Assert.Throws<ChargehandException>(() => SandboxSelector.Select(null, _ => false, isMacOs: false, isLinux: true));
        Assert.Equal(ErrorCode.SandboxUnavailable, e.Code);
        Assert.Contains("sandbox.kind", e.Action, StringComparison.Ordinal);
    }

    [Fact]
    public void None_is_chosen_only_when_asked_for_and_the_platform_sandbox_otherwise()
    {
        Assert.Equal("none", SandboxSelector.Select(new SandboxSettings("none"), _ => false, false, true).Kind);
        Assert.Equal("bubblewrap", SandboxSelector.Select(null, n => n == "bwrap", false, true).Kind);
        Assert.Equal("seatbelt", SandboxSelector.Select(null, n => n == "sandbox-exec", true, false).Kind);
    }

    [Fact]
    public void A_named_sandbox_that_is_missing_is_refused_and_an_unknown_kind_is_invalid()
    {
        Assert.Equal(ErrorCode.SandboxUnavailable, Assert.Throws<ChargehandException>(() => SandboxSelector.Select(new SandboxSettings("bubblewrap"), _ => false, false, true)).Code);
        Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ChargehandException>(() => SandboxSelector.Select(new SandboxSettings("docker"), _ => true, true, true)).Code);
    }

    [Fact]
    public void Seatbelt_profile_denies_network_and_credentials_and_writes_only_the_workspace()
    {
        var p = SeatbeltSandbox.Profile(Spec("/w/run", false, "true"), "/h", "/t");
        Assert.Contains("(deny default)", p, StringComparison.Ordinal);
        Assert.DoesNotContain("(allow network", p, StringComparison.Ordinal);
        Assert.Contains("(deny file-read* (subpath \"/h/.ssh\"))", p, StringComparison.Ordinal);
        Assert.Contains("(allow file-write* (subpath \"/w/run\") (subpath \"/t\")", p, StringComparison.Ordinal);
        Assert.Contains("(allow network*)", SeatbeltSandbox.Profile(Spec("/w/run", true, "true"), "/h", "/t"), StringComparison.Ordinal);
    }

    [Fact]
    public void Bubblewrap_shares_the_network_only_on_request_and_hides_credentials()
    {
        var env = new Dictionary<string, string> { ["PATH"] = "/usr/bin" };
        using var home = new TempDir();
        var ssh = Directory.CreateDirectory(Path.Combine(home.Path, ".ssh")).FullName;
        var off = BubblewrapSandbox.Args(Spec("/w", false, "true"), [ssh], "/t", env);
        var on = BubblewrapSandbox.Args(Spec("/w", true, "true"), [ssh], "/t", env);
        Assert.DoesNotContain("--share-net", off);
        Assert.Contains("--share-net", on);
        Assert.Contains("--clearenv", off);
        Assert.Equal(["--tmpfs", ssh], off.SkipWhile(a => a != "--tmpfs").Take(2));
        Assert.Equal(["--", "true"], off.TakeLast(2));
    }

    [Fact]
    public void The_environment_is_the_allowlist_plus_named_variables()
    {
        Environment.SetEnvironmentVariable("CHARGEHAND_TEST_PASS", "yes");
        Environment.SetEnvironmentVariable("CHARGEHAND_TEST_SECRET", "no");
        var env = ProcessRunner.Environment("/t", ["CHARGEHAND_TEST_PASS"]);
        Assert.Equal("yes", env["CHARGEHAND_TEST_PASS"]);
        Assert.False(env.ContainsKey("CHARGEHAND_TEST_SECRET"));
        Assert.Equal("/t", env["HOME"]);
    }

    [Fact]
    public async Task No_sandbox_runs_the_command_with_a_cut_environment()
    {
        Environment.SetEnvironmentVariable("CHARGEHAND_TEST_SECRET", "no");
        using var work = new TempDir();
        var r = await new NoSandbox().RunAsync(Spec(work.Path, false, "/bin/sh", "-c", "echo secret=${CHARGEHAND_TEST_SECRET-unset}"), default);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("secret=unset", r.OutputTail, StringComparison.Ordinal);
    }

    [MacOsFact]
    public async Task Seatbelt_blocks_a_write_outside_the_workspace_and_a_read_of_a_credential_directory()
    {
        using var dir = new TempDir();
        var home = Directory.CreateDirectory(Path.Combine(dir.Path, "home")).FullName;
        Directory.CreateDirectory(Path.Combine(home, ".ssh"));
        File.WriteAllText(Path.Combine(home, ".ssh", "id"), "topsecret");
        var work = Directory.CreateDirectory(Path.Combine(dir.Path, "work")).FullName;
        var outside = Path.Combine(dir.Path, "outside.txt");
        var sandbox = new SeatbeltSandbox(home);

        var write = await sandbox.RunAsync(Spec(work, false, "/bin/sh", "-c", $"echo x > '{outside}'"), default);
        var read = await sandbox.RunAsync(Spec(work, false, "/bin/cat", Path.Combine(home, ".ssh", "id")), default);
        var inside = await sandbox.RunAsync(Spec(work, false, "/bin/sh", "-c", "echo ok > inside.txt && cat inside.txt"), default);

        Assert.NotEqual(0, write.ExitCode);
        Assert.False(File.Exists(outside));
        Assert.NotEqual(0, read.ExitCode);
        Assert.DoesNotContain("topsecret", read.OutputTail, StringComparison.Ordinal);
        Assert.Equal(0, inside.ExitCode);
        Assert.Contains("ok", inside.OutputTail, StringComparison.Ordinal);
    }

    [MacOsFact]
    public async Task Seatbelt_blocks_a_connection_unless_the_network_is_allowed()
    {
        using var dir = new TempDir();
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var connect = new[] { "/usr/bin/nc", "-z", "-w", "2", "127.0.0.1", port };
            Assert.NotEqual(0, (await new SeatbeltSandbox().RunAsync(Spec(dir.Path, false, connect), default)).ExitCode);
            Assert.Equal(0, (await new SeatbeltSandbox().RunAsync(Spec(dir.Path, true, connect), default)).ExitCode);
        }
        finally
        {
            listener.Stop();
        }
    }
}

/// <summary>Runs a test only on macOS, where <c>sandbox-exec</c> exists.</summary>
public sealed class MacOsFactAttribute : FactAttribute
{
    public MacOsFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "macOS only";
    }
}
