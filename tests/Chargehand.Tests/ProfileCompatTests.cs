using System.Text.Json;
using Chargehand.Config;

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
    public void A_profile_loads_as_its_snapshot(string profile)
    {
        var loaded = JsonSerializer.Serialize(Profile.Load(Repo.Path(profile.Split('/'))), Snapshot);
        Assert.Equal(File.ReadAllText(Repo.Path("tests", "Chargehand.Tests", "ProfileCompat", Path.GetFileName(profile))), loaded);
    }
}
