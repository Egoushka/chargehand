using Chargehand.Server;

namespace Chargehand.Tests;

public class RunnerCliTests
{
    private const string Image = "registry.example/session@sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string Egress = "registry.example/chargehand@sha256:2222222222222222222222222222222222222222222222222222222222222222";

    [Fact]
    public void A_full_command_line_parses_into_settings()
    {
        var settings = RunnerCli.Parse(["--listen", "127.0.0.1:4310", "--images", Image, "--egress-image", Egress, "--max-containers", "6", "--allowed-hosts", "runner.internal", "--source-roots", "/srv/checkouts,/srv/other"],
            key: "k", TextWriter.Null);
        Assert.Equal(4310, settings!.Port);
        Assert.Equal("127.0.0.1", settings.Listen);
        Assert.Equal([Image], settings.Policy.Images);
        Assert.Equal(Egress, settings.Policy.EgressImage);
        Assert.Equal(6, settings.Policy.MaxContainers);
        Assert.Equal(["runner.internal"], settings.AllowedHosts);
        Assert.Equal(["/srv/checkouts", "/srv/other"], settings.Policy.SourceRoots);
    }

    [Theory]
    [InlineData("--listen", "127.0.0.1:4310", "--egress-image", Egress)]                                   // no images
    [InlineData("--listen", "127.0.0.1:4310", "--images", Image)]                                          // no egress image
    [InlineData("--images", Image, "--egress-image", Egress)]                                              // no listen
    [InlineData("--listen", "127.0.0.1:4310", "--images", "registry.example/session:latest", "--egress-image", Egress)]   // a tag
    [InlineData("--listen", "127.0.0.1:4310", "--images", Image, "--egress-image", "evil")]
    [InlineData("--listen", "127.0.0.1:4310", "--images", Image, "--egress-image", Egress, "--unknown", "x")]
    [InlineData("--listen", "127.0.0.1:4310", "--images", Image, "--egress-image", Egress, "--source-roots", "relative/root")]
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
}
