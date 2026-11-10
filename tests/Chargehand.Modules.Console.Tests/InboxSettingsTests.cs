using System.Text.Json;
using Chargehand.Modules.Console.Api;

namespace Chargehand.Modules.Console.Tests;

/// <summary>ADR 0085, ADR 0060: <c>inbox</c> binds without reflection, and the first owner glob that matches the repository wins.</summary>
public sealed class InboxSettingsTests
{
    [Fact]
    public void Inbox_binds_from_the_profile_shape_in_the_files_order()
    {
        var inbox = JsonSerializer.Deserialize("""{ "owners": { "o/a": "infra desk", "o/*": "web desk" } }""", ConsoleSettingsJsonContext.Default.InboxSettings)!;

        Assert.Equal(["o/a", "o/*"], inbox.Owners!.Keys);
        Assert.Equal("infra desk", inbox.OwnerOf("o/a"));
        Assert.Equal("web desk", inbox.OwnerOf("o/b"));
    }

    [Theory]
    [InlineData("/srv/checkouts/o/a", "infra desk")]
    [InlineData("O/A", "infra desk")]
    [InlineData("/srv/checkouts/o/a/", "infra desk")]
    [InlineData("other/a", null)]
    [InlineData(null, null)]
    public void A_path_or_a_slug_is_matched_by_its_last_two_segments_case_insensitively(string? repository, string? owner) =>
        Assert.Equal(owner, new InboxSettings(new Dictionary<string, string> { ["o/a"] = "infra desk" }).OwnerOf(repository));

    [Fact]
    public void No_owners_means_no_owner() => Assert.Null(new InboxSettings().OwnerOf("o/a"));
}
