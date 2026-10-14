using System.Text.Json;
using Chargehand.Config;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Memory.Api;

namespace Chargehand.Tests;

/// <summary>The repository's profiles load to the settings recorded under <c>ProfileCompat/</c> (ADR 0060): moving a settings type into the module
/// that reads it changes no key, default or value. The snapshots were taken before the first type moved; a deliberate change to the profile rewrites them.</summary>
public class ProfileCompatTests
{
    private static readonly JsonSerializerOptions Snapshot = new(Profile.Json) { WriteIndented = true };

    [Theory]
    [InlineData("profiles/example.json")]
    [InlineData("profiles/opencode.example.json")]
    [InlineData("tests/Chargehand.Tests/ProfileCompat/inputs/full.json")]
    [InlineData("tests/Chargehand.Tests/ProfileCompat/inputs/intake.json")]
    [InlineData("tests/Chargehand.Tests/ProfileCompat/inputs/runners.json")]
    [InlineData("tests/Chargehand.Tests/ProfileCompat/inputs/runner.json")]
    public void A_profile_loads_as_its_snapshot(string profile)
    {
        var loaded = JsonSerializer.Serialize(Profile.Load(Repo.Path(profile.Split('/'))), Snapshot);
        Assert.Equal(File.ReadAllText(Repo.Path("tests", "Chargehand.Tests", "ProfileCompat", Path.GetFileName(profile))), loaded);
    }

    [Theory]
    [InlineData(typeof(IReadOnlyDictionary<string, ModelPrice>), typeof(LedgerSettingsJsonContext))]
    [InlineData(typeof(IReadOnlyList<AccountSettings>), typeof(AccountsSettingsJsonContext))]
    [InlineData(typeof(List<MemoryProviderSettings>), typeof(MemorySettingsJsonContext))]
    [InlineData(typeof(DrivenForge), typeof(ForgeSettingsJsonContext))]
    public void A_modules_section_is_bound_by_the_modules_source_generated_context(Type section, Type context) =>
        Assert.IsType(context, Profile.LoadJson.GetTypeInfo(section).OriginatingResolver);
}
