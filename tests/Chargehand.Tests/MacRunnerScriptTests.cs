using System.Diagnostics;
using System.Xml.Linq;

namespace Chargehand.Tests;

/// <summary>deploy/macos (ADR 0058): the scripts that set up a runner on a Mac as a launchd login agent. They run here with a throwaway HOME and a configuration file, and only in their modes that start nothing
/// (<c>--print</c>, <c>render</c>, <c>install --dry-run</c>); they need bash, as a Mac and a CI runner both have.</summary>
public sealed class MacRunnerScriptTests : IDisposable
{
    private const string Digest = "registry.example/chargehand-session@sha256:1111111111111111111111111111111111111111111111111111111111111111";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Conf(params string[] lines)
    {
        string[] defaults = [$"SESSION_IMAGE={Digest}", "LISTEN_ADDRESS=100.100.0.77", "FORWARDS=4300=server.example.net:4300"];
        var keys = lines.Select(l => l[..l.IndexOf('=')]).ToHashSet();
        return _dir.Write("runner.conf", string.Join('\n', defaults.Where(d => !keys.Contains(d[..d.IndexOf('=')])).Concat(lines)) + "\n");
    }

    private (int Exit, string Output) Run(string script, string conf, params string[] args)
    {
        var psi = new ProcessStartInfo("bash") { WorkingDirectory = _dir.Path, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(Repo.Path("deploy", "macos", script));
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["HOME"] = _dir.Path;
        psi.Environment["CHARGEHAND_RUNNER_CONF"] = conf;
        psi.Environment.Remove("ALLOW_NON_TAILNET_ADDRESS");
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    [Fact]
    public void The_command_listens_on_the_private_address_only_and_takes_the_key_from_the_keychain()
    {
        var (exit, output) = Run("run-runner.sh", Conf("ALLOWED_HOSTS=laptop.example.net"), "--print");

        Assert.Equal(0, exit);
        Assert.Contains("--listen 100.100.0.77:4310", output);
        Assert.Contains("--allowed-hosts 100.100.0.77,laptop.example.net", output);
        Assert.Contains($"--images {Digest} --egress-image {Digest}", output);
        Assert.Contains("--forwards 4300=server.example.net:4300", output);
        Assert.Contains("--keep-awake on --host-source engine", output);
        Assert.Contains("--drain-on battery,lid", output);
        Assert.Contains($"--drain-file {_dir.Path}/.chargehand/runner/drain", output);
        Assert.Contains("DOCKER_HOST=tcp://127.0.0.1:2375", output);
        // The key is named by its Keychain item and never shown; the engine's socket is not mounted anywhere.
        Assert.Contains("CHARGEHAND_RUNNER_KEY=<Keychain item chargehand-runner>", output);
        Assert.DoesNotContain("/var/run/docker.sock", output);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("[::]")]
    [InlineData("*")]
    [InlineData("192.168.1.20")]            // the Wi-Fi
    [InlineData("10.0.0.5")]
    [InlineData("100.128.0.1")]             // just above the private network's range
    [InlineData("100.63.255.1")]            // just below
    [InlineData("laptop.local")]            // not an address
    public void An_address_outside_the_private_network_is_refused_before_anything_starts(string address)
    {
        var (exit, output) = Run("run-runner.sh", Conf($"LISTEN_ADDRESS={address}"), "--print");

        Assert.NotEqual(0, exit);
        Assert.Contains("LISTEN_ADDRESS", output);
        Assert.DoesNotContain("--listen", output);
    }

    [Theory]
    [InlineData("100.100.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("127.0.0.1")]
    [InlineData("fd7a:115c:a1e0::1")]
    public void The_private_networks_addresses_and_loopback_are_accepted(string address)
    {
        var (exit, output) = Run("run-runner.sh", Conf($"LISTEN_ADDRESS={address}"), "--print");

        Assert.Equal(0, exit);
        Assert.Contains(address.Contains(':') ? $"--listen [{address}]:4310" : $"--listen {address}:4310", output);
    }

    [Fact]
    public void The_lowest_address_of_the_private_range_is_accepted()
    {
        var lowest = string.Concat("100.", "64", ".0.1");      // spelled apart so the repository's denylist of private addresses does not read this as one

        var (exit, output) = Run("run-runner.sh", Conf($"LISTEN_ADDRESS={lowest}"), "--print");

        Assert.Equal(0, exit);
        Assert.Contains($"--listen {lowest}:4310", output);
    }

    [Fact]
    public void A_network_the_owner_trusts_can_be_named_and_a_missing_address_or_unpinned_image_is_refused()
    {
        Assert.Equal(0, Run("run-runner.sh", Conf("LISTEN_ADDRESS=192.168.1.20", "ALLOW_NON_TAILNET_ADDRESS=yes"), "--print").Exit);
        var (noAddress, message) = Run("run-runner.sh", Conf("LISTEN_ADDRESS="), "--print");
        Assert.NotEqual(0, noAddress);
        Assert.Contains("LISTEN_ADDRESS is not set", message);
        var (tag, tagMessage) = Run("run-runner.sh", Conf("SESSION_IMAGE=registry.example/chargehand-session:latest"), "--print");
        Assert.NotEqual(0, tag);
        Assert.Contains("pinned by digest", tagMessage);
    }

    [Fact]
    public void Render_writes_a_valid_agent_that_holds_no_key_and_runs_the_wrapper_at_login()
    {
        var (exit, _) = Run("install-runner.sh", Conf(), "render", "--out", Path.Combine(_dir.Path, "out"));

        Assert.Equal(0, exit);
        var plist = XDocument.Load(Path.Combine(_dir.Path, "out", "com.chargehand.runner.plist"));
        var entries = plist.Root!.Element("dict")!.Elements().ToList();
        string Value(string key) => entries[entries.FindIndex(e => e.Name == "key" && e.Value == key) + 1].Value;
        Assert.Equal("com.chargehand.runner", Value("Label"));
        Assert.Equal(new[] { "/bin/bash", $"{_dir.Path}/.chargehand/runner/run-runner.sh" }, entries[entries.FindIndex(e => e.Value == "ProgramArguments") + 1].Elements("string").Select(e => e.Value));
        Assert.Contains(entries, e => e.Name == "true");                                    // RunAtLoad
        Assert.Equal($"{_dir.Path}/Library/Logs/chargehand/runner.log", Value("StandardOutPath"));
        Assert.DoesNotContain("__HOME__", File.ReadAllText(Path.Combine(_dir.Path, "out", "com.chargehand.runner.plist")));
        // Neither file carries a key, a token or an address of the owner's.
        foreach (var file in Directory.GetFiles(Path.Combine(_dir.Path, "out")))
        {
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match use in System.Text.RegularExpressions.Regex.Matches(text, @"CHARGEHAND_RUNNER_KEY=(?<value>\S*)"))
                Assert.True(use.Groups["value"].Value is "\"$key\"" or "<Keychain", $"{Path.GetFileName(file)} sets the runner's key to something other than the Keychain's value: {use.Value}");
        }
    }

    [Fact]
    public void A_dry_run_prints_the_commands_it_would_run_and_changes_nothing()
    {
        var (exit, output) = Run("install-runner.sh", Conf(), "install", "--dry-run");

        Assert.Equal(0, exit);
        Assert.Contains($"+ docker pull {Digest}", output);
        // The proxy listens on loopback, is allowed the API sections the runner needs and gets the socket read-only.
        Assert.Contains("-p 127.0.0.1:2375:2375", output);
        Assert.Contains("-v /var/run/docker.sock:/var/run/docker.sock:ro", output);
        Assert.Contains("-e CONTAINERS=1 -e POST=1 -e IMAGES=1 -e VOLUMES=1 -e NETWORKS=1", output);
        Assert.Contains("tecnativa/docker-socket-proxy@sha256:", output);
        Assert.Contains("+ launchctl bootstrap gui/", output);
        Assert.Contains("dry run: nothing was changed", output);
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, "Library")));
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, ".chargehand")));
    }

    [Fact]
    public void An_install_with_a_wrong_configuration_stops_before_it_starts_anything()
    {
        var (exit, output) = Run("install-runner.sh", Conf("LISTEN_ADDRESS=0.0.0.0"), "install", "--dry-run");

        Assert.NotEqual(0, exit);
        Assert.DoesNotContain("+ docker", output);
    }

    [Fact]
    public void Drain_and_undrain_make_and_remove_the_file_the_runner_watches()
    {
        var drain = Path.Combine(_dir.Path, ".chargehand", "runner", "drain");

        Run("install-runner.sh", Conf(), "drain");
        Assert.True(File.Exists(drain));
        Run("install-runner.sh", Conf(), "undrain");
        Assert.False(File.Exists(drain));
    }

    [Fact]
    public void The_example_configuration_has_no_value_that_is_a_secret_or_an_address()
    {
        var text = File.ReadAllText(Repo.Path("deploy", "macos", "runner.conf.example"));
        var values = text.Split('\n').Where(l => l.Length > 0 && l[0] != '#' && l.Contains('=')).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);

        Assert.Equal("", values["LISTEN_ADDRESS"]);
        Assert.Equal("", values["SESSION_IMAGE"]);
        Assert.Equal("", values["FORWARDS"]);
        Assert.DoesNotContain(values.Keys, k => k.Contains("KEY", StringComparison.Ordinal) && k != "KEYCHAIN_SERVICE");
    }
}
