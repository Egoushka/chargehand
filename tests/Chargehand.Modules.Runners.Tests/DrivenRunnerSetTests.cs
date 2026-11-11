using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Gateway.Api;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Modules.Runners.Tests;

/// <summary>ADR 0058, ADR 0078: the runners of a <c>driven</c> block as one set, and the rules that refuse one that cannot work.</summary>
public sealed class DrivenRunnerSetTests
{
    private const string Image = "ghcr.io/example/session@sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string ArmImage = "ghcr.io/example/session@sha256:2222222222222222222222222222222222222222222222222222222222222222";

    [Fact]
    public void One_runner_is_the_default_named_runner_and_runners_keep_their_order()
    {
        var one = Assert.Single(DrivenRunnerSet.Resolve(new DrivenRunner("http://runner:4310", "runner-key"), null));
        Assert.Equal((DrivenRunnerSet.DefaultRunnerName, "http://runner:4310", "runner-key"), (one.Name, one.Url, one.ApiKeySecret));
        Assert.False(DrivenRunnerSet.Names(null));

        var named = new List<DrivenRunnerEntry> { new("box", "http://box:4310", "k1"), new("laptop", "http://laptop:4310", "k2") };
        Assert.Equal(["box", "laptop"], DrivenRunnerSet.Resolve(null, named).Select(r => r.Name));
        Assert.True(DrivenRunnerSet.Names(named));
        Assert.Empty(DrivenRunnerSet.Resolve(null, null));
    }

    [Fact]
    public void The_image_follows_the_platform_through_its_variant_and_is_unchanged_without_one()
    {
        var variants = new Dictionary<string, IReadOnlyDictionary<string, string>> { [Image] = new Dictionary<string, string> { ["linux/arm64"] = ArmImage } };

        Assert.Equal(ArmImage, DrivenRunnerSet.ImageFor(variants, Image, "linux/arm64"));
        Assert.Equal(Image, DrivenRunnerSet.ImageFor(variants, Image, "linux/amd64"));
        Assert.Equal(Image, DrivenRunnerSet.ImageFor(null, Image, "linux/arm64"));
    }

    [Fact]
    public void Mise_is_an_environment_and_an_unknown_one_is_refused()
    {
        DrivenRunnerSet.ValidateEnvironment(null);
        DrivenRunnerSet.ValidateEnvironment("image");
        Assert.True(DrivenRunnerSet.UsesMise("mise"));
        Assert.False(DrivenRunnerSet.UsesMise(null));

        var e = Assert.Throws<ChargehandException>(() => DrivenRunnerSet.ValidateEnvironment("nix"));

        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.Equal("driven.environment 'nix' must be one of image, mise", e.Message);
    }

    [Fact]
    public void Both_runner_and_runners_an_empty_list_and_a_variant_off_the_images_list_are_refused_together()
    {
        var variants = new Dictionary<string, IReadOnlyDictionary<string, string>> { [ArmImage] = new Dictionary<string, string> { ["linux/arm64"] = Image, ["windows"] = "latest" } };

        var e = Assert.Throws<ChargehandException>(() => DrivenRunnerSet.ValidateRunners(new DrivenRunner("http://runner:4310", "k"), [], null, variants, [Image]));

        Assert.StartsWith("driven: name the runner in either runner or runners, not both; runners must list at least one runner (leave it out for none); ", e.Message);
        Assert.Contains($"image_variants.{ArmImage}: the key must also be listed in driven.images", e.Message);
        Assert.Contains($"image_variants.{ArmImage}: platform 'windows' must be one of linux/amd64, linux/arm64", e.Message);
        Assert.Contains($"image_variants.{ArmImage}.windows: must be pinned by digest", e.Message);
    }

    [Fact]
    public void A_bundle_runner_has_to_replace_the_networks_forward_and_a_shared_one_passes()
    {
        var network = new DrivenNetwork(McpForward: "chargehand-host:4300");
        DrivenRunnerSet.ValidateRunners(null, [new DrivenRunnerEntry("box", "http://box:4310", "k")], network, null, [Image]);

        var e = Assert.Throws<ChargehandException>(() => DrivenRunnerSet.ValidateRunners(null, [new DrivenRunnerEntry("far", "http://far:4310", "k", Workspace: WorkspaceMode.Bundle)], network, null, [Image]));

        Assert.Contains("runners.far: workspace is bundle", e.Message);
    }
}
