using Chargehand.Kernel;
using Chargehand.Modules.Handover.Api;

namespace Chargehand.Modules.Handover.Tests;

/// <summary>CHARGEHAND-426: chargehand commits as the GitHub App's bot noreply identity once one is configured, since the default <c>chargehand@localhost</c> fails a repository's
/// secrets/noreply CI check.</summary>
public class DrivenIntegratorTests
{
    [Fact]
    public void No_integrator_commits_as_chargehand()
    {
        var (name, email) = DrivenIntegrator.CommitIdentity(null);
        Assert.Equal(("chargehand", "chargehand@localhost"), (name, email));
    }

    [Fact]
    public void An_integrator_without_an_app_slug_still_commits_as_chargehand()
    {
        var (name, email) = DrivenIntegrator.CommitIdentity(new DrivenIntegrator());
        Assert.Equal(("chargehand", "chargehand@localhost"), (name, email));
    }

    [Fact]
    public void An_app_slug_gives_the_apps_bot_noreply_identity()
    {
        var (name, email) = DrivenIntegrator.CommitIdentity(new DrivenIntegrator(AppSlug: "egoushka-release"));
        Assert.Equal(("egoushka-release[bot]", "egoushka-release[bot]@users.noreply.github.com"), (name, email));
    }

    [Fact]
    public void An_app_id_without_an_app_slug_fails_validation()
    {
        var e = Assert.Throws<ChargehandException>(() => new DrivenIntegrator(AppId: 1, InstallationId: 2, PrivateKeySecret: "k").Validate());
        Assert.Contains("app_slug must be the App's slug", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fully_configured_app_validates()
    {
        new DrivenIntegrator(AppId: 1, InstallationId: 2, PrivateKeySecret: "k", AppSlug: "egoushka-release").Validate();
    }
}
