namespace Chargehand.Tests;

/// <summary>The verbs that are dispatched before the profile loads, and one that is hidden, must be wired in <c>Program.cs</c>: a verb that is only defined in a
/// library prints the usage and exits 2, and nothing else notices (ADR 0039's session image found out that way).</summary>
public class CliVerbsTests
{
    [Theory]
    [InlineData("verify")]
    [InlineData("egress")]
    [InlineData("runner")]
    [InlineData("session")]
    [InlineData("verify-branch")]
    public void A_profile_free_verb_is_dispatched_in_the_cli(string verb)
    {
        var source = File.ReadAllText(Repo.Path("src", "Chargehand.Cli", "Program.cs"));
        Assert.Contains($"argv is [\"{verb}\"", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("egress")]
    [InlineData("runner")]
    [InlineData("runs")]
    public void A_verb_a_person_types_is_in_the_usage(string verb)
    {
        var source = File.ReadAllText(Repo.Path("src", "Chargehand.Cli", "Program.cs"));
        Assert.Contains($"      {verb} ", source, StringComparison.Ordinal);
    }
}
