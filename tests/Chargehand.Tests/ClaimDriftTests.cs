using Chargehand.Automation;

namespace Chargehand.Tests;

/// <summary>The claim-drift watch (specs/2026-11-11-claim-drift-watch.md): the marker syntax and the check that what a guide page cites still exists.</summary>
public class ClaimDriftTests
{
    private const string Page = "docs/guide/x.md";

    private static IReadOnlyList<DocClaim> Parse(string markdown) => ClaimMarkers.Parse(Page, markdown);

    private static TempDir Repo(string guide)
    {
        var dir = new TempDir();
        dir.Write("src/A.cs", "public class Alpha { const string Cap = \"max 4\"; }\n");
        dir.Write("tests/AlphaTests.cs", "public class AlphaTests { public void Runs_fine() { } }\n");
        dir.Write(Page, guide);
        return dir;
    }

    [Fact]
    public void Marker_in_a_code_fence_is_not_a_claim()
    {
        Assert.Empty(Parse("```\n<!-- cites: src/Gone.cs -->\n```\n~~~\n<!-- cites: src/Gone.cs#X -->\n~~~\n"));
    }

    [Fact]
    public void Three_forms_parse_at_the_end_of_a_line_and_on_their_own_line()
    {
        var claims = Parse("Text. <!-- cites: src/A.cs#Alpha -->\n\nMore.\n<!-- cites: src/A.cs -->\n<!-- cites-text: src/A.cs \"max 4\" -->\n");
        Assert.Collection(claims,
            c => Assert.Equal((ClaimKind.Symbol, "src/A.cs", "Alpha", 1), (c.Kind, c.Path, c.Symbol, c.Line)),
            c => Assert.Equal((ClaimKind.File, "src/A.cs", 4), (c.Kind, c.Path, c.Line)),
            c => Assert.Equal((ClaimKind.Text, "src/A.cs", "max 4", 5), (c.Kind, c.Path, c.Literal, c.Line)));
    }

    [Theory]
    [InlineData("<!-- cites: -->")]
    [InlineData("<!-- cites: src/A.cs# -->")]
    [InlineData("<!-- cites-text: src/A.cs -->")]
    [InlineData("<!-- cites: ../secret.txt -->")]
    [InlineData("<!-- cites: /etc/passwd -->")]
    [InlineData("<!-- cites: src\\A.cs -->")]
    public void Malformed_or_unsafe_marker_is_a_malformed_claim(string marker)
    {
        Assert.Equal(ClaimKind.Malformed, Assert.Single(Parse(marker)).Kind);
    }

    [Fact]
    public void Seeded_stale_claim_is_reported()
    {
        using var dir = Repo("A claim. <!-- cites: src/A.cs#Beta -->\n");
        var finding = Assert.Single(ClaimDrift.Check(ClaimDrift.Scan(dir.Path), dir.Path));
        Assert.Equal("docs/guide/x.md:1: symbol-missing src/A.cs#Beta", finding.Line);
    }

    [Fact]
    public void Live_claims_of_every_form_pass()
    {
        using var dir = Repo("One. <!-- cites: src/A.cs#Alpha -->\nTwo. <!-- cites: src/A.cs -->\nThree. <!-- cites-text: src/A.cs \"max 4\" -->\nFour. <!-- cites: tests/AlphaTests.cs#Runs_fine -->\n");
        Assert.Empty(ClaimDrift.Check(ClaimDrift.Scan(dir.Path), dir.Path, ClaimDrift.TestNames("AlphaTests.Runs_fine\n")));
    }

    [Fact]
    public void Each_reason_has_a_case()
    {
        using var dir = Repo("""
            a <!-- cites: src/Gone.cs -->
            b <!-- cites: src/A.cs#Beta -->
            c <!-- cites-text: src/A.cs "max 9" -->
            d <!-- cites: tests/AlphaTests.cs#Runs_fine -->
            e <!-- cites: -->
            f <!-- cites: ../out.txt -->
            """);
        var findings = ClaimDrift.Check(ClaimDrift.Scan(dir.Path), dir.Path, ClaimDrift.TestNames("Other.Name\n"));
        Assert.Equal(["file-missing", "symbol-missing", "text-missing", "test-missing", "malformed", "malformed"], findings.Select(f => f.ReasonName));
        Assert.Contains("docs/guide/x.md:3: text-missing src/A.cs \"max 9\"", findings.Select(f => f.Line));
    }

    [Fact]
    public void Test_names_come_from_a_list_tests_listing()
    {
        var names = ClaimDrift.TestNames("The following Tests are available:\n    Runs_fine\n    Theory_case(x: 1)\nNs.Class.Qualified\n");
        Assert.Contains("Runs_fine", names);
        Assert.Contains("Theory_case", names);
        Assert.Contains("Qualified", names);
    }
}
