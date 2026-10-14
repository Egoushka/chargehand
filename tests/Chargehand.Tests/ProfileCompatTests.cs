using System.Text.Json;
using System.Text.Json.Serialization;
using Chargehand.Config;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Modules.Batches.Api;
using Chargehand.Modules.Forge.Api;
using Chargehand.Modules.Handover.Api;
using Chargehand.Modules.Intake.Api;
using Chargehand.Modules.Ledger.Api;
using Chargehand.Modules.Memory.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Runners.Api;
using Chargehand.Runtimes.ClaudeCode.Api;
using Chargehand.Runtimes.OpenCode.Api;

namespace Chargehand.Tests;

/// <summary>The repository's profiles load to the settings recorded under <c>ProfileCompat/</c> (ADR 0060): moving a settings type into the module
/// that reads it changes no key, default or value. The snapshots were taken before the first type moved; a deliberate change to the profile rewrites them.</summary>
public class ProfileCompatTests
{
    private static readonly JsonSerializerOptions Snapshot = new(Profile.Json) { WriteIndented = true };

    /// <summary>The same snapshot written through the source-generated contexts: their metadata matches reflection's key for key.</summary>
    private static readonly JsonSerializerOptions SourceGenerated = new(Profile.LoadJson) { WriteIndented = true };

    [Theory]
    [InlineData("profiles/example.json")]
    [InlineData("profiles/opencode.example.json")]
    [InlineData("tests/Chargehand.Tests/ProfileCompat/inputs/full.json")]
    [InlineData("tests/Chargehand.Tests/ProfileCompat/inputs/intake.json")]
    [InlineData("tests/Chargehand.Tests/ProfileCompat/inputs/runners.json")]
    [InlineData("tests/Chargehand.Tests/ProfileCompat/inputs/runner.json")]
    public void A_profile_loads_as_its_snapshot(string profile)
    {
        var settings = Profile.Load(Repo.Path(profile.Split('/')));
        var snapshot = File.ReadAllText(Repo.Path("tests", "Chargehand.Tests", "ProfileCompat", Path.GetFileName(profile)));
        Assert.Equal(snapshot, JsonSerializer.Serialize(settings, Snapshot));
        Assert.Equal(snapshot, JsonSerializer.Serialize(settings, SourceGenerated.GetTypeInfo(typeof(Profile))));
    }

    [Theory]
    [InlineData(typeof(IReadOnlyDictionary<string, ModelPrice>), typeof(LedgerSettingsJsonContext))]
    [InlineData(typeof(IReadOnlyList<AccountSettings>), typeof(AccountsSettingsJsonContext))]
    [InlineData(typeof(List<MemoryProviderSettings>), typeof(MemorySettingsJsonContext))]
    [InlineData(typeof(DrivenForge), typeof(ForgeSettingsJsonContext))]
    [InlineData(typeof(DrivenIntegrator), typeof(HandoverSettingsJsonContext))]
    [InlineData(typeof(AutomationSettings), typeof(IntakeSettingsJsonContext))]
    [InlineData(typeof(DrivenTaskSource), typeof(IntakeSettingsJsonContext))]
    [InlineData(typeof(DrivenPlanning), typeof(BatchesSettingsJsonContext))]
    [InlineData(typeof(DrivenGovernor), typeof(OversightSettingsJsonContext))]
    [InlineData(typeof(DrivenLearning), typeof(OversightSettingsJsonContext))]
    [InlineData(typeof(DrivenAdmission), typeof(RunnersSettingsJsonContext))]
    [InlineData(typeof(DrivenRunner), typeof(RunnersSettingsJsonContext))]
    [InlineData(typeof(IReadOnlyList<DrivenRunnerEntry>), typeof(RunnersSettingsJsonContext))]
    [InlineData(typeof(OpenCodeSettings), typeof(OpenCodeSettingsJsonContext))]
    [InlineData(typeof(ClaudeCodeSettings), typeof(ClaudeCodeSettingsJsonContext))]
    [InlineData(typeof(Profile), typeof(ProfileJsonContext))]
    [InlineData(typeof(DrivenSettings), typeof(ProfileJsonContext))]
    [InlineData(typeof(McpServerSettings), typeof(ProfileJsonContext))]
    public void A_modules_section_is_bound_by_the_modules_source_generated_context(Type section, Type context) =>
        Assert.IsType(context, Profile.LoadJson.GetTypeInfo(section).OriginatingResolver);

    [Fact]
    public void The_profile_loads_without_reflection() =>
        Assert.All(Profile.LoadJson.TypeInfoResolverChain, resolver => Assert.IsAssignableFrom<JsonSerializerContext>(resolver));
}
