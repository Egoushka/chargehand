using Chargehand.Server;

namespace Chargehand.Tests;

public class RunnerCliTests
{
    private const string Image = "registry.example/session@sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string Egress = "registry.example/chargehand@sha256:2222222222222222222222222222222222222222222222222222222222222222";

    [Fact]
    public void A_full_command_line_parses_into_settings()
    {
        var settings = RunnerCli.Parse(["--listen", "127.0.0.1:4310", "--images", Image, "--egress-image", Egress, "--max-containers", "6", "--allowed-hosts", "runner.internal", "--source-roots", "/srv/checkouts,/srv/other", "--outside-networks", "stack_net", "--forwards", "4300=chargehand:4300"],
            key: "k", TextWriter.Null);
        Assert.Equal(4310, settings!.Port);
        Assert.Equal("127.0.0.1", settings.Listen);
        Assert.Equal([Image], settings.Policy.Images);
        Assert.Equal(Egress, settings.Policy.EgressImage);
        Assert.Equal(6, settings.Policy.MaxContainers);
        Assert.Equal(["runner.internal"], settings.AllowedHosts);
        Assert.Equal(["/srv/checkouts", "/srv/other"], settings.Policy.SourceRoots);
        Assert.Equal(["stack_net"], settings.Policy.OutsideNetworks);
        Assert.Equal(["4300=chargehand:4300"], settings.Policy.Forwards);
    }

    [Theory]
    [InlineData("--listen", "127.0.0.1:4310", "--egress-image", Egress)]                                   // no images
    [InlineData("--listen", "127.0.0.1:4310", "--images", Image)]                                          // no egress image
    [InlineData("--images", Image, "--egress-image", Egress)]                                              // no listen
    [InlineData("--listen", "127.0.0.1:4310", "--images", "registry.example/session:latest", "--egress-image", Egress)]   // a tag
    [InlineData("--listen", "127.0.0.1:4310", "--images", Image, "--egress-image", "evil")]
    [InlineData("--listen", "127.0.0.1:4310", "--images", Image, "--egress-image", Egress, "--unknown", "x")]
    [InlineData("--listen", "127.0.0.1:4310", "--images", Image, "--egress-image", Egress, "--source-roots", "relative/root")]
    [InlineData("--listen", "127.0.0.1:4310", "--images", Image, "--egress-image", Egress, "--forwards", "not-a-forward")]
    [InlineData("--listen", "127.0.0.1:4310", "--images", Image, "--egress-image", Egress, "--source-roots", "/")]
    public void A_bad_command_line_prints_usage_and_returns_nothing(params string[] args)
    {
        var error = new StringWriter();
        Assert.Null(RunnerCli.Parse(args, key: "k", error));
        Assert.Contains("usage: chargehand runner", error.ToString());
    }

    [Fact]
    public void Without_a_key_it_refuses_and_says_which_variable()
    {
        var error = new StringWriter();
        Assert.Null(RunnerCli.Parse(["--listen", "127.0.0.1:4310", "--images", Image, "--egress-image", Egress], key: null, error));
        Assert.Contains("CHARGEHAND_RUNNER_KEY", error.ToString());
    }

    [Theory]
    [InlineData(new[] { "--listen", "127.0.0.1:4310", "--egress-image", Egress }, "--images")]
    [InlineData(new[] { "--listen", "127.0.0.1:4310", "--images", Image }, "--egress-image")]
    [InlineData(new[] { "--images", Image, "--egress-image", Egress }, "--listen")]
    [InlineData(new[] { "--listen", "nonsense", "--images", Image, "--egress-image", Egress }, "--listen")]
    [InlineData(new[] { "--listen", "127.0.0.1:4310", "--images", "registry.example/session:latest", "--egress-image", Egress }, "registry.example/session:latest")]
    [InlineData(new[] { "--listen", "127.0.0.1:4310", "--images", Image, "--egress-image", "evil" }, "evil")]
    [InlineData(new[] { "--listen", "127.0.0.1:4310", "--images", Image, "--egress-image", Egress, "--unknown", "x" }, "--unknown")]
    [InlineData(new[] { "--listen", "127.0.0.1:4310", "--images", Image, "--egress-image", Egress, "--max-containers", "0" }, "--max-containers")]
    [InlineData(new[] { "--listen", "127.0.0.1:4310", "--images", Image, "--egress-image", Egress, "--source-roots", "relative/root" }, "relative/root")]
    [InlineData(new[] { "--listen", "127.0.0.1:4310", "--images", Image, "--egress-image", Egress, "--forwards", "not-a-forward" }, "not-a-forward")]
    [InlineData(new[] { "--listen", "127.0.0.1:4310", "--images", Image, "--egress-image", Egress, "--forwards" }, "--forwards")]
    public void A_bad_command_line_says_which_argument_it_rejected(string[] args, string mentions)
    {
        // The first deploy crash-looped on a tagged image reference and the runner printed only its usage line.
        var error = new StringWriter();
        Assert.Null(RunnerCli.Parse(args, key: "k", error));
        var text = error.ToString();
        var reason = text.Split('\n')[0].TrimEnd('\r');
        Assert.NotEqual(RunnerCli.Usage, reason);   // the usage line itself names every flag, so it cannot be the reason
        Assert.Contains(mentions, reason);
        Assert.Contains(RunnerCli.Usage, text);
    }

    [Fact]
    public void A_valid_command_line_with_empty_policy_lists_warns_about_each()
    {
        // Each of these is accepted and then silently refuses work later: no workspace is prepared, the egress cannot join the server's network,
        // a session cannot call the server back.
        var error = new StringWriter();
        Assert.NotNull(RunnerCli.Parse(["--listen", "127.0.0.1:4310", "--images", Image, "--egress-image", Egress], key: "k", error));
        var text = error.ToString();
        Assert.Contains("no --source-roots", text);
        Assert.Contains("no --outside-networks", text);
        Assert.Contains("no --forwards", text);

        var quiet = new StringWriter();
        Assert.NotNull(RunnerCli.Parse(["--listen", "127.0.0.1:4310", "--images", Image, "--egress-image", Egress, "--source-roots", "/srv/w", "--outside-networks", "n", "--forwards", "4300=h:4300"], key: "k", quiet));
        Assert.Equal("", quiet.ToString());
    }
}
