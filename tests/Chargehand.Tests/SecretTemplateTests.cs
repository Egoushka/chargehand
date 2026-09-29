using Chargehand.Mcp;

namespace Chargehand.Tests;

/// <summary>Goal 0.6: {secret:item} in MCP server headers and environment values, resolved through the profile's chain.</summary>
public class SecretTemplateTests
{
    private static string Secret(string item) =>
        item == "tok" ? "s3cret" : throw new InvalidOperationException($"no secret source resolved '{item}'");

    [Fact]
    public void Every_placeholder_is_replaced() =>
        Assert.Equal("Bearer s3cret and s3cret", SecretTemplate.Resolve("Bearer {secret:tok} and {secret:tok}", Secret));

    [Fact]
    public void Text_without_a_placeholder_passes_through() =>
        Assert.Equal("plain {not:a-secret}", SecretTemplate.Resolve("plain {not:a-secret}", Secret));

    [Fact]
    public void A_resolved_value_is_not_scanned_for_placeholders() =>
        Assert.Equal("{secret:tok}", SecretTemplate.Resolve("{secret:other}", _ => "{secret:tok}"));

    [Fact]
    public void An_unresolved_item_propagates_naming_the_item_only()
    {
        var e = Assert.Throws<InvalidOperationException>(() => SecretTemplate.Resolve("Bearer {secret:missing}", Secret));
        Assert.Contains("missing", e.Message);
        Assert.DoesNotContain("s3cret", e.Message);
    }

    [Theory]
    [InlineData("{secret:tok}", true)]
    [InlineData("a {secret:tok} b", true)]
    [InlineData("{query}", false)]
    public void HasSecret_finds_placeholders(string text, bool expected) => Assert.Equal(expected, SecretTemplate.HasSecret(text));
}
