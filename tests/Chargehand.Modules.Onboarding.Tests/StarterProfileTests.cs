using System.Text.Json;
using Chargehand.Modules.Onboarding.Api;

namespace Chargehand.Modules.Onboarding.Tests;

public class StarterProfileTests
{
    [Fact]
    public void Rendering_twice_gives_the_same_text()
    {
        Assert.Equal(StarterProfile.Render("/work/repo", ["a.example"]), StarterProfile.Render("/work/repo", ["a.example"]));
    }

    [Fact]
    public void The_profile_names_secrets_by_item_and_lists_the_hosts_and_the_root()
    {
        var text = StarterProfile.Render("/work/repo", ["a.example", "b.example"]);

        Assert.EndsWith("}\n", text);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        Assert.Equal("profile/v1", root.GetProperty("schema").GetString());
        Assert.Equal(["/work/repo"], root.GetProperty("repository_roots").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(StarterProfile.ApiKeyItem, root.GetProperty("http").GetProperty("api_key_secret").GetString());
        Assert.Equal(StarterProfile.PushItem, root.GetProperty("driven").GetProperty("push_secret").GetString());
        Assert.False(root.GetProperty("driven").GetProperty("enabled").GetBoolean());
        Assert.Equal(["a.example", "b.example"], root.GetProperty("driven").GetProperty("network").GetProperty("allow").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Write_creates_the_directory_and_leaves_an_existing_file_alone_unless_forced()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "nested", "local.json");

            Assert.Equal(WriteOutcome.Written, StarterProfile.Write(path, "one", force: false));
            Assert.Equal(WriteOutcome.Exists, StarterProfile.Write(path, "two", force: false));
            Assert.Equal("one", File.ReadAllText(path));
            Assert.Equal(WriteOutcome.Overwritten, StarterProfile.Write(path, "three", force: true));
            Assert.Equal("three", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }
}
