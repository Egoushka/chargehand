using System.Text.Json;
using Chargehand.Contracts;

namespace Chargehand.Kernel.Tests;

/// <summary>ADR 0069, ADR 0060: the profile's <c>delivery</c> binds without reflection, and its roots are checked before a watched checkout can sit under one.</summary>
public class DeliverySettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("delivery-settings-").FullName;

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Delivery_binds_from_the_profile_shape_and_makes_the_policy()
    {
        var settings = JsonSerializer.Deserialize("""{ "local_only": ["~/private", "/srv/internal"] }""", KernelSettingsJsonContext.Default.DeliverySettings)!;

        Assert.Equal(["~/private", "/srv/internal"], settings.LocalOnly);
        Assert.True(settings.Policy.Forced("/srv/internal/api"));
        Assert.False(new DeliverySettings().Policy.Forced("/srv/internal/api"));
    }

    [Fact]
    public void A_relative_root_is_refused_by_name()
    {
        var e = Assert.Throws<ChargehandException>(() => new DeliverySettings(["relative/dir"]).Validate([]));

        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.Contains("delivery.local_only: 'relative/dir'", e.Message);
    }

    [Fact]
    public void A_watched_checkout_under_a_root_is_refused_and_one_beside_it_is_not()
    {
        var root = Directory.CreateDirectory(Path.Combine(_dir, "local")).FullName;
        var under = Directory.CreateDirectory(Path.Combine(root, "api")).FullName;
        var beside = Directory.CreateDirectory(Path.Combine(_dir, "shared", "api")).FullName;
        var settings = new DeliverySettings([root]);

        settings.Validate([("automation.github.repositories", "shared", beside)]);
        var e = Assert.Throws<ChargehandException>(() => settings.Validate([("automation.github.repositories", "api", under)]));

        Assert.StartsWith("automation.github.repositories.api.path is under delivery.local_only", e.Message);
    }
}
