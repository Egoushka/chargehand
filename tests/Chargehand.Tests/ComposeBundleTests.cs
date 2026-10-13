using System.Text.RegularExpressions;
using Chargehand.Config;
using Chargehand.Containers;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>deploy/compose/ is what a stranger copies (docs/guide/install.md): the profile templates must load, and the runner command in
/// compose.yaml must agree with them and with RunnerCli. CI also runs <c>docker compose config -q</c> on the file (ci.yml, job compose).</summary>
public partial class ComposeBundleTests
{
    private static string Bundle(string name) => Repo.Path("deploy", "compose", name);

    [Fact]
    public void The_core_template_loads_and_has_no_driven_block()
    {
        var profile = Profile.Load(Bundle("profile.core.json"));

        Assert.Null(profile.Driven);
        Assert.Equal("0.0.0.0", profile.Http!.Listen);
        Assert.NotEmpty(profile.Http.AllowedHosts!);                     // a non-loopback listen needs one (ADR 0024)
    }

    [Fact]
    public void The_driven_template_agrees_with_the_runner_command_in_compose()
    {
        var profile = Profile.Load(Bundle("profile.driven.json"));
        var driven = profile.Driven!;
        var settings = RunnerCli.Parse(RunnerArguments(), "key", TextWriter.Null)!;

        Assert.True(driven.Enabled);
        Assert.Equal([.. driven.Images!], settings.Policy.Images);                                  // the runner starts exactly the profile's session image
        Assert.All(driven.Images!, image => Assert.True(ContainerTemplate.IsImageReference(image)));  // by digest, never a tag
        Assert.Equal(settings.Policy.Images[0], settings.Policy.EgressImage);
        Assert.Equal([profile.WorkerRoot], settings.Policy.SourceRoots);                            // the runner and the server mean the same path
        Assert.Contains(profile.WorkerRoot, profile.RepositoryRoots!);                               // covers <worker_root>/.checkouts
        Assert.Equal([driven.Network!.Outside!], settings.Policy.OutsideNetworks);
        Assert.Equal([$"{profile.Http!.Port}={driven.Network.McpForward}"], settings.Policy.Forwards);
        Assert.Equal($"http://{settings.AllowedHosts![0]}:{settings.Port}", driven.Runner!.Url);
        Assert.Contains("chargehand-driven", profile.Http.AllowedHosts!);                           // the host name a session's callback carries
    }

    [Fact]
    public void The_env_example_and_the_compose_default_pin_the_same_session_image_as_the_template()
    {
        var image = Profile.Load(Bundle("profile.driven.json")).Driven!.Images![0];

        Assert.Contains($"#CHARGEHAND_SESSION_IMAGE={image}", File.ReadAllText(Bundle(".env.example")), StringComparison.Ordinal);
        Assert.Contains($"${{CHARGEHAND_SESSION_IMAGE:-{image}}}", File.ReadAllText(Bundle("compose.yaml")), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_socket_proxy_touches_the_engine_socket_and_the_env_example_holds_no_value()
    {
        var lines = File.ReadAllLines(Bundle("compose.yaml"));
        var socket = lines.Select((line, i) => (line, i)).Where(x => !x.line.TrimStart().StartsWith('#') && x.line.Contains("docker.sock", StringComparison.Ordinal)).ToList();
        var proxyAt = Array.FindIndex(lines, l => l.StartsWith("  docker-proxy:", StringComparison.Ordinal));
        var nextService = Array.FindIndex(lines, proxyAt + 1, l => Regex.IsMatch(l, @"^\S"));

        Assert.Single(socket);
        Assert.InRange(socket[0].i, proxyAt, nextService);
        Assert.All(File.ReadAllLines(Bundle(".env.example")).Where(l => Regex.IsMatch(l, "^#?(CHARGEHAND_(API|RUNNER)_KEY|CHARGEHAND_PUSH_TOKEN|ANTHROPIC_API_KEY|CLAUDE_CODE_OAUTH_TOKEN)=")),
            l => Assert.EndsWith("=", l, StringComparison.Ordinal));
    }

    /// <summary>The arguments after <c>runner</c> in the runner service's <c>command</c>, with <c>${VAR:-default}</c> replaced by its default.</summary>
    private static List<string> RunnerArguments()
    {
        var lines = File.ReadAllLines(Bundle("compose.yaml"));
        var start = Array.FindIndex(lines, l => l.StartsWith("    command:", StringComparison.Ordinal));
        var items = lines.Skip(start + 1).TakeWhile(l => l.StartsWith("      ", StringComparison.Ordinal))
            .Select(l => l.Trim()).Where(l => !l.StartsWith('#')).Select(l => l[2..]).ToList();
        Assert.Equal("runner", items[0]);
        return [.. items.Skip(1).Select(v => DefaultOf().Replace(v, "$1"))];
    }

    [GeneratedRegex(@"^\$\{[A-Z_]+:-(.*)\}$")]
    private static partial Regex DefaultOf();
}
